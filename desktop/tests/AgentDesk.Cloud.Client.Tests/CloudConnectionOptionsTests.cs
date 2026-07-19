using AgentDesk.Cloud.Client;

namespace AgentDesk.Cloud.Client.Tests;

public sealed class CloudConnectionOptionsTests
{
    [Fact]
    public void Constructor_AcceptsHttpsEndpoint()
    {
        var options = new CloudConnectionOptions(new Uri("https://cloud.agentdesk.example/root/"));

        Assert.Equal("https://cloud.agentdesk.example/root/", options.BaseUri.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RequestTimeout);
        Assert.Equal(16 * 1024 * 1024, options.MaximumEnvelopeBytes);
        Assert.True(
            options.MaximumResponseBytes >=
            ((options.MaximumEnvelopeBytes + 2L) / 3 * 4) + 64 * 1024);
    }

    [Theory]
    [InlineData("http://cloud.agentdesk.example/")]
    [InlineData("ftp://localhost/")]
    [InlineData("https://user:password@cloud.agentdesk.example/")]
    [InlineData("https://cloud.agentdesk.example/?token=secret")]
    [InlineData("https://cloud.agentdesk.example/#fragment")]
    public void Constructor_RejectsUnsafeEndpoint(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => new CloudConnectionOptions(new Uri(endpoint)));
    }

    [Theory]
    [InlineData("http://localhost:5050/")]
    [InlineData("http://127.0.0.1:5050/")]
    [InlineData("http://[::1]:5050/")]
    public void Constructor_AllowsHttpLoopbackForLocalTesting(string endpoint)
    {
        var options = new CloudConnectionOptions(new Uri(endpoint));

        Assert.Equal(endpoint, options.BaseUri.AbsoluteUri);
    }

    [Fact]
    public void Constructor_RejectsOutOfRangeLimits()
    {
        var endpoint = new Uri("https://cloud.agentdesk.example/");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CloudConnectionOptions(endpoint, requestTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CloudConnectionOptions(endpoint, maximumEnvelopeBytes: 15));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CloudConnectionOptions(endpoint, maximumResponseBytes: 0));
    }

    [Fact]
    public void Constructor_DefaultResponseLimitTracksCustomEnvelopeLimit()
    {
        var options = new CloudConnectionOptions(
            new Uri("https://cloud.agentdesk.example/"),
            maximumEnvelopeBytes: 32 * 1024 * 1024);

        Assert.True(
            options.MaximumResponseBytes >=
            ((options.MaximumEnvelopeBytes + 2L) / 3 * 4) + 64 * 1024);
    }
}
