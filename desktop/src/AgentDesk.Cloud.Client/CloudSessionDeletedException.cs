namespace AgentDesk.Cloud.Client;

public sealed class CloudSessionDeletedException : InvalidOperationException
{
    public CloudSessionDeletedException(string sessionId, int revision)
        : base("The cloud session has been deleted at a newer revision.")
    {
        SessionId = CloudRequestGuard.Identifier(sessionId, 128, nameof(sessionId));
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }
        Revision = revision;
    }

    public string SessionId { get; }

    public int Revision { get; }
}
