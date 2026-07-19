namespace AgentDesk.Cloud.Client;

internal static class CloudNotificationValidator
{
    public static CloudNotification? ValidateHandoff(
        CloudConnectionProfile profile,
        CloudHandoffNotificationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(payload);
        if (!MatchesTeam(profile, payload.TeamId) ||
            !string.Equals(profile.DeviceId, payload.TargetDeviceId, StringComparison.Ordinal))
        {
            return null;
        }

        return TryCreate(
            () => new CloudNotification(
                CloudNotificationKind.HandoffChanged,
                payload.HandoffId));
    }

    public static CloudNotification? ValidateJob(
        CloudConnectionProfile profile,
        CloudJobNotificationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(payload);
        if (!MatchesTeam(profile, payload.TeamId))
        {
            return null;
        }

        return TryCreate(
            () => new CloudNotification(CloudNotificationKind.JobChanged, payload.JobId));
    }

    public static CloudNotification? ValidatePolicy(
        CloudConnectionProfile profile,
        CloudPolicyNotificationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(payload);
        if (!MatchesTeam(profile, payload.TeamId))
        {
            return null;
        }

        return TryCreate(
            () => new CloudNotification(
                CloudNotificationKind.PolicyChanged,
                PolicyVersion: payload.Version));
    }

    private static bool MatchesTeam(CloudConnectionProfile profile, string teamId) =>
        !profile.IsLocalOnly &&
        string.Equals(profile.TeamId, teamId, StringComparison.Ordinal);

    private static CloudNotification? TryCreate(Func<CloudNotification> create)
    {
        try
        {
            return create();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
