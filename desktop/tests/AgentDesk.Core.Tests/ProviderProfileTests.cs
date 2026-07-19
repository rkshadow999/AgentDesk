using AgentDesk.Core.Providers;

namespace AgentDesk.Core.Tests;

public sealed class ProviderProfileTests
{
    [Fact]
    public void ConstructorNormalizesEndpointAndBindsCredentialNameToIt()
    {
        var profile = new ProviderProfile(
            "HTTPS://Example.COM:443/v1/",
            "  grok-4.5  ",
            ProviderBackend.ChatCompletions);
        var equivalent = new ProviderProfile(
            "https://example.com/v1",
            "grok-4.5",
            ProviderBackend.ChatCompletions);

        Assert.Equal("https://example.com/v1", profile.BaseUrl);
        Assert.Equal("grok-4.5", profile.Model);
        Assert.Equal(equivalent.CredentialName, profile.CredentialName);
        Assert.StartsWith("providers/", profile.CredentialName, StringComparison.Ordinal);
        Assert.EndsWith(".api_key", profile.CredentialName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProviderBackend.ChatCompletions)]
    [InlineData(ProviderBackend.Responses)]
    public void ConstructorPreservesSupportedBackend(ProviderBackend backend)
    {
        var profile = new ProviderProfile(
            "https://example.com/v1",
            "grok-4.5",
            backend);

        Assert.Equal(backend, profile.Backend);
    }

    [Fact]
    public void CredentialNameChangesWhenTheEndpointPathChanges()
    {
        var first = new ProviderProfile("https://example.com/v1", "grok-4.5");
        var second = new ProviderProfile("https://example.com/proxy/v1", "grok-4.5");

        Assert.NotEqual(first.CredentialName, second.CredentialName);
    }

    [Theory]
    [InlineData("ftp://example.com/v1")]
    [InlineData("https://user:password@example.com/v1")]
    [InlineData("https://example.com/v1?tenant=secret")]
    [InlineData("https://example.com/v1#fragment")]
    [InlineData("not-a-url")]
    public void ConstructorRejectsUnsafeOrAmbiguousEndpoints(string baseUrl)
    {
        Assert.Throws<ArgumentException>(() => new ProviderProfile(baseUrl, "grok-4.5"));
    }

    [Fact]
    public void PlainHttpCredentialsRequireAnExplicitOptIn()
    {
        var protectedProfile = new ProviderProfile("http://example.com/v1", "grok-4.5");
        var optedInProfile = new ProviderProfile(
            "http://example.com/v1",
            "grok-4.5",
            allowInsecureTransport: true);

        Assert.False(protectedProfile.CanSendCredentials);
        Assert.True(optedInProfile.CanSendCredentials);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ConstructorRejectsAnEmptyModel(string model)
    {
        Assert.Throws<ArgumentException>(() =>
            new ProviderProfile("https://example.com/v1", model));
    }
}
