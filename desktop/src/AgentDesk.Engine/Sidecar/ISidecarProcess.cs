namespace AgentDesk.Engine.Sidecar;

public interface ISidecarProcess : IAsyncDisposable
{
    event EventHandler? Exited;

    Stream StandardInput { get; }

    Stream StandardOutput { get; }

    Stream StandardError { get; }

    bool HasExited { get; }

    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void Kill(bool entireProcessTree);
}

public interface ISidecarProcessFactory
{
    Task<ISidecarProcess> StartAsync(
        SidecarProcessStartInfo startInfo,
        CancellationToken cancellationToken);
}
