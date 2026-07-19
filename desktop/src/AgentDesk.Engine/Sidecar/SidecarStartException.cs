namespace AgentDesk.Engine.Sidecar;

public enum SidecarStartFailure
{
    EngineNotFound,
    WorkspaceNotFound,
    WorkspacePathConversionFailed,
    WslUnavailable,
    StartTimedOut,
    ProcessStartFailed,
    ProcessExitedDuringStart,
}

public sealed class SidecarStartException : Exception
{
    public SidecarStartException(
        SidecarStartFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public SidecarStartFailure Failure { get; }
}
