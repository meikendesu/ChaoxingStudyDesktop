using System.Text.Json;

namespace Desktop.Core;

// 首次启动用户协议的持久化状态，按数据根目录保存，与账号配置无关。
// 协议版本变化时可再次提示，版本不变则视为用户已接受。
public sealed class AgreementStore(string root)
{
    public const string CurrentVersion = "1.0";
    public const string Developer = "MEIKEN";

    private readonly string path = Path.Combine(root, "agreement.json");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public bool IsAccepted()
    {
        if (!File.Exists(path)) return false;
        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path), Options);
            return state?.Accepted == true && state.Version == CurrentVersion;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // 状态文件损坏或不可读时按未接受处理，弹出协议由用户重新确认。
            return false;
        }
    }

    public void Accept()
    {
        Directory.CreateDirectory(root);
        var state = new State(true, CurrentVersion, Developer, DateTimeOffset.Now);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record State(bool Accepted, string? Version, string? Developer, DateTimeOffset? AcceptedAt);
}
