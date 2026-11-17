using Desktop.Core;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Desktop.Core.Tests;

public sealed class BrowserRuntimeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public void Development_without_bundled_runtime_uses_installed_runtime()
        => Assert.Null(BrowserRuntime.Resolve(root));

    [Fact]
    public void Bundled_runtime_is_selected_by_absolute_path()
    {
        var runtime = Path.Combine(root, "WebView2Runtime");
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, "msedgewebview2.exe"), "fixture");
        Assert.Equal(runtime, BrowserRuntime.Resolve(root));
    }

    [Fact]
    public void Incomplete_bundle_fails_instead_of_hiding_missing_files()
    {
        Directory.CreateDirectory(Path.Combine(root, "WebView2Runtime"));
        Assert.Throws<FileNotFoundException>(() => BrowserRuntime.Resolve(root));
    }

    [Fact]
    public void Automatic_chapter_setting_survives_restart_and_old_settings_default_to_enabled()
    {
        var store = new SettingsStore(root);
        store.Save(new AppSettings { AutoAdvance = false });
        Assert.False(new SettingsStore(root).Load().Settings.AutoAdvance);
        File.WriteAllText(Path.Combine(root, "settings.json"), "{\"selectedProvider\":\"zhipu\"}");
        Assert.True(store.Load().Settings.AutoAdvance);
    }

    private string CreateBundle(string path = "resources/test.pak")
    {
        var source = Path.Combine(root, "app", "WebView2Runtime");
        Directory.CreateDirectory(Path.Combine(source, "resources"));
        File.WriteAllText(Path.Combine(source, "msedgewebview2.exe"), "fixture-browser");
        File.WriteAllText(Path.Combine(source, "resources", "test.pak"), "fixture-resource");
        File.WriteAllText(Path.Combine(source, "runtime-manifest.json"), JsonSerializer.Serialize(new
        {
            Version = "156.0.4314.58",
            Files = new[] { "msedgewebview2.exe", path }.Select(name => new
            {
                Path = name,
                SHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source,
                    name == path ? "resources/test.pak" : name))))
            })
        }));
        return source;
    }

    [Fact]
    public void Bundled_runtime_is_copied_to_dedicated_cache_without_changing_source()
    {
        var source = CreateBundle();
        var cache = Path.Combine(root, "cache");
        var prepared = BrowserRuntime.Prepare(Path.Combine(root, "app"), cache)!;
        Assert.StartsWith(cache + Path.DirectorySeparatorChar, prepared);
        Assert.Equal("fixture-browser", File.ReadAllText(Path.Combine(prepared, "msedgewebview2.exe")));
        Assert.Equal("fixture-resource", File.ReadAllText(Path.Combine(prepared, "resources", "test.pak")));
        Assert.Equal("fixture-resource", File.ReadAllText(Path.Combine(source, "resources", "test.pak")));
    }

    [Fact]
    public void Verified_cache_is_reusable_when_its_manifest_cannot_be_rewritten()
    {
        CreateBundle();
        var cache = Path.Combine(root, "cache");
        var prepared = BrowserRuntime.Prepare(Path.Combine(root, "app"), cache)!;
        using var readOnly = new FileStream(Path.Combine(prepared, "runtime-manifest.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(prepared, BrowserRuntime.Prepare(Path.Combine(root, "app"), cache));
    }

    [Fact]
    public void Damaged_cached_file_is_repaired_from_bundle()
    {
        CreateBundle();
        var cache = Path.Combine(root, "cache");
        var prepared = BrowserRuntime.Prepare(Path.Combine(root, "app"), cache)!;
        File.WriteAllText(Path.Combine(prepared, "resources", "test.pak"), "damaged");
        Assert.Equal(prepared, BrowserRuntime.Prepare(Path.Combine(root, "app"), cache));
        Assert.Equal("fixture-resource", File.ReadAllText(Path.Combine(prepared, "resources", "test.pak")));
    }

    [Fact]
    public void Runtime_manifest_cannot_escape_source_or_cache()
    {
        CreateBundle("../escape.pak");
        Assert.Throws<InvalidDataException>(() => BrowserRuntime.Prepare(Path.Combine(root, "app"), Path.Combine(root, "cache")));
        Assert.False(File.Exists(Path.Combine(root, "cache", "escape.pak")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
