using AgentDesk.Core.Execution;
using AgentDesk.Engine.Sidecar;

namespace AgentDesk.Engine.Tests;

public sealed class WslStrictRealIntegrationTests
{
    [Fact]
    public async Task InstalledWslStrictSidecarCompletesHandshakeAndReportsFailClosedHealth()
    {
        var sourcePath = Environment.GetEnvironmentVariable(
            "AGENTDESK_EXPECTED_WSL_ENGINE_SOURCE");
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        var workspace = Directory.CreateTempSubdirectory(
            "agentdesk-wsl-integration-");
        try
        {
            await using var host = new SidecarProcessHost();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var client = await host.StartAsync(
                new SidecarLaunchOptions(
                    workspace.FullName,
                    ExecutionProfile.WslStrict)
                {
                    EnginePath = sourcePath,
                    StartTimeout = TimeSpan.FromSeconds(30),
                    StopTimeout = TimeSpan.FromSeconds(5),
                    CaptureStandardError = true,
                },
                timeout.Token);

            var capabilities = await client.InitializeAsync(timeout.Token);

            Assert.True(capabilities.AgentDeskExtensions);
            Assert.True(capabilities.AgentDeskHealth);
            Assert.False(capabilities.StrictSandboxActive);

            await host.StopAsync(timeout.Token);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(
                workspace.FullName,
                TimeSpan.FromSeconds(30));
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(
        string path,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException &&
                DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
    }
}
