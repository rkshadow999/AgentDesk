namespace AgentDesk.Cloud.Client;

public sealed class CloudSyncDownloadResult
{
    internal CloudSyncDownloadResult(
        string sessionId,
        int revision,
        SessionSyncDocument document)
    {
        SessionId = sessionId;
        Revision = revision;
        Document = document;
    }

    public string SessionId { get; }

    public int Revision { get; }

    public SessionSyncDocument Document { get; }

    public override string ToString() =>
        $"CloudSyncDownloadResult {{ SessionId = {SessionId}, Revision = {Revision}, ByteLength = {Document.ByteLength} }}";
}
