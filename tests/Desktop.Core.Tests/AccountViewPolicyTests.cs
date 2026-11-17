using Desktop.Core;
using Xunit;

namespace Desktop.Core.Tests;

public class AccountViewPolicyTests
{
    [Fact]
    public void Default_profile_uses_the_existing_default_browser_profile()
    {
        Assert.Null(AccountViewPolicy.ProfileNameOf(ProfileRegistry.DefaultId));
    }

    [Fact]
    public void Named_profiles_are_stable_and_contain_only_allowed_characters()
    {
        Assert.Equal("cx-p123456789abc", AccountViewPolicy.ProfileNameOf("p123456789abc"));
        Assert.Equal("cx-p123456789abc", AccountViewPolicy.ProfileNameOf("p123456789abc"));
        Assert.DoesNotContain(" ", AccountViewPolicy.ProfileNameOf("p123456789abc")!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("p:123")]
    [InlineData("p 12")]
    public void Invalid_identifiers_are_rejected(string id)
        => Assert.Throws<ArgumentException>(() => AccountViewPolicy.ProfileNameOf(id));

    [Fact]
    public void Trim_boundary_uses_the_instance_limit()
    {
        Assert.False(AccountViewPolicy.NeedsTrim(AccountViewPolicy.MaxInstances - 1));
        Assert.True(AccountViewPolicy.NeedsTrim(AccountViewPolicy.MaxInstances));
    }

    [Fact]
    public void Eviction_never_touches_the_active_or_busy_instance()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var usage = new[]
        {
            new AccountViewUsage("active", t.AddHours(3), Busy: false),
            new AccountViewUsage("busy", t, Busy: true),
            new AccountViewUsage("old", t.AddHours(1), Busy: false),
            new AccountViewUsage("new", t.AddHours(2), Busy: false)
        };
        Assert.Equal("old", AccountViewPolicy.EvictionCandidate(usage, "active"));
        Assert.Null(AccountViewPolicy.EvictionCandidate(usage.Take(2), "active"));
    }

    [Fact]
    public void Sync_detects_stale_and_missing_instances_by_id()
    {
        var profiles = new[] { ProfileRegistry.DefaultId, "p000000000001", "p000000000002" };
        var instances = new[] { ProfileRegistry.DefaultId, "p000000000009" };
        Assert.Equal(new[] { "p000000000009" }, AccountViewPolicy.Stale(instances, profiles));
        Assert.Equal(new[] { "p000000000001", "p000000000002" }, AccountViewPolicy.Missing(profiles, instances));
    }

    [Fact]
    public void Sync_is_idempotent_when_registry_and_pool_match()
    {
        var ids = new[] { ProfileRegistry.DefaultId, "p000000000001" };
        Assert.Empty(AccountViewPolicy.Stale(ids, ids));
        Assert.Empty(AccountViewPolicy.Missing(ids, ids));
    }
}
