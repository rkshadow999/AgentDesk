using AgentDesk.App.Recovery;
using AgentDesk.Core.Providers;

namespace AgentDesk.App.Tests;

public sealed class CrashRecoveryProviderIdentityTests
{
    [Fact]
    public void Create_UsesNormalizedProviderFieldsAndReturnsUppercaseSha256()
    {
        var first = new ProviderProfile(
            " HTTPS://EXAMPLE.COM:443/v1/ ",
            " grok-4.5 ",
            ProviderBackend.Responses);
        var equivalent = new ProviderProfile(
            "https://example.com/v1",
            "grok-4.5",
            ProviderBackend.Responses);

        var identity = CrashRecoveryProviderIdentity.Create(first);

        Assert.Equal(identity, CrashRecoveryProviderIdentity.Create(equivalent));
        Assert.Matches("^[0-9A-F]{64}$", identity);
    }

    [Fact]
    public void Create_ChangesWhenTheBackendOrModelChanges()
    {
        var baseline = new ProviderProfile(
            "https://example.com/v1",
            "grok-4.5",
            ProviderBackend.Responses);
        var differentBackend = new ProviderProfile(
            baseline.BaseUrl,
            baseline.Model,
            ProviderBackend.ChatCompletions);
        var differentModel = new ProviderProfile(
            baseline.BaseUrl,
            "grok-4.6",
            baseline.Backend);

        var identity = CrashRecoveryProviderIdentity.Create(baseline);

        Assert.NotEqual(identity, CrashRecoveryProviderIdentity.Create(differentBackend));
        Assert.NotEqual(identity, CrashRecoveryProviderIdentity.Create(differentModel));
    }
}
