namespace AgentDesk.Core.Execution;

public readonly record struct ExecutionProfileSelection(
    ExecutionProfile Profile,
    bool RequiresSecurityAcknowledgement);
