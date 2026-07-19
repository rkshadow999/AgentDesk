namespace AgentDesk.Core.Engine;

public sealed record SessionForkResult(
    SessionId SessionId,
    string WorkspacePath,
    string ParentSessionId,
    int ChatMessagesCopied,
    int UpdatesCopied,
    bool PlanStateCopied,
    string? ModelId = null);

public enum SessionRewindMode
{
    All,
    ConversationOnly,
    FilesOnly,
}

public sealed record SessionRewindPoint(
    int PromptIndex,
    DateTimeOffset CreatedAt,
    int FileSnapshotCount,
    bool HasFileChanges,
    string? PromptPreview = null);

public sealed record SessionRewindConflict(string Path, string ConflictType);

public sealed record SessionRewindResult(
    bool Success,
    int TargetPromptIndex,
    SessionRewindMode Mode,
    IReadOnlyList<string> RevertedFiles,
    IReadOnlyList<string> CleanFiles,
    IReadOnlyList<SessionRewindConflict> Conflicts,
    string? PromptText = null,
    string? Error = null);
