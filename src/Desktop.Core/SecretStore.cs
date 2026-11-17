using System.Security.Cryptography;
using System.Text;

namespace Desktop.Core;

public sealed class SecretStore(string root)
{
    private string GetPath(string provider)
    {
        if (!ProviderCatalog.Contains(provider)) throw new ArgumentException("未知模型提供商。", nameof(provider));
        return Path.Combine(root, "secrets", provider + ".bin");
    }

    private static byte[] Entropy(string provider) => Encoding.UTF8.GetBytes("ChaoxingStudyDesktop:key:" + provider);

    public void Save(string provider, string value)
    {
        var path = GetPath(provider);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192) throw new ArgumentException("密钥不能为空或过长。", nameof(value));
        var plain = Encoding.UTF8.GetBytes(value.Trim());
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy(provider), DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path + ".tmp", encrypted);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public bool HasKey(string provider) => File.Exists(GetPath(provider));

    public string? Read(string provider)
    {
        var path = GetPath(provider);
        if (!File.Exists(path)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy(provider), DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Delete(string provider) => File.Delete(GetPath(provider));
}
