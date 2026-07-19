using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AgentDesk.Cloud.Tests;

public sealed class CloudBootstrapTokenTests : IDisposable
{
    private const string CurrentToken = "agentdesk-current-bootstrap-token-000000000000";
    private const string PreviousToken = "agentdesk-previous-bootstrap-token-00000000000";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"agentdesk-cloud-bootstrap-{Guid.NewGuid():N}");

    [Fact]
    public async Task CurrentAndPreviousBootstrapTokensAuthenticateDuringRotationOverlap()
    {
        Directory.CreateDirectory(_root);
        await using var factory = CreateFactory(CurrentToken, PreviousToken, "overlap.db");
        using var currentClient = AuthenticatedClient(factory, CurrentToken);
        using var previousClient = AuthenticatedClient(factory, PreviousToken);

        using var currentResponse = await currentClient.GetAsync("/api/v1/policy");
        using var previousResponse = await previousClient.GetAsync("/api/v1/policy");

        Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, previousResponse.StatusCode);
    }

    [Fact]
    public async Task RemovingPreviousBootstrapTokenRevokesItAfterRestart()
    {
        Directory.CreateDirectory(_root);
        await using var factory = CreateFactory(CurrentToken, previousToken: null, "removed.db");
        using var currentClient = AuthenticatedClient(factory, CurrentToken);
        using var previousClient = AuthenticatedClient(factory, PreviousToken);

        using var currentResponse = await currentClient.GetAsync("/api/v1/policy");
        using var previousResponse = await previousClient.GetAsync("/api/v1/policy");

        Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, previousResponse.StatusCode);
    }

    [Theory]
    [InlineData("short", null, "BootstrapToken")]
    [InlineData(CurrentToken, "short", "PreviousBootstrapToken")]
    [InlineData(CurrentToken, CurrentToken, "must differ")]
    public void InvalidBootstrapTokenConfigurationFailsAtStartup(
        string currentToken,
        string? previousToken,
        string expectedMessage)
    {
        Directory.CreateDirectory(_root);
        using var factory = CreateFactory(currentToken, previousToken, $"invalid-{Guid.NewGuid():N}.db");

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private WebApplicationFactory<Program> CreateFactory(
        string currentToken,
        string? previousToken,
        string databaseName)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder => builder.ConfigureAppConfiguration(
                (_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AgentDeskCloud:BootstrapToken"] = currentToken,
                        ["AgentDeskCloud:PreviousBootstrapToken"] = previousToken,
                        ["AgentDeskCloud:DatabasePath"] = Path.Combine(_root, databaseName),
                        ["AgentDeskCloud:RequireHttps"] = "false",
                        ["AgentDeskCloud:AutomationPollingIntervalSeconds"] = "300",
                    })));
    }

    private static HttpClient AuthenticatedClient(
        WebApplicationFactory<Program> factory,
        string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
