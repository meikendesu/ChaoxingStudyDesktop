using Desktop.Core;
using Xunit;

namespace Desktop.Core.Tests;

public sealed class ProfileRegistryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyDesktopProfiles", Guid.NewGuid().ToString("N"));

    [Fact]
    public void First_load_creates_default_profile_reusing_the_root_directory()
    {
        var registry = ProfileRegistry.Load(root).Registry;
        Assert.Equal(ProfileRegistry.DefaultId, registry.CurrentId);
        Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(registry.CurrentDirectory));
        Assert.True(registry.Current.IsDefault);
        Assert.Single(registry.Profiles);
        Assert.True(File.Exists(Path.Combine(root, "profiles.json")));
    }

    [Fact]
    public void New_profile_has_its_own_directory_and_independent_settings()
    {
        new SettingsStore(root).Save(new AppSettings { AdvanceAtPercent = 97 });
        var registry = ProfileRegistry.Load(root).Registry;
        var created = registry.Create("第二个账号");
        Assert.NotEqual(ProfileRegistry.DefaultId, created.Id);
        Assert.NotEqual(Path.GetFullPath(root), Path.GetFullPath(registry.DirectoryOf(created.Id)));
        Assert.True(File.Exists(Path.Combine(registry.DirectoryOf(created.Id), "profile.json")));
        // 默认配置的设置不会被新配置共享。
        Assert.Equal(91, new SettingsStore(registry.DirectoryOf(created.Id)).Load().Settings.AdvanceAtPercent);
        Assert.Equal(97, new SettingsStore(root).Load().Settings.AdvanceAtPercent);
    }

    [Fact]
    public void Select_rename_and_delete_survive_reload()
    {
        var registry = ProfileRegistry.Load(root).Registry;
        var first = registry.Create("工作");
        var second = registry.Create("复习");
        registry.Select(first.Id);
        registry.Rename(second.Id, "复习B");

        var reopened = ProfileRegistry.Load(root).Registry;
        Assert.Equal(first.Id, reopened.CurrentId);
        Assert.Equal("复习B", reopened.Profiles.Single(entry => entry.Id == second.Id).Name);
        Assert.Equal(3, reopened.Profiles.Count);

        reopened.Delete(second.Id);
        Assert.DoesNotContain(reopened.Profiles, entry => entry.Id == second.Id);
        Assert.False(Directory.Exists(Path.Combine(root, "configurations", second.Id)));
        Assert.Equal(2, ProfileRegistry.Load(root).Registry.Profiles.Count);
    }

    [Fact]
    public void Deleting_current_profile_falls_back_to_default()
    {
        var registry = ProfileRegistry.Load(root).Registry;
        var created = registry.Create("临时");
        registry.Select(created.Id);
        registry.Delete(created.Id);
        Assert.Equal(ProfileRegistry.DefaultId, registry.CurrentId);
        Assert.Equal(ProfileRegistry.DefaultId, ProfileRegistry.Load(root).Registry.CurrentId);
    }

    [Fact]
    public void Default_profile_cannot_be_deleted_and_names_are_validated()
    {
        var registry = ProfileRegistry.Load(root).Registry;
        Assert.Throws<InvalidOperationException>(() => registry.Delete(ProfileRegistry.DefaultId));
        Assert.Throws<InvalidOperationException>(() => registry.Create(""));
        Assert.Throws<InvalidOperationException>(() => registry.Create("a/b"));
        registry.Create("工作");
        Assert.Throws<InvalidOperationException>(() => registry.Create("工作"));
        Assert.Throws<ArgumentException>(() => registry.Select("p-not-a-valid-id"));
    }

    [Fact]
    public void Corrupt_manifest_is_preserved_and_reported()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "profiles.json"), "{broken");
        var result = ProfileRegistry.Load(root);
        Assert.NotNull(result.Warning);
        Assert.Equal(ProfileRegistry.DefaultId, result.Registry.CurrentId);
        Assert.Equal("{broken", File.ReadAllText(Path.Combine(root, "profiles.json")));
    }

    [Fact]
    public void Profile_cookie_snapshot_is_encrypted_and_scoped()
    {
        var directory = Path.Combine(root, "configurations", "snapshot");
        var store = new ProfileCookieStore(directory);
        var cookie = new SavedProfileCookie("session", "fake-value", ".chaoxing.com", "/", true, true, 1, DateTime.MinValue);
        store.Save([cookie, cookie with { Domain = "chaoxing.com.evil.test" },
            cookie with { Name = "persist", Expires = DateTime.UtcNow.AddDays(3) }]);
        var restored = store.Read();
        Assert.Equal(2, restored.Length);
        Assert.Equal("fake-value", restored[0].Value);
        Assert.Equal(DateTime.MinValue, restored[0].Expires);
        Assert.Contains(restored, item => item.Name == "persist" && item.Expires > DateTime.MinValue);
        Assert.DoesNotContain("fake-value", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "profile-cookies.bin"))));
        store.Clear();
        Assert.Empty(store.Read());
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
