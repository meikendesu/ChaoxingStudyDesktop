using Desktop.Core;
using Xunit;

namespace Desktop.Core.Tests;

public sealed class EnvSettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    public EnvSettingsTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void Save_roundtrips_special_key_and_preserves_unrelated_lines()
    {
        var path = Path.Combine(root, ".env");
        File.WriteAllText(path, "# keep\nOTHER=value\nCX_AI_API_KEY=old\nCX_AI_API_KEY=duplicate\n");
        new AiConfiguration { Provider = "zhipu", ApiKey = "fake=key#'\"", Model = "test",
            Endpoint = "https://example.test/chat/completions" }.Save(path);
        var result = AiConfiguration.Load(root);
        Assert.Equal("fake=key#'\"", result.ApiKey);
        Assert.Contains("# keep", File.ReadAllText(path));
        Assert.Contains("OTHER=value", File.ReadAllText(path));
        Assert.Single(File.ReadLines(path), line => line.StartsWith("CX_AI_API_KEY="));
    }

    [Fact]
    public void Invalid_settings_do_not_overwrite_existing_file()
    {
        var path = Path.Combine(root, ".env");
        File.WriteAllText(path, "OTHER=keep");
        Assert.Throws<InvalidOperationException>(() => new AiConfiguration { ApiKey = "fake\nOTHER=bad" }.Save(path));
        Assert.Throws<InvalidOperationException>(() => new AiConfiguration { ApiKey = "fake", Model = "test", Endpoint = "http://example.test" }.Save(path));
        Assert.Equal("OTHER=keep", File.ReadAllText(path));
    }

    [Fact]
    public void Incomplete_local_file_reports_missing_fields_and_actual_path_without_key()
    {
        File.WriteAllText(Path.Combine(root, "ChaoxingStudyDesktop.slnx"), "");
        new AiConfiguration { Provider = "zhipu", ApiKey = "root-fake-key", Model = "root-model",
            Endpoint = "https://example.test/chat/completions" }.Save(Path.Combine(root, ".env"));
        var app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        var localPath = Path.Combine(app, ".env");
        File.WriteAllText(localPath, "CX_AI_PROVIDER=zhipu\nCX_AI_API_KEY=local-fake-key\n");
        var config = AiConfiguration.Load(app);
        var error = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Equal(localPath, config.ConfigPath);
        Assert.Equal("local-fake-key", config.ApiKey);
        Assert.Contains("CX_AI_MODEL", error.Message);
        Assert.Contains("CX_AI_ENDPOINT", error.Message);
        Assert.Contains(localPath, error.Message);
        Assert.DoesNotContain("local-fake-key", error.Message);
        Assert.DoesNotContain("root-fake-key", error.Message);
    }

    [Fact]
    public void Local_file_wins_over_project_and_is_reread()
    {
        File.WriteAllText(Path.Combine(root, "ChaoxingStudyDesktop.slnx"), "");
        File.WriteAllText(Path.Combine(root, ".env"), "CX_AI_MODEL=project");
        var local = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        Assert.Equal("project", AiConfiguration.Load(local).Model);
        File.WriteAllText(Path.Combine(local, ".env"), "CX_AI_MODEL=local");
        Assert.Equal("local", AiConfiguration.Load(local).Model);
        File.WriteAllText(Path.Combine(local, ".env"), "CX_AI_MODEL=updated");
        Assert.Equal("updated", AiConfiguration.Load(local).Model);
    }
}
