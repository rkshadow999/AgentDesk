namespace AgentDesk.Updater.Core;

public sealed class UpdateSecurityException : Exception
{
    public UpdateSecurityException()
    {
    }

    public UpdateSecurityException(string message)
        : base(message)
    {
    }

    public UpdateSecurityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
