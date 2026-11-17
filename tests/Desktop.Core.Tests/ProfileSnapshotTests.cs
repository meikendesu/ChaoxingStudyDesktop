using Desktop.Core;
using Xunit;

namespace Desktop.Core.Tests;

public sealed class ProfileSnapshotTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyDesktopSnapshots", Guid.NewGuid().ToString("N"));
    private readonly string globalEnv;

    public ProfileSnapshotTests()
    {
        Directory.CreateDirectory(root);
        globalEnv = Path.Combine(root, ".env");
        File.WriteAllText(globalEnv, "CX_AI_PROVIDER=zhipu\nCX_AI_API_KEY=fixture-only\nCX_AI_MODEL=fixture\nCX_AI_ENDPOINT=https://example.test/chat/completions\n");
    }

    [Fact]
    public void Read_collects_target_profile_data_without_applying_it()
    {
        var target = Path.Combine(root, "configurations", "p000000000001");
        new SettingsStore(target).Save(new AppSettings { AdvanceAtPercent = 77, PlaybackRate = 1.5, AutoAdvance = false });
        new TaskStateStore(target).Save(new TaskSnapshot
        { CourseUrl = "https://i.chaoxing.com/course", CourseId = "1", SavedAt = DateTimeOffset.Now });
        new ProfileCookieStore(target).Save([
            new SavedProfileCookie("session", "value", ".chaoxing.com", "/", true, true, 1, DateTime.MinValue)]);

        var snapshot = ProfileSnapshot.Read(target, globalEnv);

        Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(snapshot.Root));
        Assert.Equal(77, snapshot.Settings.AdvanceAtPercent);
        Assert.Equal(1.5, snapshot.Settings.PlaybackRate);
        Assert.False(snapshot.Settings.AutoAdvance);
        Assert.True(snapshot.HasTask);
        Assert.True(snapshot.HasEnv);
        Assert.Equal(Path.GetFullPath(globalEnv), Path.GetFullPath(snapshot.EnvPath));
        Assert.Equal(1, snapshot.CookieCount);
        Assert.Null(snapshot.SettingsWarning);
    }

    [Fact]
    public void Read_prefers_profile_local_env_and_is_independent_per_profile()
    {
        var first = Path.Combine(root, "configurations", "p000000000002");
        var second = Path.Combine(root, "configurations", "p000000000003");
        new SettingsStore(first).Save(new AppSettings { AdvanceAtPercent = 51 });
        new SettingsStore(second).Save(new AppSettings { AdvanceAtPercent = 92 });
        File.WriteAllText(Path.Combine(first, ".env"), "CX_AI_MODEL=first-only\n");

        var a = ProfileSnapshot.Read(first, globalEnv);
        var b = ProfileSnapshot.Read(second, globalEnv);

        Assert.Equal(51, a.Settings.AdvanceAtPercent);
        Assert.Equal(92, b.Settings.AdvanceAtPercent);
        Assert.Equal(Path.GetFullPath(Path.Combine(first, ".env")), Path.GetFullPath(a.EnvPath));
        Assert.Equal(Path.GetFullPath(globalEnv), Path.GetFullPath(b.EnvPath));
    }

    [Fact]
    public void Read_does_not_modify_existing_config_files()
    {
        var target = Path.Combine(root, "configurations", "p000000000004");
        new SettingsStore(target).Save(new AppSettings { AdvanceAtPercent = 63 });
        var settingsPath = Path.Combine(target, "settings.json");
        var before = File.ReadAllText(settingsPath);
        var written = File.GetLastWriteTimeUtc(settingsPath);

        ProfileSnapshot.Read(target, globalEnv);

        Assert.Equal(before, File.ReadAllText(settingsPath));
        Assert.Equal(written, File.GetLastWriteTimeUtc(settingsPath));
        // 读取不产生会话 Cookie 或课程记录文件。
        Assert.False(File.Exists(Path.Combine(target, "session-cookies.bin")));
        Assert.False(File.Exists(Path.Combine(target, "task.json")));
    }

    [Fact]
    public void Read_reports_corrupt_settings_as_warning_with_defaults()
    {
        var target = Path.Combine(root, "configurations", "p000000000005");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "settings.json"), "{broken");

        var snapshot = ProfileSnapshot.Read(target, globalEnv);

        Assert.NotNull(snapshot.SettingsWarning);
        Assert.Equal(91, snapshot.Settings.AdvanceAtPercent);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
