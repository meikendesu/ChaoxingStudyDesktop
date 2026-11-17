using Desktop.Core;
using Xunit;

namespace Desktop.Core.Tests;

public sealed class AgreementStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    public AgreementStoreTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void Fresh_root_requires_agreement_and_persists_acceptance()
    {
        var store = new AgreementStore(root);
        Assert.False(store.IsAccepted());

        store.Accept();

        Assert.True(new AgreementStore(root).IsAccepted());
        var saved = File.ReadAllText(Path.Combine(root, "agreement.json"));
        Assert.Contains(AgreementStore.Developer, saved);
        Assert.Contains(AgreementStore.CurrentVersion, saved);
    }

    [Fact]
    public void Corrupt_state_is_treated_as_not_accepted()
    {
        File.WriteAllText(Path.Combine(root, "agreement.json"), "{ broken json");
        Assert.False(new AgreementStore(root).IsAccepted());
    }

    [Fact]
    public void Acceptance_of_other_version_requires_reconfirmation()
    {
        var store = new AgreementStore(root);
        store.Accept();
        var path = Path.Combine(root, "agreement.json");
        var text = File.ReadAllText(path);
        var replaced = System.Text.RegularExpressions.Regex.Replace(text,
            "(\"version\"\\s*:\\s*)\"[^\"]+\"", "$1\"0.9\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.NotEqual(text, replaced);
        File.WriteAllText(path, replaced);
        Assert.False(new AgreementStore(root).IsAccepted());
    }
}
