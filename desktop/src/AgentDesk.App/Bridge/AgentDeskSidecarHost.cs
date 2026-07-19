using AgentDesk.Core.Engine;
using AgentDesk.Engine.Sidecar;

namespace AgentDesk.App.Bridge;

public interface IAgentDeskSidecarHostFactory
{
    IAgentDeskSidecarHost Create();
}

public interface IAgentDeskSidecarHost : IAsyncDisposable
{
    event EventHandler<SidecarExitedEventArgs>? Exited;

    string? EngineWorkspacePath { get; }

    Task<IEngineClient> StartAsync(
        SidecarLaunchOptions options,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class AgentDeskSidecarHostFactory : IAgentDeskSidecarHostFactory
{
    public IAgentDeskSidecarHost Create() => new AgentDeskSidecarHost(new SidecarProcessHost());
}

internal sealed class AgentDeskSidecarHost(SidecarProcessHost host) : IAgentDeskSidecarHost
{
    public event EventHandler<SidecarExitedEventArgs>? Exited
    {
        add => host.Exited += value;
        remove => host.Exited -= value;
    }

    public string? EngineWorkspacePath => host.EngineWorkspacePath;

    public Task<IEngineClient> StartAsync(
        SidecarLaunchOptions options,
        CancellationToken cancellationToken = default) =>
        host.StartAsync(options, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        host.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => host.DisposeAsync();
}
