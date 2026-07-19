using AgentDesk.Core.Engine;
using AgentDesk.Core.Execution;

namespace AgentDesk.App.Recovery;

public sealed record CrashRecoveryMarker(
    SessionId SessionId,
    string WorkspacePath,
    ExecutionProfile ExecutionProfile,
    SessionMode SessionMode,
    DateTimeOffset UpdatedAt,
    string ProviderIdentity = "");
