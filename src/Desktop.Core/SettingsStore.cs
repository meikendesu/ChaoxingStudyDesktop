using System.Text.Json;

namespace Desktop.Core;

public sealed record AppSettings
{
    public string SelectedProvider { get; init; } = "zhipu";
    public int AdvanceAtPercent { get; init; } = 91;
    public double PlaybackRate { get; init; } = 2.0;
    public bool AutoAdvance { get; init; } = true;
}

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning);

public sealed class SettingsStore(string root)
{
    private readonly string path = Path.Combine(root, "settings.json");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SettingsLoadResult Load()
    {
        if (!File.Exists(path)) return new(new AppSettings(), null);
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
            if (settings is null || !ProviderCatalog.Contains(settings.SelectedProvider) || settings.AdvanceAtPercent is < 1 or > 100 || settings.PlaybackRate is < 0.25 or > 16)
                return new(new AppSettings(), "设置内容无效，已使用默认值；原文件保留。保存设置后才会覆盖。");
            return new(settings, null);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(new AppSettings(), "设置无法读取，已使用默认值；原文件保留。保存设置后才会覆盖。");
        }
    }

    public void Save(AppSettings settings)
    {
        if (!ProviderCatalog.Contains(settings.SelectedProvider)) throw new ArgumentException("未知模型提供商。", nameof(settings));
        Directory.CreateDirectory(root);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings, Options));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
