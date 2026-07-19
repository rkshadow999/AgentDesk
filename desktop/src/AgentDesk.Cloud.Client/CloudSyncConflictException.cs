namespace AgentDesk.Cloud.Client;

public enum CloudSyncConflictKind
{
    RemoteMissing,
    RemoteDeleted,
    RemoteDocumentChanged,
}

public sealed class CloudSyncConflictException : InvalidOperationException
{
    internal CloudSyncConflictException(
        CloudSyncConflictKind kind,
        string sessionId,
        int? knownRevision,
        int? remoteRevision)
        : base("The cloud session changed and requires explicit conflict resolution.")
    {
        Kind = kind;
        SessionId = sessionId;
        KnownRevision = knownRevision;
        RemoteRevision = remoteRevision;
    }

    public CloudSyncConflictKind Kind { get; }

    public string SessionId { get; }

    public int? KnownRevision { get; }

    public int? RemoteRevision { get; }
}
