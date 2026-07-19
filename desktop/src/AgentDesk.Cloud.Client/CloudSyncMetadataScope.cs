namespace AgentDesk.Cloud.Client;

public sealed record CloudSyncMetadataScope
{
    public CloudSyncMetadataScope(Uri serverBaseUri, string teamId)
    {
        ArgumentNullException.ThrowIfNull(serverBaseUri);
        var options = new CloudConnectionOptions(serverBaseUri);
        ServerScope = options.BaseUri.AbsoluteUri;
        TeamId = CloudRequestGuard.Identifier(teamId, 128, nameof(teamId));
    }

    public string ServerScope { get; }

    public string TeamId { get; }

    public static CloudSyncMetadataScope FromProfile(CloudConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.IsLocalOnly || profile.BaseUri is null || profile.TeamId is null)
        {
            throw new ArgumentException(
                "A remote cloud profile is required for sync metadata.",
                nameof(profile));
        }
        return new CloudSyncMetadataScope(profile.BaseUri, profile.TeamId);
    }

    public override string ToString() =>
        $"CloudSyncMetadataScope {{ ServerScope = {ServerScope}, Team = Present }}";
}
