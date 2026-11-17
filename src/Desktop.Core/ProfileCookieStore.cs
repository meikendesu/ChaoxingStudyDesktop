using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Desktop.Core;

// 配置切换时的完整 Cookie 快照（含持久 Cookie 和 Expires），与原有会话恢复文件互不干扰。
public sealed record SavedProfileCookie(string Name, string Value, string Domain, string Path,
    bool Secure, bool HttpOnly, int SameSite, DateTime Expires);

public sealed class ProfileCookieStore(string root)
{
    private string FilePath => Path.Combine(root, "profile-cookies.bin");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ChaoxingStudyDesktop:profile-cookies:v1");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public bool Exists => File.Exists(FilePath);

    public void Save(IEnumerable<SavedProfileCookie> cookies)
    {
        var items = cookies.Where(c => SessionCookieStore.IsPlatformDomain(c.Domain)).Take(1000).ToArray();
        if (items.Length == 0) { Clear(); return; }
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Envelope(DateTimeOffset.UtcNow, items), Options);
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(root);
            File.WriteAllBytes(FilePath + ".tmp", encrypted);
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public SavedProfileCookie[] Read()
    {
        if (!Exists) return [];
        if (new FileInfo(FilePath).Length > 4 * 1024 * 1024) throw new IOException("配置 Cookie 快照过大。");
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var saved = JsonSerializer.Deserialize<Envelope>(plain, Options);
            // 平台自身的过期时间才决定登录是否仍有效；过期项由浏览器自行丢弃。
            return saved?.Cookies.Where(c => SessionCookieStore.IsPlatformDomain(c.Domain)).ToArray() ?? [];
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Clear() => File.Delete(FilePath);

    private sealed record Envelope(DateTimeOffset SavedAt, SavedProfileCookie[] Cookies);
}
