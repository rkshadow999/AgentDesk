using AgentDesk.Core.Execution;
using AgentDesk.App.Cloud;
using AgentDesk.App.Workspace;

namespace AgentDesk.App.Bridge;

public sealed record AgentDeskHostOptions
{
    public AgentDeskHostOptions(string? workspacePath = null)
    {
        WorkspacePath = string.IsNullOrWhiteSpace(workspacePath) ? null : workspacePath;
    }

    public string? WorkspacePath { get; }

    public string CredentialName { get; init; } = "xai";

    public string? NativeEnginePath { get; init; }

    public string? WslEnginePath { get; init; }

    public bool IsTrustedWorkspace { get; init; }

    public bool IsWslStrictAvailable { get; init; }

    public TimeSpan AcpHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan RuntimeOperationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public AgentDeskCloudPolicyGate? CloudPolicyGate { get; init; }

    public IWorkspaceContextService? WorkspaceContextService { get; init; }

    public string? GetEnginePath(ExecutionProfile executionProfile) => executionProfile switch
    {
        ExecutionProfile.NativeProtected => NativeEnginePath,
        ExecutionProfile.WslStrict => WslEnginePath,
        _ => throw new ArgumentOutOfRangeException(nameof(executionProfile)),
    };
}
