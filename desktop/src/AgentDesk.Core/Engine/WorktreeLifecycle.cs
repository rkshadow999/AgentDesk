namespace AgentDesk.Core.Engine;

public enum WorktreeCopyMode
{
    Clean,
    Dirty,
}

public enum WorktreeCreationType
{
    Linked,
    Standalone,
    Git,
}

public enum WorktreeCreateStatus
{
    Creating,
    Exists,
}

public enum WorktreeKind
{
    Session,
    Ab,
    Pool,
    Fork,
    Manual,
    Subagent,
}

public enum WorktreeRecordStatus
{
    Alive,
    Dead,
}

public enum WorktreeApplyMode
{
    Overwrite,
    Merge,
}

public enum WorktreeApplyStatus
{
    Success,
    Conflicts,
}

public enum WorktreeChangeType
{
    Create,
    Edit,
    Delete,
    Rename,
    Copy,
    TypeChange,
    Untracked,
}

public sealed record WorktreeCreateRequest(
    SessionId SessionId,
    string SourcePath,
    string? DestinationPath = null,
    WorktreeCopyMode CopyMode = WorktreeCopyMode.Dirty,
    string? GitReference = null,
    bool CopyIgnoredInBackground = false,
    IReadOnlyList<string>? IgnoredSkipPatterns = null,
    WorktreeCreationType? CreationType = null,
    string? Label = null);

public sealed record WorktreeCreateResult(
    WorktreeCreateStatus Status,
    SessionId SessionId,
    string WorktreePath,
    string? SourceGitRoot = null,
    string? Commit = null);

public sealed record WorktreeListRequest(
    string? Repository = null,
    IReadOnlyList<WorktreeKind>? Types = null,
    bool IncludeAll = false);

public sealed record WorktreeShowRequest(string IdOrPath);

public sealed record WorktreeMetadata(string Label, bool UserProvided);

public sealed record WorktreeRecord(
    string Id,
    string Path,
    string SourceRepository,
    string RepositoryName,
    WorktreeKind Kind,
    WorktreeCreationType CreationType,
    string? GitReference,
    string? HeadCommit,
    SessionId? SessionId,
    uint? CreatorProcessId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastAccessedAt,
    WorktreeRecordStatus Status,
    WorktreeMetadata? Metadata);

public sealed record WorktreeApplyRequest(
    SessionId SessionId,
    string WorktreePath,
    WorktreeApplyMode Mode = WorktreeApplyMode.Overwrite);

public sealed record WorktreeFileChange(
    string Path,
    string? OldPath,
    WorktreeChangeType ChangeType,
    bool? Staged,
    ulong Additions,
    ulong Deletions,
    string? Patch = null,
    ulong? PatchBytes = null,
    ulong? PatchLines = null,
    string? OldText = null,
    string? NewText = null);

public sealed record WorktreeConflict(
    string Path,
    WorktreeChangeType ChangeType,
    string? Base,
    string? Ours,
    string? Theirs);

public sealed record WorktreeApplyResult(
    WorktreeApplyStatus Status,
    IReadOnlyList<WorktreeFileChange> Files,
    IReadOnlyList<WorktreeConflict> Conflicts,
    string? GitRoot = null);

public sealed record WorktreeRemoveRequest(
    string IdOrPath,
    bool Force = false,
    bool DryRun = false);

public sealed record WorktreeRemoveResult(bool Removed, string? ResolvedPath = null);

public sealed record WorktreeGcRequest(
    bool DryRun = true,
    TimeSpan? MaximumAge = null,
    bool Force = false);

public sealed record WorktreeGcResult(
    ulong DeadRemoved,
    ulong ExpiredRemoved,
    ulong SkippedAlive,
    ulong RemoveFailed);
