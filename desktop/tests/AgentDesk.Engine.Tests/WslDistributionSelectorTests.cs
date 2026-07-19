using AgentDesk.Engine.Sidecar;

namespace AgentDesk.Engine.Tests;

public sealed class WslDistributionSelectorTests
{
    [Fact]
    public void SystemResolverMatchesTheOptInIntegrationDistribution()
    {
        var expected = Environment.GetEnvironmentVariable(
            "AGENTDESK_EXPECTED_WSL_DISTRIBUTION");
        if (string.IsNullOrWhiteSpace(expected))
        {
            return;
        }

        var wslExecutablePath = Path.Combine(
            Environment.SystemDirectory,
            "wsl.exe");
        var selected = SystemWslDistributionResolver.Instance.Resolve(wslExecutablePath);

        Assert.Equal(expected, selected);
    }

    [Fact]
    public void SelectUsesTheOnlyNonDockerDistribution()
    {
        var selected = WslDistributionSelector.Select(
            ["docker-desktop", "Ubuntu"],
            configuredName: null);

        Assert.Equal("Ubuntu", selected);
    }

    [Fact]
    public void SelectFailsClosedWhenMoreThanOneNonDockerDistributionExists()
    {
        var selected = WslDistributionSelector.Select(
            ["Ubuntu", "Debian"],
            configuredName: null);

        Assert.Null(selected);
    }

    [Fact]
    public void SelectHonorsAnInstalledExplicitDistribution()
    {
        var selected = WslDistributionSelector.Select(
            ["Ubuntu", "Debian"],
            configuredName: "debian");

        Assert.Equal("Debian", selected);
    }

    [Theory]
    [InlineData("docker-desktop")]
    [InlineData("docker-desktop-data")]
    [InlineData("missing")]
    [InlineData("Ubuntu\nDebian")]
    public void SelectRejectsUnsafeOrUnavailableExplicitDistributions(string configuredName)
    {
        var selected = WslDistributionSelector.Select(
            ["docker-desktop", "Ubuntu"],
            configuredName);

        Assert.Null(selected);
    }
}
