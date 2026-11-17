using System.Text.Json;

namespace Desktop.Core;

public sealed record TaskSnapshot
{
    public string CourseUrl { get; init; } = "";
    public string CourseId { get; init; } = "";
    public string ChapterId { get; init; } = "";
    public DateTimeOffset SavedAt { get; init; }
}

// 仅保存课程地址与标识，不含账号、Cookie、题干或答案，用于下次启动时提示恢复。
public sealed class TaskStateStore(string root)
{
    private readonly string path = Path.Combine(root, "task.json");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public TaskSnapshot? Load()
    {
        if (!File.Exists(path)) return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<TaskSnapshot>(File.ReadAllText(path), Options);
            return snapshot is { CourseUrl.Length: > 0 } ? snapshot : null;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    public void Save(TaskSnapshot snapshot)
    {
        if (snapshot.CourseUrl.Length == 0) return;
        Directory.CreateDirectory(root);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(snapshot, Options));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
