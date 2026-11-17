using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Desktop.Core;

public static class BrowserRuntime
{
    // 项目目录可能继承低完整性/受限权限，固定版浏览器改在本机专用缓存中运行。
    // 文件仍来自分发包，不下载运行时，也不接触 BrowserProfile/Cookie。
    public static string? Prepare(string executableDirectory, string cacheDirectory)
    {
        var source = Resolve(executableDirectory);
        if (source is null) return null;
        var manifestText = File.ReadAllText(Path.Combine(source, "runtime-manifest.json"));
        var manifest = JsonSerializer.Deserialize<RuntimeManifest>(manifestText);
        if (manifest?.Files is not { Length: > 0 } || !manifest.Files.Any(file => file.Path == "msedgewebview2.exe"))
            throw new InvalidDataException("内置 WebView2 校验清单无效，请重新解压部署包。");
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestText)));
        var destination = Path.GetFullPath(Path.Combine(cacheDirectory, identity));
        // 在写入之前校验全部路径，拒绝清单越界。
        foreach (var file in manifest.Files)
        {
            ValidatePath(source, file.Path);
            ValidatePath(destination, file.Path);
        }
        Directory.CreateDirectory(destination);
        GrantRuntimeReadAccess(destination);
        foreach (var file in manifest.Files)
        {
            var target = ValidatePath(destination, file.Path);
            if (File.Exists(target) && HasHash(target, file.SHA256)) continue;
            var original = ValidatePath(source, file.Path);
            if (!HasHash(original, file.SHA256)) throw new InvalidDataException("内置 WebView2 文件校验失败，请重新解压完整部署包。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // 复制后继承本机目录权限，不沿用部署目录的受限权限。
            File.Copy(original, target, overwrite: true);
            if (!HasHash(target, file.SHA256)) throw new IOException("WebView2 缓存复制未通过校验。");
        }
        var cachedManifest = Path.Combine(destination, "runtime-manifest.json");
        if (!File.Exists(cachedManifest) || File.ReadAllText(cachedManifest) != manifestText)
            File.WriteAllText(cachedManifest, manifestText);
        return destination;
    }

    private static bool HasHash(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidatePath(string directory, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(directory, relative));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("WebView2 校验清单路径越界。");
        return path;
    }

    private static void GrantRuntimeReadAccess(string directory)
    {
        var info = new DirectoryInfo(directory);
        var access = info.GetAccessControl();
        var rules = access.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var changed = false;
        foreach (var sid in new[] { "S-1-15-2-1", "S-1-15-2-2" })
        {
            // 已配置好的缓存只读复用，避免受限启动时重复写入相同 ACL。
            if (rules.Any(rule => rule.IdentityReference.Value == sid && rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute
                && (rule.InheritanceFlags & inheritance) == inheritance && rule.PropagationFlags == PropagationFlags.None)) continue;
            access.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.ReadAndExecute,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
            changed = true;
        }
        if (changed) info.SetAccessControl(access);
    }

    private sealed record RuntimeFile(string Path, string SHA256);
    private sealed record RuntimeManifest(RuntimeFile[] Files);

    public static string? Resolve(string executableDirectory)
    {
        var directory = Path.GetFullPath(Path.Combine(executableDirectory, "WebView2Runtime"));
        if (!Directory.Exists(directory)) return null;
        if (!File.Exists(Path.Combine(directory, "msedgewebview2.exe")))
            throw new FileNotFoundException("内置 WebView2 运行时不完整，请重新解压完整部署包。", directory);
        return directory;
    }
}
