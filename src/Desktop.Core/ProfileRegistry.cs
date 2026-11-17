using System.Text.Json;
using System.Text.RegularExpressions;

namespace Desktop.Core;

// 一个账号配置：拥有独立的 settings.json / task.json / session-cookies.bin。
// 默认配置的目录就是数据根目录本身，因此原有资料与文件路径保持不变。
public sealed record ProfileEntry(string Id, string Name, DateTimeOffset CreatedAt, bool IsDefault = false);

public sealed record ProfileLoadResult(ProfileRegistry Registry, string? Warning);

public sealed class ProfileRegistry
{
    public const string DefaultId = "default";
    private const int MaxProfiles = 24;
    private const int MaxNameLength = 24;
    private readonly string root;
    private readonly string manifestPath;
    private readonly List<ProfileEntry> entries;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private ProfileRegistry(string root, List<ProfileEntry> entries, string currentId)
    {
        this.root = root;
        this.entries = entries;
        manifestPath = Path.Combine(root, "profiles.json");
        CurrentId = currentId;
    }

    public IReadOnlyList<ProfileEntry> Profiles => entries;
    public string CurrentId { get; private set; }
    public ProfileEntry Current => entries.First(entry => entry.Id == CurrentId);
    public string Root => root;
    public string CurrentDirectory => DirectoryOf(CurrentId);

    // 默认配置沿用根目录；其余配置位于 root/configurations/<id>，不与其他配置共用文件。
    public string DirectoryOf(string id)
    {
        if (!IsValidId(id)) throw new ArgumentException("未知配置。", nameof(id));
        return id == DefaultId ? root : Path.Combine(root, "configurations", id);
    }

    // 供实例池生成命名 profile 前复用同一套标识规则，避免两处规则漂移。
    public static bool IsValidId(string id) => id == DefaultId || Regex.IsMatch(id, "^p[0-9a-f]{12}$");

    public static ProfileLoadResult Load(string root)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "profiles.json");
        if (!File.Exists(path))
        {
            // 首次运行：把根目录已有资料视为默认配置，不移动、不覆盖任何原文件。
            var initial = new ProfileRegistry(root,
                [new ProfileEntry(DefaultId, "默认配置", DateTimeOffset.Now, IsDefault: true)], DefaultId);
            initial.Save();
            return new(initial, null);
        }
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Options);
            var list = (manifest?.Profiles ?? [])
                .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Id) &&
                    IsValidId(entry.Id!) && !string.IsNullOrWhiteSpace(entry.Name))
                .Select(entry => new ProfileEntry(entry.Id!, entry.Name!.Trim(), entry.CreatedAt, entry.Id == DefaultId))
                .GroupBy(entry => entry.Id).Select(group => group.First()).Take(MaxProfiles).ToList();
            if (list.Count == 0 || list.All(entry => entry.Id != DefaultId))
                list.Insert(0, new ProfileEntry(DefaultId, "默认配置", DateTimeOffset.Now, IsDefault: true));
            var current = list.Any(entry => entry.Id == manifest?.CurrentId) ? manifest!.CurrentId! : DefaultId;
            return new(new ProfileRegistry(root, list, current), null);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // 与设置文件一致的策略：损坏的清单保留原文件并提示，显式保存后才覆盖。
            return new(new ProfileRegistry(root,
                [new ProfileEntry(DefaultId, "默认配置", DateTimeOffset.Now, IsDefault: true)], DefaultId),
                "配置清单无法读取，已使用默认配置；原文件保留，保存后才会覆盖。");
        }
    }

    public ProfileEntry Create(string name)
    {
        if (entries.Count >= MaxProfiles) throw new InvalidOperationException($"最多只能创建 {MaxProfiles} 个配置。");
        name = NormalizeName(name);
        if (entries.Any(entry => entry.Name == name)) throw new InvalidOperationException("已存在同名配置，请换一个名称。");
        var id = "p" + Guid.NewGuid().ToString("N")[..12];
        var entry = new ProfileEntry(id, name, DateTimeOffset.Now);
        Directory.CreateDirectory(DirectoryOf(id));
        File.WriteAllText(Path.Combine(DirectoryOf(id), "profile.json"), JsonSerializer.Serialize(entry, Options));
        entries.Add(entry);
        Save();
        return entry;
    }

    public void Rename(string id, string name)
    {
        var index = entries.FindIndex(entry => entry.Id == id);
        if (index < 0) throw new ArgumentException("未知配置。", nameof(id));
        name = NormalizeName(name);
        if (entries.Any(entry => entry.Id != id && entry.Name == name)) throw new InvalidOperationException("已存在同名配置，请换一个名称。");
        var updated = entries[index] with { Name = name };
        entries[index] = updated;
        Save();
        if (id != DefaultId)
            File.WriteAllText(Path.Combine(DirectoryOf(id), "profile.json"), JsonSerializer.Serialize(updated, Options));
    }

    public void Select(string id)
    {
        if (!entries.Any(entry => entry.Id == id)) throw new ArgumentException("未知配置。", nameof(id));
        CurrentId = id;
        Save();
    }

    // 默认配置不可删除，保证根目录原资料与浏览器共享目录始终可用。
    public void Delete(string id)
    {
        if (id == DefaultId) throw new InvalidOperationException("默认配置不能删除。");
        var entry = entries.FirstOrDefault(item => item.Id == id) ?? throw new ArgumentException("未知配置。", nameof(id));
        var directory = DirectoryOf(id);
        var parent = Path.Combine(root, "configurations");
        if (!directory.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("配置目录越界，未执行删除。");
        if (Directory.Exists(directory) && !ContainsLink(directory))
            Directory.Delete(directory, recursive: true);
        entries.Remove(entry);
        if (CurrentId == id) CurrentId = DefaultId;
        Save();
    }

    // 逐层检查，避免跟随链接遍历到配置目录之外。
    private static bool ContainsLink(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            foreach (var item in Directory.EnumerateFileSystemEntries(current))
            {
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) return true;
                if (Directory.Exists(item)) pending.Push(item);
            }
        }
        return false;
    }

    private static string NormalizeName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > MaxNameLength || name.Any(char.IsControl) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Any(c => c is '\\' or '/'))
            throw new InvalidOperationException($"配置名称需为 1–{MaxNameLength} 个字符，且不能包含路径符号或控制字符。");
        return name;
    }

    private void Save()
    {
        var temporary = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var items = entries.Select(entry => new ProfileItem(entry.Id, entry.Name, entry.CreatedAt)).ToArray();
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Manifest(CurrentId, items), Options));
            File.Move(temporary, manifestPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // 清单使用可空字段的独立形状，损坏或缺失字段时按默认配置回退。
    private sealed record Manifest(string? CurrentId, ProfileItem[]? Profiles);
    private sealed record ProfileItem(string? Id, string? Name, DateTimeOffset CreatedAt);
}
