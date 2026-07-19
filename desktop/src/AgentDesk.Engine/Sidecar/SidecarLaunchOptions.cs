using AgentDesk.Core.Execution;
using AgentDesk.Core.Providers;

namespace AgentDesk.Engine.Sidecar;

public sealed record SidecarLaunchOptions(
    string WorkspacePath,
    ExecutionProfile ExecutionProfile)
{
    public string? EnginePath { get; init; }

    public string? ApiKey { get; init; }

    public ProviderProfile? ProviderProfile { get; init; }

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public bool CaptureStandardError { get; init; }

    public int StandardErrorCharacterLimit { get; init; } = 8 * 1024;
}
