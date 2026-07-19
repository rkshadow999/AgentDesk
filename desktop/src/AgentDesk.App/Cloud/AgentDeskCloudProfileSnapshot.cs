using AgentDesk.Cloud.Client;

namespace AgentDesk.App.Cloud;

public sealed class AgentDeskCloudProfileSnapshot
{
    internal AgentDeskCloudProfileSnapshot(
        CloudConnectionProfile profile,
        bool hasAccessToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.IsLocalOnly && hasAccessToken)
        {
            throw new ArgumentException(
                "A local-only cloud profile cannot have an access token.",
                nameof(hasAccessToken));
        }

        Profile = profile;
        HasAccessToken = hasAccessToken;
    }

    public CloudConnectionProfile Profile { get; }

    public bool HasAccessToken { get; }

    public override string ToString() => Profile.IsLocalOnly
        ? "AgentDeskCloudProfileSnapshot { Mode = LocalOnly, HasAccessToken = False }"
        : $"AgentDeskCloudProfileSnapshot {{ Mode = Remote, BaseUri = {Profile.BaseUri}, HasAccessToken = {HasAccessToken} }}";
}
