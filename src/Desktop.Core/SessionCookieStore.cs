using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Desktop.Core;

public sealed record SavedSessionCookie(string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly, int SameSite);

public sealed class SessionCookieStore(string root)
{
    private string FilePath => Path.Combine(root, "session-cookies.bin");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ChaoxingStudyDesktop:session-cookies:v1");
    public static bool IsPlatformDomain(string domain)
    {
        domain = domain.TrimStart('.').ToLowerInvariant();
        return domain is "chaoxing.com" or "xuexitong.com" || domain.EndsWith(".chaoxing.com", StringComparison.Ordinal) || domain.EndsWith(".xuexitong.com", StringComparison.Ordinal);
    }
    public void Save(IEnumerable<SavedSessionCookie> cookies)
    {
        var items = cookies.Where(c => IsPlatformDomain(c.Domain)).Take(1000).ToArray();
        if (items.Length == 0) { Clear(); return; }
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Envelope(DateTimeOffset.UtcNow, items));
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(root);
            File.WriteAllBytes(FilePath + ".tmp", encrypted);
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public SavedSessionCookie[] Read(DateTimeOffset? now = null)
    {
        if (!File.Exists(FilePath)) return [];
        if (new FileInfo(FilePath).Length > 4 * 1024 * 1024) throw new IOException("会话资料过大。");
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var saved = JsonSerializer.Deserialize<Envelope>(plain);
            if (saved is null || (now ?? DateTimeOffset.UtcNow) - saved.SavedAt > TimeSpan.FromDays(7)) return [];
            return saved.Cookies.Where(c => IsPlatformDomain(c.Domain)).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Clear() => File.Delete(FilePath);
    private sealed record Envelope(DateTimeOffset SavedAt, SavedSessionCookie[] Cookies);
}
