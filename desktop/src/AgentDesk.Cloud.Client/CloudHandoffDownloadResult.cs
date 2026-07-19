namespace AgentDesk.Cloud.Client;

public sealed class CloudHandoffDownloadResult
{
    internal CloudHandoffDownloadResult(
        string handoffId,
        string sourceDeviceId,
        string targetDeviceId,
        string sessionId,
        DateTimeOffset createdAt,
        SessionSyncDocument document)
    {
        HandoffId = handoffId;
        SourceDeviceId = sourceDeviceId;
        TargetDeviceId = targetDeviceId;
        SessionId = sessionId;
        CreatedAt = createdAt;
        Document = document;
    }

    public string HandoffId { get; }

    public string SourceDeviceId { get; }

    public string TargetDeviceId { get; }

    public string SessionId { get; }

    public DateTimeOffset CreatedAt { get; }

    public SessionSyncDocument Document { get; }

    public override string ToString() =>
        $"CloudHandoffDownloadResult {{ HandoffId = {HandoffId}, SessionId = {SessionId}, ByteLength = {Document.ByteLength} }}";
}
