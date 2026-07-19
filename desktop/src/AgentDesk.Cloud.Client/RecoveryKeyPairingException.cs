namespace AgentDesk.Cloud.Client;

public sealed class RecoveryKeyPairingException : Exception
{
    internal RecoveryKeyPairingException()
        : base("The protected recovery-key package could not be opened securely.")
    {
    }
}
