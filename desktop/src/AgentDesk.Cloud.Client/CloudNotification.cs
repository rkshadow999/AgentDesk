namespace AgentDesk.Cloud.Client;

public enum CloudNotificationKind
{
    HandoffChanged,
    JobChanged,
    PolicyChanged,
}

public sealed record CloudNotification
{
    public CloudNotification(
        CloudNotificationKind kind,
        string? ResourceId = null,
        int? PolicyVersion = null)
    {
        switch (kind)
        {
            case CloudNotificationKind.HandoffChanged:
            case CloudNotificationKind.JobChanged:
                ResourceId = CloudRequestGuard.Identifier(
                    ResourceId!,
                    128,
                    nameof(ResourceId));
                if (PolicyVersion is not null)
                {
                    throw new ArgumentException(
                        "A resource notification cannot include a policy version.",
                        nameof(PolicyVersion));
                }
                break;
            case CloudNotificationKind.PolicyChanged:
                if (ResourceId is not null)
                {
                    throw new ArgumentException(
                        "A policy notification cannot include a resource identifier.",
                        nameof(ResourceId));
                }
                if (PolicyVersion is null or < 1)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(PolicyVersion),
                        "The policy version must be positive.");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        this.ResourceId = ResourceId;
        this.PolicyVersion = PolicyVersion;
    }

    public CloudNotificationKind Kind { get; }

    public string? ResourceId { get; }

    public int? PolicyVersion { get; }

    public override string ToString() => Kind switch
    {
        CloudNotificationKind.HandoffChanged =>
            $"CloudNotification {{ Kind = HandoffChanged, ResourceId = {ResourceId} }}",
        CloudNotificationKind.JobChanged =>
            $"CloudNotification {{ Kind = JobChanged, ResourceId = {ResourceId} }}",
        CloudNotificationKind.PolicyChanged =>
            $"CloudNotification {{ Kind = PolicyChanged, PolicyVersion = {PolicyVersion} }}",
        _ => nameof(CloudNotification),
    };
}
