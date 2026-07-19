namespace AgentDesk.Cloud.Client;

public sealed class RecoveryKeyStoreException : Exception
{
    internal RecoveryKeyStoreException()
        : base("The recovery key could not be accessed securely.")
    {
    }
}
