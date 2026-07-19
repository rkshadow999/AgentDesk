using AgentDesk.Core.Engine;

namespace AgentDesk.App.Cloud;

public interface IAgentDeskCloudEngineHost
{
    Task<IAgentDeskCloudEngineLease> BeginCloudEngineOperationAsync(
        CancellationToken cancellationToken = default);
}

public interface IAgentDeskCloudEngineLease : IAsyncDisposable
{
    IEngineClient Engine { get; }

    string WorkspacePath { get; }

    string EngineWorkspacePath { get; }

    Task ActivateSessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);
}
