namespace AgentDesk.Updater.Core;

public static class UpdateSelector
{
    public static UpdateAsset? Select(
        UpdateManifest manifest,
        SemanticVersion installedVersion,
        SemanticVersion? highestSeen,
        UpdateArchitecture architecture,
        bool allowPrerelease)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (highestSeen is { } floor && manifest.Version.CompareAgentDeskReleaseTo(floor) < 0)
        {
            throw new UpdateSecurityException("The update manifest is older than the highest trusted version.");
        }

        if (manifest.Version.CompareAgentDeskReleaseTo(installedVersion) <= 0 ||
            (manifest.Version.IsPrerelease && !allowPrerelease))
        {
            return null;
        }

        return manifest.Assets.SingleOrDefault(asset => asset.Architecture == architecture)
            ?? throw new UpdateSecurityException("The update manifest does not contain the required architecture.");
    }
}
