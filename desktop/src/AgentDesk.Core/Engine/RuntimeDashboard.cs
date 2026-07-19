namespace AgentDesk.Core.Engine;

public enum BackgroundTaskKind
{
    Bash,
    Monitor,
}

public enum BackgroundTaskKillOutcome
{
    Killed,
    AlreadyExited,
    NotFound,
}

public sealed record BackgroundTaskSnapshot(
    string TaskId,
    string Command,
    string? DisplayCommand,
    string WorkingDirectory,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Output,
    string OutputFile,
    bool Truncated,
    int? ExitCode,
    string? Signal,
    bool Completed,
    BackgroundTaskKind Kind,
    bool ExplicitlyKilled,
    string? OwnerSessionId)
{
    public string UserFacingCommand => string.IsNullOrWhiteSpace(DisplayCommand)
        ? Command
        : DisplayCommand;
}

public enum SubagentStatus
{
    Initializing,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public sealed record SubagentSnapshot(
    string SubagentId,
    string ParentSessionId,
    string ChildSessionId,
    string SubagentType,
    string Description,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    SubagentStatus Status,
    int? TurnCount = null,
    int? ToolCallCount = null,
    ulong? TokensUsed = null,
    ulong? ContextWindowTokens = null,
    byte? ContextUsagePercent = null,
    IReadOnlyList<string>? ToolsUsed = null,
    int? ErrorCount = null,
    string? Output = null,
    string? WorktreePath = null,
    string? FailureError = null,
    string? CancelReason = null,
    string? ForkContextSource = null,
    string? ForkParentPromptId = null,
    string? ResumedFrom = null)
{
    public bool IsTerminal => Status is
        SubagentStatus.Completed or SubagentStatus.Failed or SubagentStatus.Cancelled;
}

public enum SubagentCancelOutcome
{
    Cancelled,
    AlreadyFinished,
    NotFound,
}

public sealed record SubagentCancelResult(
    SubagentCancelOutcome Outcome,
    SubagentStatus? TerminalStatus = null);
