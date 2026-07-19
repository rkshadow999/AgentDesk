namespace AgentDesk.Cloud.Client;

public sealed class CloudRollbackDetectedException : InvalidOperationException
{
    internal CloudRollbackDetectedException(int knownRevision, int serverRevision)
        : base("The cloud session revision is older than the locally recorded revision.")
    {
        KnownRevision = knownRevision;
        ServerRevision = serverRevision;
    }

    public int KnownRevision { get; }

    public int ServerRevision { get; }
}
