namespace AgentDesk.Engine.Sidecar;

public sealed class SidecarExitedEventArgs(int exitCode, bool wasExpected) : EventArgs
{
    public int ExitCode { get; } = exitCode;

    public bool WasExpected { get; } = wasExpected;
}
