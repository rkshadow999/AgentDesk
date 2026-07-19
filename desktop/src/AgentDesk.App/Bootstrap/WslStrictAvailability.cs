using AgentDesk.Engine.Sidecar;

namespace AgentDesk.App.Bootstrap;

public static class WslStrictAvailability
{
    private static bool StrictNetworkEnforcementImplemented => false;

    public static bool IsAvailable(
        string wslExecutablePath,
        string linuxEnginePath,
        IWslDistributionResolver? distributionResolver = null,
        IWslEngineInstallationVerifier? installationVerifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wslExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(linuxEnginePath);
        if (!StrictNetworkEnforcementImplemented ||
            !File.Exists(wslExecutablePath) ||
            !File.Exists(linuxEnginePath))
        {
            return false;
        }

        try
        {
            var distributionName = (distributionResolver ??
                SystemWslDistributionResolver.Instance).Resolve(wslExecutablePath);
            return WslDistributionSelector.IsSafeName(distributionName) &&
                (installationVerifier ??
                    SystemWslEngineInstallationVerifier.Instance).IsCurrent(
                        wslExecutablePath,
                        distributionName,
                        linuxEnginePath);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                IOException or
                System.ComponentModel.Win32Exception or
                UnauthorizedAccessException)
        {
            return false;
        }
    }
}
