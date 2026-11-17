namespace Desktop.Core;

public static class UserDataPaths
{
    public static string Fallback(string executableDirectory)
    {
        var cursor = new DirectoryInfo(Path.GetFullPath(executableDirectory));
        while (cursor is not null)
        {
            if (File.Exists(Path.Combine(cursor.FullName, "ChaoxingStudyDesktop.slnx")))
                return Path.Combine(cursor.FullName, "artifacts", "client-data");
            cursor = cursor.Parent;
        }
        return Path.Combine(executableDirectory, "data");
    }

    public static void MigrateLegacy(string destination)
    {
        // 仅首次建立项目固定目录时迁移；后续启动绝不覆盖已使用的资料。
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination)) return;
        var parent = Path.GetDirectoryName(destination)!;
        var published = Path.Combine(parent, "publish");
        if (!Directory.Exists(published)) return;
        var source = Directory.EnumerateDirectories(published)
            .Select(p => Path.Combine(p, "data"))
            .Where(p => Directory.Exists(Path.Combine(p, "BrowserProfile")))
            .OrderByDescending(p => File.Exists(Path.Combine(p, "BrowserProfile", "Default", "Network", "Cookies"))
                ? File.GetLastWriteTimeUtc(Path.Combine(p, "BrowserProfile", "Default", "Network", "Cookies"))
                : Directory.GetLastWriteTimeUtc(Path.Combine(p, "BrowserProfile"))).FirstOrDefault();
        if (source is null) return;
        var staging = destination + ".migration-" + Guid.NewGuid().ToString("N");
        // staging 与 destination 都由同一固定目录生成，不移动或删除原发布包资料。
        CopyDirectory(source, staging);
        Directory.Move(staging, destination);
    }

    private static void CopyDirectory(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("资料目录包含链接，未自动迁移。");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var folder in Directory.EnumerateDirectories(source))
            CopyDirectory(folder, Path.Combine(destination, Path.GetFileName(folder)));
    }
}
