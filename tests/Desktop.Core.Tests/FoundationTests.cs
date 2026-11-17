using System.Text.Json;
using Desktop.Core;
using Xunit;

namespace Desktop.Core.Tests;

public sealed class FoundationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyDesktopTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("https://chaoxing.com/", true)]
    [InlineData("https://passport2.chaoxing.com/login", true)]
    [InlineData("https://www.xuexitong.com/", true)]
    [InlineData("https://chaoxing.com.evil.test/", true)]
    [InlineData("https://fakechaoxing.com/", true)]
    [InlineData("http://chaoxing.com/", true)]
    [InlineData("https://chaoxing.com:8443/", true)]
    [InlineData("https://user:pass@chaoxing.com/", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/private.txt", false)]
    public void Navigation_allows_any_http_https_website(string url, bool expected)
        => Assert.Equal(expected, BrowserPolicy.IsAllowed(url));

    [Fact]
    public void Settings_survive_restart_without_secret_properties()
    {
        var store = new SettingsStore(root);
        store.Save(new AppSettings { SelectedProvider = "deepseek" });
        var restored = new SettingsStore(root).Load();
        Assert.Equal("deepseek", restored.Settings.SelectedProvider);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "settings.json")));
        Assert.False(json.RootElement.TryGetProperty("apiKey", out _));
    }

    [Fact]
    public void Corrupt_settings_are_preserved_and_reported()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, "{broken");
        var loaded = new SettingsStore(root).Load();
        Assert.NotNull(loaded.Warning);
        Assert.Equal("zhipu", loaded.Settings.SelectedProvider);
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    [Fact]
    public void Secrets_are_encrypted_and_isolated_by_provider()
    {
        var vault = new SecretStore(root);
        const string secret = "test-only-not-a-real-key-123456";
        vault.Save("zhipu", secret);
        vault.Save("deepseek", "another-test-only-key");
        var reopened = new SecretStore(root);
        Assert.Equal(secret, reopened.Read("zhipu"));
        Assert.Equal("another-test-only-key", reopened.Read("deepseek"));
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(root, "secrets", "zhipu.bin")));
        reopened.Delete("zhipu");
        Assert.Null(reopened.Read("zhipu"));
        Assert.NotNull(reopened.Read("deepseek"));
    }

    [Fact]
    public void Invalid_provider_cannot_escape_secret_directory()
        => Assert.Throws<ArgumentException>(() => new SecretStore(root).Save("../escape", "test-only"));

    [Fact]
    public void Log_is_bounded_and_redacts_registered_keys_and_authorization()
    {
        var log = new EventLog();
        log.RegisterSecret("test-only-secret-value");
        for (var i = 0; i < 55; i++) log.Write("INFO", $"Entry {i}");
        log.Write("INFO", "test-only-secret-value Authorization: Bearer abcdefghijk");
        Assert.Equal(50, log.Entries.Count);
        Assert.DoesNotContain("test-only-secret-value", log.Entries.Last().Message);
        Assert.DoesNotContain("abcdefghijk", log.Entries.Last().Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
