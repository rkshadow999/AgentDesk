namespace AgentDesk.Core.Engine;

public sealed record SessionSummary(
    SessionId SessionId,
    string Title,
    string WorkspacePath,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    string? ModelId = null,
    string? ParentSessionId = null,
    string? Branch = null,
    string? WorktreeLabel = null,
    string? SourceWorkspacePath = null);

public sealed record SessionPage(
    IReadOnlyList<SessionSummary> Sessions,
    string? NextCursor = null);
