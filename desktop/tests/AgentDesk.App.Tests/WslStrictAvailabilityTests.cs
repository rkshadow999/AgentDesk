using AgentDesk.App.Bootstrap;
using AgentDesk.Engine.Sidecar;

namespace AgentDesk.App.Tests;

public sealed class WslStrictAvailabilityTests
{
    [Fact]
    public void IsAvailableRejectsWslWithoutANonDockerDistribution()
    {
        var directory = Directory.CreateTempSubdirectory("agentdesk-wsl-");
        try
        {
            var wsl = Path.Combine(directory.FullName, "wsl.exe");
            var engine = Path.Combine(directory.FullName, "agentdesk-engine");
            File.WriteAllBytes(wsl, []);
            File.WriteAllBytes(engine, []);

            Assert.False(WslStrictAvailability.IsAvailable(wsl, engine));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public void IsAvailableDoesNotTreatInstallationAsStrictEnforcement()
    {
        var directory = Directory.CreateTempSubdirectory("agentdesk-wsl-");
        try
        {
            var wsl = Path.Combine(directory.FullName, "wsl.exe");
            var engine = Path.Combine(directory.FullName, "agentdesk-engine");
            File.WriteAllBytes(wsl, []);
            File.WriteAllBytes(engine, []);

            var resolver = new FixedWslDistributionResolver("Ubuntu");
            var verifier = new FixedWslEngineInstallationVerifier(isCurrent: true);
            Assert.False(WslStrictAvailability.IsAvailable(
                wsl,
                engine,
                resolver,
                verifier));
            File.Delete(engine);
            Assert.False(WslStrictAvailability.IsAvailable(
                wsl,
                engine,
                resolver,
                verifier));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public void IsAvailableRejectsAMissingOrMismatchedInstalledEngine()
    {
        var directory = Directory.CreateTempSubdirectory("agentdesk-wsl-");
        try
        {
            var wsl = Path.Combine(directory.FullName, "wsl.exe");
            var engine = Path.Combine(directory.FullName, "agentdesk-engine");
            File.WriteAllBytes(wsl, []);
            File.WriteAllBytes(engine, []);

            Assert.False(WslStrictAvailability.IsAvailable(
                wsl,
                engine,
                new FixedWslDistributionResolver("Ubuntu"),
                new FixedWslEngineInstallationVerifier(isCurrent: false)));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private sealed class FixedWslDistributionResolver(string? distributionName)
        : IWslDistributionResolver
    {
        public string? Resolve(string wslExecutablePath) => distributionName;
    }

    private sealed class FixedWslEngineInstallationVerifier(bool isCurrent)
        : IWslEngineInstallationVerifier
    {
        public bool IsCurrent(
            string wslExecutablePath,
            string distributionName,
            string bundledEnginePath) =>
            isCurrent;
    }
}
