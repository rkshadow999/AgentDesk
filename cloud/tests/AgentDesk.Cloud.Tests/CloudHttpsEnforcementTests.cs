using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AgentDesk.Cloud.Tests;

public sealed class CloudHttpsEnforcementTests : IDisposable
{
    private const string BootstrapToken = "agentdesk-https-test-bootstrap-token-000000000";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"agentdesk-cloud-https-{Guid.NewGuid():N}");

    [Fact]
    public async Task RequireHttpsRejectsPlainHttpWithoutDependingOnRedirectConfiguration()
    {
        Directory.CreateDirectory(_root);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("http://localhost"),
                AllowAutoRedirect = false,
            });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            BootstrapToken);

        using var health = await client.GetAsync("/health/live");
        using var protectedApi = await client.GetAsync("/api/v1/policy");

        Assert.Equal(HttpStatusCode.UpgradeRequired, health.StatusCode);
        Assert.Equal(HttpStatusCode.UpgradeRequired, protectedApi.StatusCode);
    }

    [Fact]
    public async Task RequireHttpsAllowsHttpsRequests()
    {
        Directory.CreateDirectory(_root);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
            });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            BootstrapToken);

        using var health = await client.GetAsync("/health/live");
        using var protectedApi = await client.GetAsync("/api/v1/policy");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, protectedApi.StatusCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder => builder.ConfigureAppConfiguration(
                (_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AgentDeskCloud:BootstrapToken"] = BootstrapToken,
                        ["AgentDeskCloud:DatabasePath"] = Path.Combine(_root, "cloud.db"),
                        ["AgentDeskCloud:RequireHttps"] = "true",
                        ["AgentDeskCloud:AutomationPollingIntervalSeconds"] = "300",
                    })));
}
