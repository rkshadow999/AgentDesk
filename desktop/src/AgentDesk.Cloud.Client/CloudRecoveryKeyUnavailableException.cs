namespace AgentDesk.Cloud.Client;

public sealed class CloudRecoveryKeyUnavailableException : InvalidOperationException
{
    internal CloudRecoveryKeyUnavailableException()
        : base("The shared recovery key required to decrypt this cloud handoff is unavailable.")
    {
    }
}
