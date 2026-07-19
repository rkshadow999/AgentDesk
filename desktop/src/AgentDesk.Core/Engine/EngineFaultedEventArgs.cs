namespace AgentDesk.Core.Engine;

public sealed class EngineFaultedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception ?? throw new ArgumentNullException(nameof(exception));
}
