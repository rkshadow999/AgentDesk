namespace AgentDesk.Cloud.Client;

public sealed class CloudSyncUnavailableException : InvalidOperationException
{
    internal CloudSyncUnavailableException()
        : base("Cloud synchronization is unavailable while AgentDesk is in local-only mode.")
    {
    }
}
