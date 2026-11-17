using Desktop.Core;
using Xunit;

namespace Desktop.Core.Tests;

public sealed class LoginPersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public void Published_versions_share_fallback_and_migrate_without_deleting_old_data()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ChaoxingStudyDesktop.slnx"), "");
        var previous = Path.Combine(root, "artifacts", "publish", "previous", "data");
        Directory.CreateDirectory(Path.Combine(previous, "BrowserProfile"));
        File.WriteAllText(Path.Combine(previous, "BrowserProfile", "fixture.txt"), "fake-profile");
        var first = UserDataPaths.Fallback(Path.Combine(root, "artifacts", "publish", "first"));
        var second = UserDataPaths.Fallback(Path.Combine(root, "artifacts", "publish", "second"));
        Assert.Equal(first, second);
        UserDataPaths.MigrateLegacy(first);
        Assert.Equal("fake-profile", File.ReadAllText(Path.Combine(first, "BrowserProfile", "fixture.txt")));
        Assert.True(File.Exists(Path.Combine(previous, "BrowserProfile", "fixture.txt")));
        File.WriteAllText(Path.Combine(first, "BrowserProfile", "fixture.txt"), "new-profile");
        UserDataPaths.MigrateLegacy(first);
        Assert.Equal("new-profile", File.ReadAllText(Path.Combine(first, "BrowserProfile", "fixture.txt")));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public void Session_cookies_are_encrypted_scoped_expiring_and_clearable()
    {
        var store = new SessionCookieStore(root);
        var cookie = new SavedSessionCookie("fake-session", "fixture-secret-only", ".chaoxing.com", "/", true, true, 1);
        store.Save([cookie, cookie with { Domain = "chaoxing.com.evil.test" }]);
        Assert.Equal(cookie, Assert.Single(store.Read()));
        Assert.DoesNotContain("fixture-secret-only", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "session-cookies.bin"))));
        Assert.Empty(store.Read(DateTimeOffset.UtcNow.AddDays(8)));
        store.Clear();
        Assert.Empty(store.Read());
    }
}
