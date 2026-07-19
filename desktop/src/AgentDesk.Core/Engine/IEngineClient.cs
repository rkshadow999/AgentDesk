namespace AgentDesk.Core.Engine;

public interface IEngineClient : IAsyncDisposable
{
    event EventHandler<EngineEvent>? EventReceived;

    event EventHandler<PermissionRequest>? PermissionRequested;

    event EventHandler<EngineFaultedEventArgs>? Faulted;

    EngineCapabilities Capabilities { get; }

    Task<EngineCapabilities> InitializeAsync(CancellationToken cancellationToken = default);

    Task AuthenticateAsync(CancellationToken cancellationToken = default);

    Task<SessionId> NewSessionAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default);

    Task<EngineSessionDocument> ExportSessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<EngineSessionDocument>(
            new NotSupportedException("This engine client does not support session export."));

    Task<SessionId> ImportSessionAsync(
        EngineSessionDocument document,
        string workingDirectory,
        CancellationToken cancellationToken = default) =>
        Task.FromException<SessionId>(
            new NotSupportedException("This engine client does not support session import."));

    Task<WorktreeCreateResult> CreateWorktreeAsync(
        WorktreeCreateRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<WorktreeCreateResult>(
            new NotSupportedException("This engine client does not support worktree creation."));

    Task<IReadOnlyList<WorktreeRecord>> ListWorktreesAsync(
        WorktreeListRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<WorktreeRecord>>(
            new NotSupportedException("This engine client does not support worktree listing."));

    Task<WorktreeRecord?> ShowWorktreeAsync(
        WorktreeShowRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<WorktreeRecord?>(
            new NotSupportedException("This engine client does not support worktree inspection."));

    Task<WorktreeApplyResult> ApplyWorktreeAsync(
        WorktreeApplyRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<WorktreeApplyResult>(
            new NotSupportedException("This engine client does not support worktree application."));

    Task<WorktreeRemoveResult> RemoveWorktreeAsync(
        WorktreeRemoveRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<WorktreeRemoveResult>(
            new NotSupportedException("This engine client does not support worktree removal."));

    Task<WorktreeGcResult> GcWorktreesAsync(
        WorktreeGcRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<WorktreeGcResult>(
            new NotSupportedException("This engine client does not support worktree garbage collection."));

    Task<IReadOnlyList<McpServerCatalogItem>> ListMcpServersAsync(
        SessionId? sessionId,
        bool useCache = true,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<McpServerCatalogItem>>(
            new NotSupportedException("This engine client does not support MCP catalog management."));

    Task<bool> ToggleMcpServerAsync(
        SessionId sessionId,
        string serverName,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(
            new NotSupportedException("This engine client does not support MCP server toggles."));

    Task<bool> UpsertMcpServerAsync(
        McpServerUpsertRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(
            new NotSupportedException("This engine client does not support MCP server configuration."));

    Task<bool> DeleteMcpServerAsync(
        SessionId sessionId,
        string serverName,
        CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(
            new NotSupportedException("This engine client does not support MCP server deletion."));

    Task<IReadOnlyList<SkillDescriptor>> ListSkillsAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<SkillDescriptor>>(
            new NotSupportedException("This engine client does not support skill discovery."));

    Task<SkillPathMutationResult> AddSkillPathAsync(
        string path,
        string? workingDirectory,
        CancellationToken cancellationToken = default) =>
        Task.FromException<SkillPathMutationResult>(
            new NotSupportedException("This engine client does not support adding skill paths."));

    Task<SkillPathMutationResult> RemoveSkillPathAsync(
        string path,
        string? workingDirectory,
        CancellationToken cancellationToken = default) =>
        Task.FromException<SkillPathMutationResult>(
            new NotSupportedException("This engine client does not support removing skill paths."));

    Task<SkillPathMutationResult> ResetSkillsAsync(
        string? workingDirectory,
        CancellationToken cancellationToken = default) =>
        Task.FromException<SkillPathMutationResult>(
            new NotSupportedException("This engine client does not support resetting skill configuration."));

    Task<SkillsConfiguration> GetSkillsConfigurationAsync(
        string? workingDirectory,
        CancellationToken cancellationToken = default) =>
        Task.FromException<SkillsConfiguration>(
            new NotSupportedException("This engine client does not support skill configuration inspection."));

    Task<HookCatalog> ListHooksAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<HookCatalog>(
            new NotSupportedException("This engine client does not support hook discovery."));

    Task<ExtensionActionOutcome> ExecuteHookActionAsync(
        SessionId sessionId,
        HookAction action,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ExtensionActionOutcome>(
            new NotSupportedException("This engine client does not support hook management."));

    Task<IReadOnlyList<PluginDescriptor>> ListPluginsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<PluginDescriptor>>(
            new NotSupportedException("This engine client does not support plugin discovery."));

    Task<ExtensionActionOutcome> ExecutePluginActionAsync(
        SessionId sessionId,
        PluginAction action,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ExtensionActionOutcome>(
            new NotSupportedException("This engine client does not support plugin management."));

    Task<MarketplaceCatalog> ListMarketplaceAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<MarketplaceCatalog>(
            new NotSupportedException("This engine client does not support marketplace discovery."));

    Task<ExtensionActionOutcome> ExecuteMarketplaceActionAsync(
        SessionId sessionId,
        MarketplaceAction action,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ExtensionActionOutcome>(
            new NotSupportedException("This engine client does not support marketplace management."));

    Task LoadSessionAsync(
        SessionId sessionId,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RuntimeCommand>> ListRuntimeCommandsAsync(
        string? workingDirectory,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackgroundTaskSnapshot>> ListBackgroundTasksAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);

    Task<BackgroundTaskKillOutcome> KillBackgroundTaskAsync(
        SessionId sessionId,
        string taskId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SubagentSnapshot>> ListRunningSubagentsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);

    Task<SubagentSnapshot?> GetSubagentAsync(
        SessionId sessionId,
        string subagentId,
        bool block = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task<SubagentCancelResult> CancelSubagentAsync(
        SessionId sessionId,
        string subagentId,
        CancellationToken cancellationToken = default);

    Task<SessionPage> ListSessionsAsync(
        string? workingDirectory,
        string? query,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task RenameSessionAsync(
        SessionId sessionId,
        string title,
        string? workingDirectory,
        CancellationToken cancellationToken = default);

    Task<SessionForkResult> ForkSessionAsync(
        SessionId sourceSessionId,
        string sourceWorkingDirectory,
        string targetWorkingDirectory,
        int? targetPromptIndex = null,
        string? modelId = null,
        string? sessionKind = null,
        string? sourceWorkspacePath = null,
        CancellationToken cancellationToken = default);

    Task CompactSessionAsync(
        SessionId sessionId,
        string? userContext = null,
        CancellationToken cancellationToken = default);

    Task FlushMemoryAsync(
        SessionId activeSessionId,
        CancellationToken cancellationToken = default);

    Task<MemoryFileListing> ListMemoryFilesAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<MemoryFileListing>(
            new NotSupportedException("This engine client does not support memory browsing."));

    Task<MemoryFileDocument> ReadMemoryFileAsync(
        SessionId sessionId,
        MemoryFileId fileId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<MemoryFileDocument>(
            new NotSupportedException("This engine client does not support memory reading."));

    Task<MemoryMutationResult> WriteMemoryFileAsync(
        SessionId sessionId,
        MemoryFileId fileId,
        string content,
        bool confirmed = false,
        CancellationToken cancellationToken = default) =>
        Task.FromException<MemoryMutationResult>(
            new NotSupportedException("This engine client does not support memory writing."));

    Task<MemoryMutationResult> DeleteMemoryFileAsync(
        SessionId sessionId,
        MemoryFileId fileId,
        bool confirmed = false,
        CancellationToken cancellationToken = default) =>
        Task.FromException<MemoryMutationResult>(
            new NotSupportedException("This engine client does not support memory deletion."));

    Task<IReadOnlyList<SessionRewindPoint>> GetRewindPointsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);

    Task<SessionRewindResult> RewindSessionAsync(
        SessionId sessionId,
        int targetPromptIndex,
        SessionRewindMode mode,
        bool force = false,
        CancellationToken cancellationToken = default);

    Task SetSessionModeAsync(
        SessionId sessionId,
        SessionMode mode,
        CancellationToken cancellationToken = default);

    Task<PromptResult> PromptAsync(
        SessionId sessionId,
        string text,
        CancellationToken cancellationToken = default);

    Task<PromptResult> PromptWithAttachmentsAsync(
        SessionId sessionId,
        string text,
        IReadOnlyList<PromptAttachment> attachments,
        CancellationToken cancellationToken = default) =>
        attachments.Count == 0
            ? PromptAsync(sessionId, text, cancellationToken)
            : Task.FromException<PromptResult>(
                new NotSupportedException("This engine client does not support image prompts."));

    Task CancelAsync(SessionId sessionId, CancellationToken cancellationToken = default);

    Task<bool> RespondToPermissionAsync(
        string requestId,
        PermissionDecision decision,
        CancellationToken cancellationToken = default);
}
