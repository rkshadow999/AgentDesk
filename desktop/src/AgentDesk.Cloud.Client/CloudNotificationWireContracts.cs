namespace AgentDesk.Cloud.Client;

internal static class CloudNotificationMethods
{
    public const string HandoffChanged = "handoffChanged";
    public const string JobChanged = "jobChanged";
    public const string PolicyChanged = "policyChanged";
}

internal sealed record CloudHandoffNotificationPayload(
    string TeamId,
    string TargetDeviceId,
    string HandoffId);

internal sealed record CloudJobNotificationPayload(string TeamId, string JobId);

internal sealed record CloudPolicyNotificationPayload(string TeamId, int Version);
