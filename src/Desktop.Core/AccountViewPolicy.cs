namespace Desktop.Core;

// 一个账户实例的使用情况，供实例池做 LRU 淘汰；不含任何 WPF / WebView2 对象。
public sealed record AccountViewUsage(string Id, DateTime LastActivated, bool Busy);

// 多账户 WebView 实例池的纯逻辑：命名 Profile、实例上限、淘汰候选与清单差异。
// 放在 Core 层是为了与 UI 解耦，方便离线单元测试。
public static class AccountViewPolicy
{
    // 同时保留的 WebView 实例上限。超出时释放最久未使用的后台实例，避免内存随账户数线性增长。
    public const int MaxInstances = 6;
    private const string Prefix = "cx-";

    // 默认账户返回 null：继续沿用浏览器的默认 profile，保证已有登录资料不被迁移或丢失。
    // 其余账户各自使用同一 userDataFolder 下的命名 profile，会话天然隔离且共享浏览器进程资源。
    public static string? ProfileNameOf(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !ProfileRegistry.IsValidId(id))
            throw new ArgumentException("账户标识无效。", nameof(id));
        if (id == ProfileRegistry.DefaultId) return null;
        var name = Prefix + id;
        if (name.Length > 64 || name.Any(c => !IsProfileChar(c)))
            throw new ArgumentException("账户标识包含命名 Profile 不支持的字符。", nameof(id));
        return name;
    }

    // WebView2 ProfileName 允许的 ASCII 字符：字母、数字与 # @ $ ( ) + - _ ~ . 空格。
    private static bool IsProfileChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
            or '#' or '@' or '$' or '(' or ')' or '+' or '-' or '_' or '~' or '.' or ' ';

    public static bool NeedsTrim(int count, int max = MaxInstances) => count >= max;

    // 只淘汰「非当前、非正在加载」的最久未激活实例；找不到时返回 null，由调用方拒绝超限创建。
    public static string? EvictionCandidate(IEnumerable<AccountViewUsage> usage, string activeId)
        => usage.Where(item => item.Id != activeId && !item.Busy)
            .OrderBy(item => item.LastActivated).ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.Id).FirstOrDefault();

    // 清单中已不存在、但实例池仍持有的账户，用于配置删除后的实例同步。
    public static string[] Stale(IEnumerable<string> instanceIds, IEnumerable<string> profileIds)
    {
        var known = profileIds.ToHashSet(StringComparer.Ordinal);
        return instanceIds.Where(id => !known.Contains(id)).Distinct(StringComparer.Ordinal).ToArray();
    }

    // 清单中存在、但尚未建立实例的账户，用于配置新增后的后台预热。
    public static string[] Missing(IEnumerable<string> profileIds, IEnumerable<string> instanceIds)
    {
        var existing = instanceIds.ToHashSet(StringComparer.Ordinal);
        return profileIds.Where(id => !existing.Contains(id)).Distinct(StringComparer.Ordinal).ToArray();
    }
}
