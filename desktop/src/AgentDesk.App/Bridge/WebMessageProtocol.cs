using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentDesk.App.Attachments;
using AgentDesk.App.Automation;
using AgentDesk.App.Cloud;
using AgentDesk.App.Settings;
using AgentDesk.App.Workspace;
using AgentDesk.Cloud.Client;
using AgentDesk.Core.Engine;
using AgentDesk.Core.Execution;
using AgentDesk.Core.Providers;
using AgentDesk.Updater.Core;

namespace AgentDesk.App.Bridge;

public static class WebMessageProtocol
{
    public const int SchemaVersion = 1;
    public const int MaximumMessageCharacters = 32 * 1024 * 1024;

    private const int MaximumSessionIdCharacters = 512;
    private const int MaximumFileNameCharacters = 255;
    private const int MaximumPathCharacters = 32_767;
    private const int MaximumGitReferenceCharacters = 512;
    private const int MaximumLabelCharacters = 256;
    private const int MaximumSkipPatterns = 256;
    private const int MaximumSkipPatternCharacters = 1_024;
    private const int MaximumWorktreeRecords = 4_096;
    private const int MaximumWorktreeChanges = 10_000;
    private const int MaximumWorktreeTextCharacters = 2 * 1024 * 1024;
    private const int MaximumWorktreeAggregateTextCharacters = 16 * 1024 * 1024;
    private const long MaximumWorktreeAgeSeconds = 3_650L * 24L * 60L * 60L;
    private const int MaximumExtensionPayloadCharacters = 256 * 1024;
    private const int MaximumExtensionListItems = 4_096;
    private const int MaximumExtensionNameCharacters = 512;
    private const int MaximumExtensionTextCharacters = 64 * 1024;
    private const int MaximumExtensionRequestIdCharacters = 64;
    private const int MaximumWorkspaceContextQueryCharacters = 512;
    private const int MaximumWorkspaceContextContentCharacters = 512 * 1024;
    private const int MaximumWorkspaceContextFiles = 100;
    private const int MaximumMemoryFiles = 512;
    private const int MaximumMemoryFileIdCharacters = 263;
    private const int MaximumMemoryFileNameCharacters = 255;
    private const int MaximumMemoryContentBytes = 64 * 1024;
    private const int MaximumMemoryMessageCharacters = 4 * 1024;
    private const int MemoryConfirmationTokenCharacters = 64;
    private const int DocumentTokenCharacters = 64;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly UTF8Encoding WorkspaceContextUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static WebCommand ParseCommand(string json) =>
        ParseCommandCore(json, expectedDocumentToken: null, requireDocumentToken: false);

    internal static WebEvent? TryCreateCommandErrorEvent(string json, string message)
    {
        if (string.IsNullOrWhiteSpace(json) ||
            json.Length > MaximumMessageCharacters ||
            string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    return null;
                }
            }

            if (!root.TryGetProperty("schemaVersion", out var version) ||
                !version.TryGetInt32(out var versionNumber) ||
                versionNumber != SchemaVersion ||
                !root.TryGetProperty("type", out var typeValue) ||
                typeValue.ValueKind is not JsonValueKind.String ||
                typeValue.GetString() is not ("extensions/list" or "extensions/action") ||
                !root.TryGetProperty("requestId", out var requestIdValue) ||
                requestIdValue.ValueKind is not JsonValueKind.String ||
                requestIdValue.GetString() is not { } requestId)
            {
                return null;
            }

            ValidateMaintenanceRequestId(requestId);
            return new ExtensionsErrorWebEvent(requestId, null, null, null, message);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    internal static WebCommand ParseAuthenticatedCommand(
        string json,
        string? expectedDocumentToken) =>
        ParseCommandCore(json, expectedDocumentToken, requireDocumentToken: true);

    private static WebCommand ParseCommandCore(
        string json,
        string? expectedDocumentToken,
        bool requireDocumentToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (json.Length > MaximumMessageCharacters)
        {
            throw Invalid("The web message exceeds the maximum supported size.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("The web message must be a JSON object.");
            }
            ValidateNoDuplicateProperties(root);

            if (!root.TryGetProperty("schemaVersion", out var version) ||
                !version.TryGetInt32(out var versionNumber) ||
                versionNumber != SchemaVersion)
            {
                throw Invalid("The web message schema version is not supported.");
            }

            if (requireDocumentToken)
            {
                ValidateDocumentToken(root, expectedDocumentToken);
            }

            var type = RequiredString(root, "type");
            ValidateCommandProperties(root, type, requireDocumentToken);
            return type switch
            {
                "ui/ready" => new UiReadyWebCommand(),
                "ui/modal" => new ModalStateWebCommand(RequiredBoolean(root, "isOpen")),
                "ui/preferences/save" => ParseUiPreferences(root),
                "attachment/select" => new SelectImageAttachmentsWebCommand(
                    RequiredMaintenanceRequestId(root)),
                "attachment/discard" => ParseDiscardImageAttachments(root),
                "workspace/select" => new SelectWorkspaceWebCommand(),
                "workspace/recent/open" => new OpenRecentWorkspaceWebCommand(
                    RequiredString(root, "path")),
                "workspace/recent/remove" => new RemoveRecentWorkspaceWebCommand(
                    RequiredString(root, "path")),
                "workspace/context/instructions/list" =>
                    new WorkspaceInstructionsListWebCommand(
                        RequiredWorkspaceContextRequestId(root),
                        RequiredNonNegativeInt32(root, "workspaceGeneration")),
                "workspace/context/file/read" => new WorkspaceFileReadWebCommand(
                    RequiredWorkspaceContextRequestId(root),
                    RequiredNonNegativeInt32(root, "workspaceGeneration"),
                    RequiredWorkspaceInstructionPath(root, "relativePath")),
                "workspace/context/instructions/write" =>
                    new WorkspaceInstructionsWriteWebCommand(
                        RequiredWorkspaceContextRequestId(root),
                        RequiredNonNegativeInt32(root, "workspaceGeneration"),
                        RequiredWorkspaceInstructionPath(root, "relativePath"),
                        RequiredWorkspaceContextContent(root)),
                "workspace/context/file/search" => new WorkspaceFileSearchWebCommand(
                    RequiredWorkspaceContextRequestId(root),
                    RequiredNonNegativeInt32(root, "workspaceGeneration"),
                    RequiredWorkspaceContextQuery(root)),
                "provider/save" => ParseProvider(root),
                "session/list" => ParseSessionList(root),
                "session/open" => ParseSessionOpen(root),
                "session/new" => ParseSessionNew(root),
                "session/rename" => ParseSessionRename(root),
                "session/archive" => ParseSessionArchive(root),
                "session/fork" => ParseSessionFork(root),
                "session/compact" => ParseSessionCompact(root),
                "session/rewind/points" => new SessionRewindPointsWebCommand(
                    RequiredString(root, "sessionId")),
                "session/rewind" => ParseSessionRewind(root),
                "runtime/dashboard/refresh" => new RuntimeDashboardRefreshWebCommand(
                    RequiredString(root, "sessionId")),
                "runtime/task/kill" => new RuntimeTaskKillWebCommand(
                    RequiredString(root, "sessionId"),
                    RequiredString(root, "taskId")),
                "runtime/subagent/get" => new RuntimeSubagentGetWebCommand(
                    RequiredString(root, "sessionId"),
                    RequiredString(root, "subagentId")),
                "runtime/subagent/cancel" => new RuntimeSubagentCancelWebCommand(
                    RequiredString(root, "sessionId"),
                    RequiredString(root, "subagentId")),
                "runtime/commands/list" => new RuntimeCommandsListWebCommand(
                    RequiredNonNegativeInt32(root, "workspaceGeneration")),
                "runtime/memory/flush" => new MemoryFlushWebCommand(
                    RequiredString(root, "sessionId")),
                "memory/list" => ParseMemoryList(root),
                "memory/read" => ParseMemoryRead(root),
                "memory/write" => ParseMemoryWrite(root),
                "memory/delete" => ParseMemoryDelete(root),
                "worktree/create" => ParseWorktreeCreate(root),
                "worktree/list" => ParseWorktreeList(root),
                "worktree/show" => new WorktreeShowWebCommand(
                    RequiredNonNegativeInt32(root, "workspaceGeneration"),
                    RequiredBoundedString(root, "idOrPath", MaximumPathCharacters)),
                "worktree/apply" => ParseWorktreeApply(root),
                "worktree/remove" => new WorktreeRemoveWebCommand(
                    RequiredNonNegativeInt32(root, "workspaceGeneration"),
                    RequiredBoundedString(root, "idOrPath", MaximumPathCharacters),
                    RequiredBoolean(root, "force"),
                    RequiredBoolean(root, "dryRun")),
                "worktree/gc" => new WorktreeGcWebCommand(
                    RequiredNonNegativeInt32(root, "workspaceGeneration"),
                    RequiredBoolean(root, "dryRun"),
                    OptionalPositiveInt64(
                        root,
                        "maximumAgeSeconds",
                        MaximumWorktreeAgeSeconds),
                    RequiredBoolean(root, "force")),
                "session/export" => new SessionExportWebCommand(
                    RequiredMaintenanceRequestId(root),
                    RequiredBoundedString(root, "sessionId", MaximumSessionIdCharacters)),
                "session/import" => new SessionImportWebCommand(
                    RequiredMaintenanceRequestId(root)),
                "backup/create" => new BackupCreateWebCommand(
                    RequiredMaintenanceRequestId(root)),
                "backup/restore" => new BackupRestoreWebCommand(
                    RequiredMaintenanceRequestId(root)),
                "update/check" => new UpdateCheckWebCommand(
                    RequiredMaintenanceRequestId(root)),
                "update/apply" => new UpdateApplyWebCommand(
                    RequiredMaintenanceRequestId(root)),
                "cloud/profile/get" => new CloudProfileGetWebCommand(
                    RequiredMaintenanceRequestId(root)),
                "cloud/profile/save-local" => new CloudProfileSaveLocalWebCommand(
                    RequiredMaintenanceRequestId(root)),
                "cloud/profile/save-remote" => ParseCloudRemoteProfile(root),
                "cloud/pairing/export" => new CloudPairingExportWebCommand(
                    RequiredMaintenanceRequestId(root)),
                "cloud/pairing/import" => new CloudPairingImportWebCommand(
                    RequiredMaintenanceRequestId(root)),
                "cloud/session/upload" => new CloudSessionUploadWebCommand(
                    RequiredMaintenanceRequestId(root),
                    RequiredCloudIdentifier(root, "sessionId")),
                "cloud/session/download" => new CloudSessionDownloadWebCommand(
                    RequiredMaintenanceRequestId(root),
                    RequiredCloudIdentifier(root, "remoteSessionId")),
                "cloud/session/delete" => new CloudSessionDeleteWebCommand(
                    RequiredMaintenanceRequestId(root),
                    RequiredCloudIdentifier(root, "remoteSessionId")),
                "cloud/session/export" => new CloudSessionExportWebCommand(
                    RequiredMaintenanceRequestId(root),
                    RequiredCloudIdentifier(root, "sessionId")),
                "cloud/handoff/create" => new CloudHandoffCreateWebCommand(
                    RequiredMaintenanceRequestId(root),
                    RequiredCloudIdentifier(root, "sessionId"),
                    RequiredCloudIdentifier(root, "targetDeviceId")),
                "cloud/handoff/receive" => new CloudHandoffReceiveWebCommand(
                    RequiredMaintenanceRequestId(root)),
                "cloud/policy/get" => new CloudPolicyGetWebCommand(
                    RequiredMaintenanceRequestId(root)),
                "cloud/policy/update" => ParseCloudPolicyUpdate(root),
                "cloud/runner/register" => ParseCloudRunnerRegistration(root),
                "cloud/runner/queue" => ParseCloudRunnerQueue(root),
                "cloud/runner/claim" => ParseCloudRunnerClaim(root),
                "cloud/runner/complete" => ParseCloudRunnerComplete(root),
                "cloud/automation/list" => new CloudAutomationListWebCommand(
                    RequiredMaintenanceRequestId(root)),
                "cloud/automation/disable" => new CloudAutomationDisableWebCommand(
                    RequiredMaintenanceRequestId(root),
                    RequiredCloudIdentifier(root, "automationId")),
                "cloud/automation/create" => ParseCloudAutomationCreate(root),
                "extensions/list" => ParseExtensionsList(root),
                "extensions/action" => ParseExtensionsAction(root),
                "engine/prompt" => ParsePrompt(root),
                "engine/cancel" => new CancelWebCommand(RequiredString(root, "sessionId")),
                "windows/automation/execute" => ParseWindowsAutomation(root),
                "permission/respond" => ParsePermissionResponse(root),
                _ => throw Invalid("The web message command type is not supported."),
            };
        }
        catch (JsonException exception)
        {
            throw Invalid("The web message is not valid JSON.", exception);
        }
    }

    internal static string SerializeDocumentToken(string documentToken)
    {
        if (!IsDocumentToken(documentToken))
        {
            throw Invalid("The document token is invalid.");
        }

        return JsonSerializer.Serialize(
            new
            {
                schemaVersion = SchemaVersion,
                type = "host/document-token",
                documentToken,
            },
            SerializerOptions);
    }

    public static string SerializeEvent(WebEvent webEvent)
    {
        ArgumentNullException.ThrowIfNull(webEvent);

        object envelope = webEvent switch
        {
            ImageAttachmentsChangedWebEvent value => ProjectImageAttachmentsChanged(value),
            EngineStatusWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "engine/status",
                value.Status,
                value.Message,
                value.SessionId,
                value.EngineEpoch,
                capabilities = value.ExecutionProfiles is null
                    ? null
                    : new
                    {
                        executionProfiles = value.ExecutionProfiles.Select(ExecutionProfileName),
                        value.WslStrictReason,
                        value.ImagePrompts,
                        sessionModes = value.SessionModes?.Select(SessionModeName),
                    },
            },
            WorkspaceSelectedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "workspace/selected",
                value.Path,
                value.WorkspaceGeneration,
            },
            RecentWorkspacesChangedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "workspace/recent/changed",
                paths = value.Paths,
            },
            WorkspaceInstructionsListWebEvent value =>
                ProjectWorkspaceContextFiles(
                    "workspace/context/instructions/list",
                    value.RequestId,
                    value.WorkspaceGeneration,
                    query: null,
                    value.Files),
            WorkspaceFileReadWebEvent value => ProjectWorkspaceContextRead(value),
            WorkspaceInstructionsWriteWebEvent value =>
                ProjectWorkspaceContextWrite(value),
            WorkspaceFileSearchWebEvent value =>
                ProjectWorkspaceContextFiles(
                    "workspace/context/file/search",
                    value.RequestId,
                    value.WorkspaceGeneration,
                    value.Query,
                    value.Files),
            WorkspaceContextErrorWebEvent value => ProjectWorkspaceContextError(value),
            EngineCapabilitiesChangedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "engine/capabilities",
                value.SessionId,
                value.ImagePrompts,
                sessionModes = value.SessionModes.Select(SessionModeName),
            },
            CredentialStatusWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "credential/status",
                value.Status,
                value.Message,
            },
            ProviderStatusWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "provider/status",
                value.Status,
                value.BaseUrl,
                value.Model,
                value.Backend,
                value.AllowInsecureTransport,
                value.HasCredential,
                value.Message,
            },
            SessionUpdateWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "session/update",
                value.SessionId,
                value.UpdateKind,
                value.Text,
                value.Update,
                value.EngineEpoch,
            },
            PromptCompletedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "prompt/completed",
                value.SessionId,
                value.StopReason,
            },
            SessionModeChangedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "session/mode/changed",
                value.SessionId,
                mode = SessionModeName(value.Mode),
                value.PlanAvailable,
            },
            SessionListChangedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "session/list/changed",
                value.RequestId,
                sessions = value.Sessions.Select(session => new
                {
                    sessionId = session.SessionId.Value,
                    session.Title,
                    session.WorkspacePath,
                    session.CreatedAt,
                    session.UpdatedAt,
                    session.MessageCount,
                    session.ModelId,
                    session.ParentSessionId,
                    session.Branch,
                    session.WorktreeLabel,
                    session.SourceWorkspacePath,
                }),
                value.NextCursor,
            },
            SessionListErrorWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "session/list/error",
                value.RequestId,
                value.Message,
            },
            SessionActiveChangedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "session/active/changed",
                value.SessionId,
                value.WorkspacePath,
                value.EngineEpoch,
            },
            SessionRenamedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "session/renamed",
                value.RequestId,
                value.SessionId,
                value.Title,
            },
            SessionArchiveChangedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "session/archive/changed",
                value.RequestId,
                value.SessionId,
                value.Archived,
            },
            SessionOperationErrorWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "session/operation/error",
                value.RequestId,
                value.Operation,
                value.SessionId,
                value.Message,
            },
            SessionForkedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "session/forked",
                sessionId = value.Result.SessionId.Value,
                workspacePath = value.Result.WorkspacePath,
                value.Result.ParentSessionId,
                value.Result.ChatMessagesCopied,
                value.Result.UpdatesCopied,
                value.Result.PlanStateCopied,
                value.Result.ModelId,
            },
            SessionCompactedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "session/compacted",
                value.SessionId,
            },
            SessionRewindPointsWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "session/rewind/points",
                value.SessionId,
                points = value.Points.Select(point => new
                {
                    point.PromptIndex,
                    point.CreatedAt,
                    point.FileSnapshotCount,
                    point.HasFileChanges,
                    point.PromptPreview,
                }),
            },
            SessionRewindPointsErrorWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "session/rewind/points/error",
                value.SessionId,
                value.Message,
            },
            SessionRewoundWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "session/rewound",
                value.SessionId,
                value.Result.Success,
                value.Result.TargetPromptIndex,
                mode = RewindModeName(value.Result.Mode),
                value.Result.RevertedFiles,
                value.Result.CleanFiles,
                value.Result.Conflicts,
                value.Result.PromptText,
                value.Result.Error,
            },
            RuntimeDashboardChangedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "runtime/dashboard/changed",
                value.SessionId,
                backgroundTasks = value.BackgroundTasks.Select(ProjectBackgroundTask),
                subagents = value.Subagents.Select(ProjectSubagent),
            },
            RuntimeTaskKilledWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "runtime/task/killed",
                value.SessionId,
                value.TaskId,
                outcome = BackgroundTaskKillOutcomeName(value.Outcome),
            },
            RuntimeSubagentDetailWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "runtime/subagent/detail",
                value.SessionId,
                value.SubagentId,
                snapshot = value.Snapshot is null ? null : ProjectSubagent(value.Snapshot),
            },
            RuntimeSubagentCancelledWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "runtime/subagent/cancelled",
                value.SessionId,
                value.SubagentId,
                outcome = SubagentCancelOutcomeName(value.Result.Outcome),
                terminalStatus = value.Result.TerminalStatus is { } status
                    ? SubagentStatusName(status)
                    : null,
            },
            RuntimeDashboardErrorWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "runtime/dashboard/error",
                value.SessionId,
                value.Message,
                operation = RuntimeDashboardOperationName(value.Operation),
                value.ItemId,
            },
            RuntimeCommandsChangedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "runtime/commands/changed",
                value.WorkspaceGeneration,
                commands = value.Commands.Select(ProjectRuntimeCommand),
            },
            RuntimeCommandsErrorWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "runtime/commands/error",
                value.WorkspaceGeneration,
                value.Message,
            },
            WorktreeCreatedWebEvent value => ProjectWorktreeCreated(value),
            WorktreeListChangedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "worktree/list/changed",
                value.WorkspaceGeneration,
                worktrees = ProjectWorktrees(value.Worktrees),
            },
            WorktreeDetailWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "worktree/detail",
                value.WorkspaceGeneration,
                worktree = value.Worktree is null ? null : ProjectWorktree(value.Worktree),
            },
            WorktreeAppliedWebEvent value => ProjectWorktreeApplied(value),
            WorktreeRemovedWebEvent value => ProjectWorktreeRemoved(value),
            WorktreeGcCompletedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "worktree/gc/completed",
                value.WorkspaceGeneration,
                value.Result.DeadRemoved,
                value.Result.ExpiredRemoved,
                value.Result.SkippedAlive,
                value.Result.RemoveFailed,
            },
            WorktreeErrorWebEvent value => ProjectWorktreeError(value),
            SessionExportedWebEvent value => ProjectSessionExported(value),
            SessionImportedWebEvent value => ProjectSessionImported(value),
            BackupCompletedWebEvent value => ProjectBackupCompleted(value),
            UpdateStatusWebEvent value => ProjectUpdateStatus(value),
            BackgroundUpdateAvailableWebEvent value => ProjectBackgroundUpdateAvailable(value),
            MaintenanceErrorWebEvent value => ProjectMaintenanceOutcome(
                "maintenance/error",
                value.RequestId,
                value.Operation),
            MaintenanceCancelledWebEvent value => ProjectMaintenanceOutcome(
                "maintenance/cancelled",
                value.RequestId,
                value.Operation),
            CloudProfileWebEvent value => ProjectCloudProfile(value),
            CloudPairingCompletedWebEvent value => ProjectCloudPairing(value),
            CloudSessionUploadedWebEvent value => ProjectCloudSessionUploaded(value),
            CloudSessionImportedWebEvent value => ProjectCloudSessionImported(value),
            CloudSessionDeletedWebEvent value => ProjectCloudSessionDeleted(value),
            CloudSessionExportedWebEvent value => ProjectCloudSessionExported(value),
            CloudHandoffCreatedWebEvent value => ProjectCloudHandoffCreated(value),
            CloudHandoffsReceivedWebEvent value => ProjectCloudHandoffsReceived(value),
            CloudPolicyWebEvent value => ProjectCloudPolicy(value),
            CloudNotificationWebEvent value => ProjectCloudNotification(value),
            CloudRunnerRegisteredWebEvent value => ProjectCloudRunner(value),
            CloudRunnerQueuedWebEvent value => ProjectCloudRunnerQueued(value),
            CloudRunnerClaimedWebEvent value => ProjectCloudRunnerClaimed(value),
            CloudRunnerCompletedWebEvent value => ProjectCloudRunnerCompleted(value),
            CloudAutomationsWebEvent value => ProjectCloudAutomations(value),
            CloudAutomationDisabledWebEvent value => ProjectCloudAutomationDisabled(value),
            CloudAutomationCreatedWebEvent value => ProjectCloudAutomationCreated(value),
            CloudErrorWebEvent value => ProjectCloudOutcome(
                "cloud/error",
                value.RequestId,
                value.Operation),
            CloudCancelledWebEvent value => ProjectCloudOutcome(
                "cloud/cancelled",
                value.RequestId,
                value.Operation),
            ExtensionsCatalogWebEvent value => ProjectExtensionsCatalog(value),
            ExtensionsActionCompletedWebEvent value => ProjectExtensionsActionCompleted(value),
            ExtensionsErrorWebEvent value => ProjectExtensionsError(value),
            MemoryCapabilitiesWebEvent value => ProjectMemoryCapabilities(value),
            MemoryListedWebEvent value => ProjectMemoryListed(value),
            MemoryDocumentWebEvent value => ProjectMemoryDocument(value),
            MemoryMutationWebEvent value => ProjectMemoryMutation(value),
            MemoryErrorWebEvent value => ProjectMemoryError(value),
            MemoryFlushStatusWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "runtime/memory/status",
                value.SessionId,
                value.Status,
                value.Message,
            },
            UiPreferencesChangedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "ui/preferences/changed",
                value.Preferences.Language,
                value.Preferences.ComposerDraft,
                sessionMode = SessionModeName(value.Preferences.SessionMode),
                executionProfile = ExecutionProfileName(value.Preferences.ExecutionProfile),
                value.Preferences.NotificationsEnabled,
                value.Preferences.WindowsAutomationEnabled,
                value.Preferences.BackgroundUpdateChecksEnabled,
                value.Preferences.FullAccessEnabled,
                value.Preferences.FontScalePercent,
                value.RestartRequired,
            },
            PermissionRequestedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "permission/requested",
                value.RequestId,
                value.SessionId,
                value.ToolCallId,
                value.Title,
                value.ToolKind,
                value.RawInput,
                options = value.Options.Select(option => new
                {
                    option.OptionId,
                    option.Name,
                    kind = PermissionOptionKindName(option.Kind),
                }),
                value.Locations,
            },
            WindowsAutomationCompletedWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "windows/automation/completed",
                value.RequestId,
                action = WindowsAutomationActionName(value.Action),
                value.ProcessId,
                value.Target,
            },
            WindowsAutomationCancelledWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "windows/automation/cancelled",
                value.RequestId,
            },
            WindowsAutomationErrorWebEvent value => new
            {
                schemaVersion = SchemaVersion,
                type = "windows/automation/error",
                value.RequestId,
                value.Reason,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(webEvent)),
        };

        var json = JsonSerializer.Serialize(envelope, SerializerOptions);
        if (json.Length > MaximumMessageCharacters)
        {
            throw Invalid("The web event exceeds the maximum supported size.");
        }
        return json;
    }

    private static WorktreeCreateWebCommand ParseWorktreeCreate(JsonElement root) => new(
        RequiredNonNegativeInt32(root, "workspaceGeneration"),
        RequiredBoundedString(root, "sessionId", MaximumSessionIdCharacters),
        RequiredString(root, "copyMode") switch
        {
            "clean" => WorktreeCopyMode.Clean,
            "dirty" => WorktreeCopyMode.Dirty,
            _ => throw Invalid("The worktree copy mode is not supported."),
        },
        OptionalBoundedString(root, "gitReference", MaximumGitReferenceCharacters),
        RequiredBoolean(root, "copyIgnoredInBackground"),
        ParseBoundedStringArray(
            root,
            "ignoredSkipPatterns",
            MaximumSkipPatterns,
            MaximumSkipPatternCharacters),
        ParseOptionalWorktreeCreationType(root),
        OptionalBoundedString(root, "label", MaximumLabelCharacters),
        OptionalBoundedString(root, "destinationPath", MaximumPathCharacters));

    private static WorktreeListWebCommand ParseWorktreeList(JsonElement root)
    {
        if (!root.TryGetProperty("types", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("The web message is missing a valid 'types' field.");
        }

        var types = new List<WorktreeKind>();
        var seen = new HashSet<WorktreeKind>();
        foreach (var candidate in value.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.String || candidate.GetString() is not { } name)
            {
                throw Invalid("The worktree type filter is invalid.");
            }
            var type = name switch
            {
                "session" => WorktreeKind.Session,
                "ab" => WorktreeKind.Ab,
                "pool" => WorktreeKind.Pool,
                "fork" => WorktreeKind.Fork,
                "manual" => WorktreeKind.Manual,
                "subagent" => WorktreeKind.Subagent,
                _ => throw Invalid("The worktree type filter is not supported."),
            };
            if (!seen.Add(type))
            {
                throw Invalid("The worktree type filter contains a duplicate value.");
            }
            types.Add(type);
        }

        return new WorktreeListWebCommand(
            RequiredNonNegativeInt32(root, "workspaceGeneration"),
            RequiredBoolean(root, "includeAll"),
            types);
    }

    private static WorktreeApplyWebCommand ParseWorktreeApply(JsonElement root) => new(
        RequiredNonNegativeInt32(root, "workspaceGeneration"),
        RequiredBoundedString(root, "sessionId", MaximumSessionIdCharacters),
        RequiredBoundedString(root, "worktreePath", MaximumPathCharacters),
        RequiredString(root, "mode") switch
        {
            "overwrite" => WorktreeApplyMode.Overwrite,
            "merge" => WorktreeApplyMode.Merge,
            _ => throw Invalid("The worktree apply mode is not supported."),
        });

    private static WorktreeCreationType? ParseOptionalWorktreeCreationType(JsonElement root)
    {
        var value = OptionalNonEmptyString(root, "creationType");
        return value switch
        {
            null => null,
            "linked" => WorktreeCreationType.Linked,
            "standalone" => WorktreeCreationType.Standalone,
            "git" => WorktreeCreationType.Git,
            _ => throw Invalid("The worktree creation type is not supported."),
        };
    }

    private static IReadOnlyList<string> ParseBoundedStringArray(
        JsonElement root,
        string propertyName,
        int maximumCount,
        int maximumCharacters)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"The web message is missing a valid '{propertyName}' field.");
        }

        var items = new List<string>();
        foreach (var candidate in value.EnumerateArray())
        {
            if (items.Count == maximumCount ||
                candidate.ValueKind != JsonValueKind.String ||
                candidate.GetString() is not { } text ||
                string.IsNullOrWhiteSpace(text) ||
                text.Length > maximumCharacters)
            {
                throw Invalid($"The web message has an invalid '{propertyName}' field.");
            }
            items.Add(text);
        }
        return items;
    }

    private static object ProjectWorktreeCreated(WorktreeCreatedWebEvent value)
    {
        long aggregateCharacters = 0;
        ValidateWorktreeText(value.Result.SessionId.Value, ref aggregateCharacters);
        ValidateWorktreePath(value.Result.WorktreePath, ref aggregateCharacters);
        ValidateWorktreePath(value.Result.SourceGitRoot, ref aggregateCharacters);
        ValidateWorktreeText(value.Result.Commit, ref aggregateCharacters);
        return new
        {
            schemaVersion = SchemaVersion,
            type = "worktree/created",
            value.WorkspaceGeneration,
            status = WorktreeCreateStatusName(value.Result.Status),
            sessionId = value.Result.SessionId.Value,
            value.Result.WorktreePath,
            value.Result.SourceGitRoot,
            value.Result.Commit,
        };
    }

    private static IReadOnlyList<object> ProjectWorktrees(IReadOnlyList<WorktreeRecord> worktrees)
    {
        if (worktrees.Count > MaximumWorktreeRecords)
        {
            throw Invalid("The worktree list exceeds the maximum supported size.");
        }

        long aggregateCharacters = 0;
        foreach (var worktree in worktrees)
        {
            ValidateWorktreeRecord(worktree, ref aggregateCharacters);
        }
        return worktrees.Select(ProjectWorktreeUnchecked).ToArray();
    }

    private static object ProjectWorktree(WorktreeRecord worktree)
    {
        long aggregateCharacters = 0;
        ValidateWorktreeRecord(worktree, ref aggregateCharacters);
        return ProjectWorktreeUnchecked(worktree);
    }

    private static object ProjectWorktreeUnchecked(WorktreeRecord worktree) => new
    {
        worktree.Id,
        worktree.Path,
        worktree.SourceRepository,
        worktree.RepositoryName,
        kind = WorktreeKindName(worktree.Kind),
        creationType = WorktreeCreationTypeName(worktree.CreationType),
        worktree.GitReference,
        worktree.HeadCommit,
        sessionId = worktree.SessionId?.Value,
        worktree.CreatorProcessId,
        worktree.CreatedAt,
        worktree.LastAccessedAt,
        status = WorktreeRecordStatusName(worktree.Status),
        metadata = worktree.Metadata is null
            ? null
            : new { worktree.Metadata.Label, worktree.Metadata.UserProvided },
    };

    private static object ProjectWorktreeApplied(WorktreeAppliedWebEvent value)
    {
        if (value.Result.Files.Count > MaximumWorktreeChanges ||
            value.Result.Conflicts.Count > MaximumWorktreeChanges)
        {
            throw Invalid("The worktree apply result exceeds the maximum supported size.");
        }

        long aggregateCharacters = 0;
        ValidateWorktreePath(value.Result.GitRoot, ref aggregateCharacters);
        foreach (var file in value.Result.Files)
        {
            ValidateWorktreePath(file.Path, ref aggregateCharacters);
            ValidateWorktreePath(file.OldPath, ref aggregateCharacters);
            ValidateWorktreeText(file.Patch, ref aggregateCharacters);
            ValidateWorktreeText(file.OldText, ref aggregateCharacters);
            ValidateWorktreeText(file.NewText, ref aggregateCharacters);
        }
        foreach (var conflict in value.Result.Conflicts)
        {
            ValidateWorktreePath(conflict.Path, ref aggregateCharacters);
            ValidateWorktreeText(conflict.Base, ref aggregateCharacters);
            ValidateWorktreeText(conflict.Ours, ref aggregateCharacters);
            ValidateWorktreeText(conflict.Theirs, ref aggregateCharacters);
        }

        return new
        {
            schemaVersion = SchemaVersion,
            type = "worktree/applied",
            value.WorkspaceGeneration,
            status = WorktreeApplyStatusName(value.Result.Status),
            files = value.Result.Files.Select(file => new
            {
                file.Path,
                file.OldPath,
                changeType = WorktreeChangeTypeName(file.ChangeType),
                file.Staged,
                file.Additions,
                file.Deletions,
                file.Patch,
                file.PatchBytes,
                file.PatchLines,
                file.OldText,
                file.NewText,
            }).ToArray(),
            conflicts = value.Result.Conflicts.Select(conflict => new
            {
                conflict.Path,
                changeType = WorktreeChangeTypeName(conflict.ChangeType),
                conflict.Base,
                conflict.Ours,
                conflict.Theirs,
            }).ToArray(),
            value.Result.GitRoot,
        };
    }

    private static object ProjectWorktreeRemoved(WorktreeRemovedWebEvent value)
    {
        long aggregateCharacters = 0;
        ValidateWorktreeText(value.IdOrPath, ref aggregateCharacters);
        ValidateWorktreePath(value.Result.ResolvedPath, ref aggregateCharacters);
        return new
        {
            schemaVersion = SchemaVersion,
            type = "worktree/removed",
            value.WorkspaceGeneration,
            value.IdOrPath,
            value.Result.Removed,
            value.Result.ResolvedPath,
        };
    }

    private static object ProjectWorktreeError(WorktreeErrorWebEvent value)
    {
        long aggregateCharacters = 0;
        ValidateWorktreeText(value.Message, ref aggregateCharacters);
        ValidateWorktreeText(value.ItemId, ref aggregateCharacters);
        return new
        {
            schemaVersion = SchemaVersion,
            type = "worktree/error",
            value.WorkspaceGeneration,
            value.Message,
            operation = WorktreeOperationName(value.Operation),
            value.ItemId,
        };
    }

    private static object ProjectSessionExported(SessionExportedWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        ValidateMaintenanceString(value.SessionId, MaximumSessionIdCharacters, "session ID");
        ValidateSafeFileName(value.FileName);
        return new
        {
            schemaVersion = SchemaVersion,
            type = "session/exported",
            value.RequestId,
            value.SessionId,
            value.FileName,
        };
    }

    private static object ProjectSessionImported(SessionImportedWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        ValidateMaintenanceString(value.SessionId, MaximumSessionIdCharacters, "session ID");
        ValidateMaintenanceString(value.WorkspacePath, MaximumPathCharacters, "workspace path");
        return new
        {
            schemaVersion = SchemaVersion,
            type = "session/imported",
            value.RequestId,
            value.SessionId,
            value.WorkspacePath,
        };
    }

    private static object ProjectBackupCompleted(BackupCompletedWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        if (value.Operation is not ("create" or "restore") ||
            value.FileCount < 0 ||
            value.TotalBytes < 0)
        {
            throw Invalid("The maintenance result is invalid.");
        }
        return new
        {
            schemaVersion = SchemaVersion,
            type = "backup/completed",
            value.RequestId,
            value.Operation,
            value.FileCount,
            value.TotalBytes,
            value.RestartRequired,
        };
    }

    private static object ProjectUpdateStatus(UpdateStatusWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        if (value.Status is not ("checking" or "up-to-date" or "available" or
            "launching" or "unsupported" or "error") ||
            (value.Version is not null && !SemanticVersion.TryParse(value.Version, out _)))
        {
            throw Invalid("The update status is invalid.");
        }
        return new
        {
            schemaVersion = SchemaVersion,
            type = "update/status",
            value.RequestId,
            value.Status,
            value.Version,
        };
    }

    private static object ProjectBackgroundUpdateAvailable(
        BackgroundUpdateAvailableWebEvent value)
    {
        if (!SemanticVersion.TryParse(value.Version, out _))
        {
            throw Invalid("The background update version is invalid.");
        }
        return new
        {
            schemaVersion = SchemaVersion,
            type = "update/background-available",
            value.Version,
        };
    }

    private static object ProjectMaintenanceOutcome(
        string type,
        string requestId,
        string operation)
    {
        ValidateMaintenanceRequestId(requestId);
        if (operation is not ("session-export" or "session-import" or "backup-create" or
            "backup-restore" or "update-check" or "update-apply"))
        {
            throw Invalid("The maintenance operation is invalid.");
        }
        return new
        {
            schemaVersion = SchemaVersion,
            type,
            requestId,
            operation,
        };
    }

    private static void ValidateMaintenanceRequestId(string requestId)
    {
        if (!Guid.TryParseExact(requestId, "D", out var parsed) ||
            !string.Equals(requestId, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw Invalid("The maintenance request identifier is invalid.");
        }
    }

    private static object ProjectCloudProfile(CloudProfileWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        if (value.LocalOnly)
        {
            if (value.BaseUri is not null || value.TeamId is not null || value.DeviceId is not null ||
                value.HasAccessToken)
            {
                throw Invalid("The local-only cloud profile is invalid.");
            }
        }
        else
        {
            _ = new CloudConnectionProfile(
                new Uri(value.BaseUri!, UriKind.Absolute),
                value.TeamId!,
                value.DeviceId!);
        }
        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/profile",
            value.RequestId,
            value.LocalOnly,
            value.BaseUri,
            value.TeamId,
            value.DeviceId,
            value.HasAccessToken,
        };
    }

    private static object ProjectExtensionsCatalog(ExtensionsCatalogWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        ValidateExtensionSessionId(value.SessionId, required: true);
        if (value.McpServers.Count > MaximumExtensionListItems ||
            value.Skills.Count > MaximumExtensionListItems ||
            value.Hooks.Hooks.Count > MaximumExtensionListItems ||
            value.Plugins.Count > MaximumExtensionListItems ||
            value.Marketplace.Sources.Count > MaximumExtensionListItems)
        {
            throw Invalid("The extension catalog exceeds the maximum supported size.");
        }

        return new
        {
            schemaVersion = SchemaVersion,
            type = "extensions/catalog",
            value.RequestId,
            value.SessionId,
            mcp = new
            {
                servers = value.McpServers.Select(ProjectMcpServer).ToArray(),
            },
            skills = new
            {
                skills = value.Skills.Select(ProjectSkill).ToArray(),
                configuration = value.SkillsConfiguration is null
                    ? null
                    : ProjectSkillsConfiguration(value.SkillsConfiguration),
            },
            hooks = new
            {
                hooks = value.Hooks.Hooks.Select(ProjectHook).ToArray(),
                value.Hooks.ProjectTrusted,
                loadErrorCount = value.Hooks.LoadErrors.Count,
            },
            plugins = new
            {
                plugins = value.Plugins.Select(ProjectPlugin).ToArray(),
            },
            marketplace = new
            {
                sources = value.Marketplace.Sources.Select(ProjectMarketplaceSource).ToArray(),
            },
        };
    }

    private static object ProjectMcpServer(McpServerCatalogItem value)
    {
        ValidateExtensionIdentifier(value.Name, "MCP server name");
        return new
        {
            value.Name,
            value.DisplayName,
            source = EnumName(value.Source),
            value.SourceLabel,
            transport = EnumName(value.Transport),
            url = SafeExtensionUrl(value.Url),
            value.Scope,
            value.ScopeId,
            value.ScopeName,
            value.Command,
            arguments = value.Arguments.Take(MaximumExtensionListItems).Select(item => SafeExtensionText(item, MaximumExtensionTextCharacters)).ToArray(),
            environmentVariableNames = value.EnvironmentVariableNames.Take(MaximumExtensionListItems).Select(item =>
            {
                ValidateEnvironmentName(item, "environment variable name");
                return item;
            }).ToArray(),
            session = value.Session is null
                ? null
                : new
                {
                    value.Session.Enabled,
                    status = value.Session.Status is null ? null : EnumName(value.Session.Status.Value),
                    tools = value.Session.Tools.Take(MaximumExtensionListItems).Select(tool => new
                    {
                        Name = SafeExtensionText(tool.Name, MaximumExtensionNameCharacters),
                        DisplayName = SafeExtensionText(tool.DisplayName, MaximumExtensionTextCharacters),
                        Description = SafeExtensionText(tool.Description, MaximumExtensionTextCharacters),
                        tool.Enabled,
                    }).ToArray(),
                    value.Session.AuthRequired,
                },
        };
    }

    private static object ProjectSkill(SkillDescriptor value)
    {
        ValidateExtensionIdentifier(value.Name, "skill name");
        return new
        {
            value.Name,
            displayName = SafeExtensionText(value.DisplayName, MaximumExtensionTextCharacters),
            description = SafeExtensionText(value.Description, MaximumExtensionTextCharacters),
            paths = value.Paths.Take(MaximumExtensionListItems).Select(item => SafeExtensionText(item, MaximumPathCharacters)).ToArray(),
            path = SafeExtensionText(value.Path, MaximumPathCharacters),
            scope = EnumName(value.Scope),
            pluginName = SafeExtensionText(value.PluginName, MaximumExtensionNameCharacters),
            pluginVersion = SafeExtensionText(value.PluginVersion, MaximumExtensionNameCharacters),
            allowedTools = value.AllowedTools.Take(MaximumExtensionListItems).Select(item => SafeExtensionText(item, MaximumExtensionNameCharacters)).ToArray(),
            model = SafeExtensionText(value.Model, MaximumExtensionNameCharacters),
            effort = SafeExtensionText(value.Effort, MaximumExtensionNameCharacters),
            value.UserInvocable,
            value.DisableModelInvocation,
            value.Enabled,
        };
    }

    private static object ProjectSkillsConfiguration(SkillsConfiguration value) => new
    {
        paths = value.Paths.Take(MaximumExtensionListItems).Select(item => SafeExtensionText(item, MaximumPathCharacters)).ToArray(),
        ignoredPaths = value.IgnoredPaths.Take(MaximumExtensionListItems).Select(item => SafeExtensionText(item, MaximumPathCharacters)).ToArray(),
        value.TotalSkills,
    };

    private static object ProjectHook(HookDescriptor value) => new
    {
        name = SafeExtensionText(value.Name, MaximumExtensionNameCharacters),
        @event = EnumName(value.Event),
        handlerType = EnumName(value.HandlerType),
        matcher = SafeExtensionText(value.Matcher, MaximumExtensionTextCharacters),
        // Commands and URLs can contain inline credentials. The UI receives only
        // their presence; execution remains inside the sidecar.
        hasCommand = !string.IsNullOrWhiteSpace(value.Command),
        hasUrl = !string.IsNullOrWhiteSpace(value.Url),
        timeoutMs = checked((long)value.Timeout.TotalMilliseconds),
        sourceDirectory = SafeExtensionText(value.SourceDirectory, MaximumPathCharacters),
        value.Disabled,
    };

    private static object ProjectPlugin(PluginDescriptor value)
    {
        ValidateExtensionIdentifier(value.Id, "plugin ID");
        return new
        {
            name = SafeExtensionText(value.Name, MaximumExtensionNameCharacters),
            value.Id,
            root = SafeExtensionText(value.Root, MaximumPathCharacters),
            scope = EnumName(value.Scope),
            value.Trusted,
            value.Enabled,
            version = SafeExtensionText(value.Version, MaximumExtensionNameCharacters),
            description = SafeExtensionText(value.Description, MaximumExtensionTextCharacters),
            value.SkillCount,
            skillNames = value.SkillNames.Take(MaximumExtensionListItems).Select(item => SafeExtensionText(item, MaximumExtensionNameCharacters)).ToArray(),
            value.AgentCount,
            agentNames = value.AgentNames.Take(MaximumExtensionListItems).Select(item => SafeExtensionText(item, MaximumExtensionNameCharacters)).ToArray(),
            hookStatus = EnumName(value.HookStatus),
            value.HookCount,
            value.McpServerCount,
            mcpStatus = EnumName(value.McpStatus),
            marketplaceSource = SafeExtensionText(value.MarketplaceSource, MaximumPathCharacters),
            origin = value.Origin is null
                ? null
                : new
                {
                    type = EnumName(value.Origin.Kind),
                    marketplace = SafeExtensionText(value.Origin.Marketplace, MaximumPathCharacters),
                    sourceName = SafeExtensionText(value.Origin.SourceName, MaximumExtensionNameCharacters),
                },
            conflict = SafeExtensionText(value.Conflict, MaximumExtensionTextCharacters),
        };
    }

    private static object ProjectMarketplaceSource(MarketplaceSourceDescriptor value) => new
    {
        name = SafeExtensionText(value.Name, MaximumExtensionNameCharacters),
        kind = EnumName(value.Kind),
        source = SafeExtensionUrl(value.Source) ?? SafeExtensionText(value.Source, MaximumPathCharacters),
        plugins = value.Plugins.Take(MaximumExtensionListItems).Select(ProjectMarketplacePlugin).ToArray(),
    };

    private static object ProjectMarketplacePlugin(MarketplacePluginDescriptor value) => new
    {
        name = SafeExtensionText(value.Name, MaximumExtensionNameCharacters),
        source = SafeExtensionUrl(value.Target.Source) ?? SafeExtensionText(value.Target.Source, MaximumPathCharacters),
        version = SafeExtensionText(value.Version, MaximumExtensionNameCharacters),
        description = SafeExtensionText(value.Description, MaximumExtensionTextCharacters),
        category = SafeExtensionText(value.Category, MaximumExtensionNameCharacters),
        author = SafeExtensionText(value.Author, MaximumExtensionNameCharacters),
        tags = value.Tags.Take(MaximumExtensionListItems).Select(item => SafeExtensionText(item, MaximumExtensionNameCharacters)).ToArray(),
        keywords = value.Keywords.Take(MaximumExtensionListItems).Select(item => SafeExtensionText(item, MaximumExtensionNameCharacters)).ToArray(),
        domains = value.Domains.Take(MaximumExtensionListItems).Select(item => SafeExtensionText(item, MaximumExtensionNameCharacters)).ToArray(),
        homepage = SafeExtensionUrl(value.Homepage),
        relativePath = SafeExtensionText(value.Target.RelativePath, MaximumPathCharacters),
        skillCount = value.SkillCount,
        value.HasHooks,
        value.HasAgents,
        value.HasMcp,
        installStatus = EnumName(value.InstallStatus),
        installedVersion = SafeExtensionText(value.InstalledVersion, MaximumExtensionNameCharacters),
    };

    private static object ProjectExtensionsActionCompleted(ExtensionsActionCompletedWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        ValidateExtensionSessionId(value.SessionId, required: true);
        ValidateExtensionActionName(value.Action);
        return new
        {
            schemaVersion = SchemaVersion,
            type = "extensions/action/completed",
            value.RequestId,
            value.SessionId,
            scope = EnumName(value.Scope),
            value.Action,
            status = EnumName(value.Outcome.Status),
            message = SafeExtensionText(value.Outcome.Message, MaximumExtensionTextCharacters),
            value.Outcome.RequiresReload,
            value.Outcome.RequiresRestart,
        };
    }

    private static object ProjectExtensionsError(ExtensionsErrorWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        ValidateExtensionSessionId(value.SessionId, required: false);
        if (value.Scope is not null && value.Action is not null)
        {
            ValidateExtensionActionName(value.Action);
        }
        return new
        {
            schemaVersion = SchemaVersion,
            type = "extensions/error",
            value.RequestId,
            value.SessionId,
            scope = value.Scope is null ? null : EnumName(value.Scope.Value),
            value.Action,
            message = SafeExtensionText(value.Message, MaximumExtensionTextCharacters),
        };
    }

    private static void ValidateExtensionActionName(string value) =>
        ValidateExtensionText(value, MaximumExtensionNameCharacters, "extension action");

    private static string? SafeExtensionText(string? value, int maximum)
    {
        if (value is null)
        {
            return null;
        }
        if (value.Length > maximum)
        {
            return value[..maximum];
        }
        return value.Any(char.IsControl) ? null : value;
    }

    private static string? SafeExtensionUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 ||
            uri.Scheme is not ("https" or "http"))
        {
            return null;
        }
        return uri.GetLeftPart(UriPartial.Path);
    }

    private static string EnumName<T>(T value)
        where T : struct, Enum =>
        value switch
        {
            ExtensionScope scope => scope switch
            {
                ExtensionScope.Mcp => "mcp",
                ExtensionScope.Skills => "skills",
                ExtensionScope.Hooks => "hooks",
                ExtensionScope.Plugins => "plugins",
                ExtensionScope.Marketplace => "marketplace",
                _ => throw new ArgumentOutOfRangeException(nameof(value)),
            },
            McpServerSource source => source switch
            {
                McpServerSource.Managed => "managed",
                McpServerSource.Local => "local",
                _ => throw new ArgumentOutOfRangeException(nameof(value)),
            },
            McpServerTransportKind transport => transport switch
            {
                McpServerTransportKind.Http => "http",
                McpServerTransportKind.Stdio => "stdio",
                McpServerTransportKind.ManagedGateway => "managed_gateway",
                _ => throw new ArgumentOutOfRangeException(nameof(value)),
            },
            McpSessionStatus status => status switch
            {
                McpSessionStatus.Ready => "ready",
                McpSessionStatus.Initializing => "initializing",
                McpSessionStatus.Unavailable => "unavailable",
                _ => throw new ArgumentOutOfRangeException(nameof(value)),
            },
            SkillScope skillScope => skillScope.ToString().ToLowerInvariant(),
            HookEvent hookEvent => ToSnakeCase(hookEvent.ToString()),
            HookHandlerType hookHandler => hookHandler.ToString().ToLowerInvariant(),
            PluginScope pluginScope => pluginScope.ToString().ToLowerInvariant(),
            PluginHookStatus hookStatus => ToSnakeCase(hookStatus.ToString()),
            PluginMcpStatus mcpStatus => ToSnakeCase(mcpStatus.ToString()),
            PluginOriginKind originKind => ToSnakeCase(originKind.ToString()),
            MarketplaceSourceKind sourceKind => sourceKind.ToString().ToLowerInvariant(),
            MarketplaceInstallStatus installStatus => ToSnakeCase(installStatus.ToString()),
            ExtensionActionStatus actionStatus => ToSnakeCase(actionStatus.ToString()),
            _ => value.ToString(),
        };

    private static string ToSnakeCase(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character))
            {
                builder.Append('_');
            }
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static object ProjectCloudPairing(CloudPairingCompletedWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        if (value.Operation is not ("export" or "import"))
        {
            throw Invalid("The cloud pairing operation is invalid.");
        }
        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/pairing/completed",
            value.RequestId,
            value.Operation,
        };
    }

    private static object ProjectCloudSessionUploaded(CloudSessionUploadedWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        ValidateCloudIdentifier(value.SessionId, "session ID");
        if (value.Revision < 1)
        {
            throw Invalid("The cloud session revision is invalid.");
        }
        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/session/uploaded",
            value.RequestId,
            value.SessionId,
            value.Revision,
        };
    }

    private static object ProjectCloudSessionImported(CloudSessionImportedWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        ValidateCloudIdentifier(value.RemoteSessionId, "remote session ID");
        if (value.Found != (value.Revision is not null && value.ImportedSessionId is not null) ||
            value.Revision is < 1)
        {
            throw Invalid("The cloud session import result is invalid.");
        }
        if (value.ImportedSessionId is not null)
        {
            ValidateCloudIdentifier(value.ImportedSessionId, "imported session ID");
        }
        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/session/imported",
            value.RequestId,
            value.RemoteSessionId,
            value.Found,
            value.Revision,
            value.ImportedSessionId,
        };
    }

    private static object ProjectCloudSessionDeleted(CloudSessionDeletedWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        ValidateCloudIdentifier(value.RemoteSessionId, "remote session ID");
        if (value.Found != (value.Revision is not null) || value.Revision is < 1)
        {
            throw Invalid("The cloud session delete result is invalid.");
        }
        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/session/deleted",
            value.RequestId,
            value.RemoteSessionId,
            value.Found,
            value.Revision,
        };
    }

    private static object ProjectCloudSessionExported(CloudSessionExportedWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        ValidateCloudIdentifier(value.SessionId, "session ID");
        ValidateSafeFileName(value.FileName);
        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/session/exported",
            value.RequestId,
            value.SessionId,
            value.FileName,
        };
    }

    private static object ProjectCloudHandoffCreated(CloudHandoffCreatedWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        ValidateCloudIdentifier(value.HandoffId, "handoff ID");
        ValidateCloudIdentifier(value.SessionId, "session ID");
        ValidateCloudIdentifier(value.TargetDeviceId, "target device ID");
        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/handoff/created",
            value.RequestId,
            value.HandoffId,
            value.SessionId,
            value.TargetDeviceId,
        };
    }

    private static object ProjectCloudHandoffsReceived(CloudHandoffsReceivedWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        if (value.Imports.Count > 50)
        {
            throw Invalid("The cloud handoff import list is too large.");
        }
        foreach (var item in value.Imports)
        {
            ValidateCloudIdentifier(item.HandoffId, "handoff ID");
            ValidateCloudIdentifier(item.SourceDeviceId, "source device ID");
            ValidateCloudIdentifier(item.RemoteSessionId, "remote session ID");
            ValidateCloudIdentifier(item.ImportedSessionId, "imported session ID");
        }
        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/handoffs/received",
            value.RequestId,
            imports = value.Imports,
        };
    }

    private static object ProjectCloudPolicy(CloudPolicyWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        if (value.Version < 0 || value.MaximumConcurrentJobs is < 1 or
            > CloudPolicyLimits.MaximumConcurrentJobs ||
            value.AllowedExecutionProfiles.Count > 2 ||
            value.AllowedExecutionProfiles.Any(profile =>
                profile is not ("NativeProtected" or "WslStrict")) ||
            value.AllowedPluginPublishers.Count > CloudPolicyLimits.MaximumPluginPublishers)
        {
            throw Invalid("The cloud policy is invalid.");
        }
        foreach (var publisher in value.AllowedPluginPublishers)
        {
            ValidateCloudIdentifier(publisher, "plugin publisher");
        }
        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/policy",
            value.RequestId,
            value.Version,
            value.AllowedExecutionProfiles,
            value.RemoteRunnerEnabled,
            value.UiAutomationEnabled,
            value.MaximumConcurrentJobs,
            value.AllowedPluginPublishers,
        };
    }

    private static object ProjectCloudNotification(CloudNotificationWebEvent value)
    {
        switch (value.Kind)
        {
            case "handoff-changed":
            case "job-changed":
                ValidateCloudIdentifier(value.ResourceId!, "notification resource ID");
                if (value.PolicyVersion is not null)
                {
                    throw Invalid("A resource notification cannot include a policy version.");
                }
                break;
            case "policy-changed":
                if (value.ResourceId is not null || value.PolicyVersion is null or < 1)
                {
                    throw Invalid("The policy notification is invalid.");
                }
                break;
            default:
                throw Invalid("The cloud notification kind is invalid.");
        }

        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/notification",
            value.Kind,
            value.ResourceId,
            value.PolicyVersion,
        };
    }

    private static object ProjectCloudRunner(CloudRunnerRegisteredWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        ValidateCloudIdentifier(value.RunnerId, "runner ID");
        if (value.Capabilities.Count is < 1 or > 64)
        {
            throw Invalid("The cloud runner capabilities are invalid.");
        }
        foreach (var capability in value.Capabilities)
        {
            ValidateCloudIdentifier(capability, "runner capability");
        }
        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/runner/registered",
            value.RequestId,
            value.RunnerId,
            value.Capabilities,
        };
    }

    private static object ProjectCloudRunnerQueued(CloudRunnerQueuedWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        ValidateCloudIdentifier(value.JobId, "job ID");
        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/runner/queued",
            value.RequestId,
            value.JobId,
        };
    }

    private static object ProjectCloudRunnerClaimed(CloudRunnerClaimedWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        if (!value.Found)
        {
            if (value.JobId is not null || value.RequiredCapability is not null ||
                value.Task is not null || value.LeaseExpiresAt is not null ||
                value.ClaimHandle is not null)
            {
                throw Invalid("The cloud runner claim event is inconsistent.");
            }
        }
        else
        {
            ValidateCloudIdentifier(value.JobId!, "job ID");
            ValidateCloudIdentifier(value.ClaimHandle!, "runner claim handle");
            ValidateCloudIdentifier(value.RequiredCapability!, "runner capability");
            ValidateMaintenanceString(value.Task!, 64 * 1024, "runner task");
            if (value.LeaseExpiresAt is null)
            {
                throw Invalid("The cloud runner claim event has no lease expiry.");
            }
        }
        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/runner/claimed",
            value.RequestId,
            value.Found,
            value.ClaimHandle,
            value.JobId,
            value.RequiredCapability,
            value.Task,
            value.LeaseExpiresAt,
        };
    }

    private static object ProjectCloudRunnerCompleted(CloudRunnerCompletedWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        ValidateCloudIdentifier(value.ClaimHandle, "runner claim handle");
        ValidateCloudIdentifier(value.JobId, "job ID");
        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/runner/completed",
            value.RequestId,
            value.ClaimHandle,
            value.JobId,
        };
    }

    private static object ProjectCloudAutomations(CloudAutomationsWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        if (value.Automations.Count > 1_000)
        {
            throw Invalid("The cloud automation list is too large.");
        }
        foreach (var automation in value.Automations)
        {
            ValidateCloudIdentifier(automation.AutomationId, "automation ID");
            ValidateMaintenanceString(automation.Name, 256, "automation name");
            if (automation.IntervalSeconds < 1)
            {
                throw Invalid("The cloud automation interval is invalid.");
            }
        }
        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/automations",
            value.RequestId,
            value.Automations,
        };
    }

    private static object ProjectCloudAutomationDisabled(CloudAutomationDisabledWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        ValidateCloudIdentifier(value.AutomationId, "automation ID");
        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/automation/disabled",
            value.RequestId,
            value.AutomationId,
            value.Disabled,
        };
    }

    private static object ProjectCloudAutomationCreated(CloudAutomationCreatedWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        return new
        {
            schemaVersion = SchemaVersion,
            type = "cloud/automation/created",
            value.RequestId,
            automation = ProjectCloudAutomationSummary(value.Automation),
        };
    }

    private static object ProjectCloudAutomationSummary(CloudAutomationWebSummary value)
    {
        ValidateCloudIdentifier(value.AutomationId, "automation ID");
        ValidateMaintenanceString(value.Name, 256, "automation name");
        if (value.IntervalSeconds < 1)
        {
            throw Invalid("The cloud automation interval is invalid.");
        }
        return value;
    }

    private static object ProjectCloudOutcome(string type, string requestId, string operation)
    {
        ValidateMaintenanceRequestId(requestId);
        if (operation is not ("profile-get" or "profile-save-local" or
            "profile-save-remote" or "pairing-export" or "pairing-import" or
            "session-upload" or "session-download" or "session-delete" or
            "session-export" or "handoff-create" or
            "handoff-receive" or "policy-get" or "policy-update" or
            "runner-register" or "runner-queue" or "runner-claim" or
            "runner-complete" or "automation-list" or "automation-disable" or
            "automation-create"))
        {
            throw Invalid("The cloud operation is invalid.");
        }
        return new { schemaVersion = SchemaVersion, type, requestId, operation };
    }

    private static void ValidateCloudIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw Invalid($"The cloud {name} is invalid.");
        }
    }

    private static void ValidateMaintenanceString(string value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw Invalid($"The maintenance {name} is invalid.");
        }
    }

    private static void ValidateSafeFileName(string fileName)
    {
        ValidateMaintenanceString(fileName, MaximumFileNameCharacters, "file name");
        if (fileName is "." or ".." ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
            fileName.EndsWith(' ') ||
            fileName.EndsWith('.') ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw Invalid("The maintenance file name is invalid.");
        }
    }

    private static void ValidateWorktreeRecord(
        WorktreeRecord worktree,
        ref long aggregateCharacters)
    {
        ValidateWorktreeText(worktree.Id, ref aggregateCharacters);
        ValidateWorktreePath(worktree.Path, ref aggregateCharacters);
        ValidateWorktreePath(worktree.SourceRepository, ref aggregateCharacters);
        ValidateWorktreeText(worktree.RepositoryName, ref aggregateCharacters);
        ValidateWorktreeText(worktree.GitReference, ref aggregateCharacters);
        ValidateWorktreeText(worktree.HeadCommit, ref aggregateCharacters);
        ValidateWorktreeText(worktree.SessionId?.Value, ref aggregateCharacters);
        ValidateWorktreeText(worktree.Metadata?.Label, ref aggregateCharacters);
    }

    private static void ValidateWorktreePath(string? value, ref long aggregateCharacters)
    {
        if (value is not null && value.Length > MaximumPathCharacters)
        {
            throw Invalid("A worktree path exceeds the maximum supported size.");
        }
        ValidateWorktreeText(value, ref aggregateCharacters);
    }

    private static void ValidateWorktreeText(string? value, ref long aggregateCharacters)
    {
        if (value is null)
        {
            return;
        }
        if (value.Length > MaximumWorktreeTextCharacters)
        {
            throw Invalid("A worktree text field exceeds the maximum supported size.");
        }
        aggregateCharacters = checked(aggregateCharacters + value.Length);
        if (aggregateCharacters > MaximumWorktreeAggregateTextCharacters)
        {
            throw Invalid("The worktree text payload exceeds the maximum supported size.");
        }
    }

    private static SaveProviderWebCommand ParseProvider(JsonElement root)
    {
        var backend = RequiredString(root, "backend") switch
        {
            "chat_completions" => ProviderBackend.ChatCompletions,
            "responses" => ProviderBackend.Responses,
            _ => throw Invalid("The provider backend is not supported."),
        };
        var useExistingCredential = RequiredBoolean(root, "useExistingCredential");
        var replaceCredential = RequiredBoolean(root, "replaceCredential");
        if (useExistingCredential == replaceCredential)
        {
            throw Invalid("The provider credential intent is invalid.");
        }
        try
        {
            return new SaveProviderWebCommand(
                new ProviderProfile(
                    RequiredString(root, "baseUrl"),
                    RequiredString(root, "model"),
                    backend,
                    OptionalBoolean(root, "allowInsecureTransport")),
                useExistingCredential,
                replaceCredential);
        }
        catch (ArgumentException exception)
        {
            throw Invalid("The provider settings are invalid.", exception);
        }
    }

    private static CloudProfileSaveRemoteWebCommand ParseCloudRemoteProfile(JsonElement root)
    {
        var requestId = RequiredMaintenanceRequestId(root);
        var teamId = RequiredCloudIdentifier(root, "teamId");
        var deviceId = RequiredCloudIdentifier(root, "deviceId");
        try
        {
            var profile = new CloudConnectionProfile(
                new Uri(RequiredBoundedString(root, "baseUri", 2_048), UriKind.Absolute),
                teamId,
                deviceId);
            return new CloudProfileSaveRemoteWebCommand(
                requestId,
                profile.BaseUri!,
                profile.TeamId!,
                profile.DeviceId!);
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            throw Invalid("The cloud profile is invalid.", exception);
        }
    }

    private static CloudPolicyUpdateWebCommand ParseCloudPolicyUpdate(JsonElement root)
    {
        var executionProfiles = ParseBoundedStringArray(
            root,
            "allowedExecutionProfiles",
            maximumCount: 2,
            maximumCharacters: 32);
        if (executionProfiles.Count == 0 ||
            executionProfiles.Distinct(StringComparer.Ordinal).Count() != executionProfiles.Count ||
            executionProfiles.Any(profile => profile is not ("NativeProtected" or "WslStrict")))
        {
            throw Invalid("The cloud execution profile policy is invalid.");
        }

        var publishers = ParseBoundedStringArray(
            root,
            "allowedPluginPublishers",
            maximumCount: CloudPolicyLimits.MaximumPluginPublishers,
            maximumCharacters: CloudPolicyLimits.MaximumPublisherIdCharacters);
        if (publishers.Distinct(StringComparer.Ordinal).Count() != publishers.Count)
        {
            throw Invalid("The cloud plugin publisher policy is invalid.");
        }

        return new CloudPolicyUpdateWebCommand(
            RequiredMaintenanceRequestId(root),
            executionProfiles,
            RequiredBoolean(root, "remoteRunnerEnabled"),
            RequiredBoolean(root, "uiAutomationEnabled"),
            RequiredPositiveInt32(
                root,
                "maximumConcurrentJobs",
                CloudPolicyLimits.MaximumConcurrentJobs),
            publishers);
    }

    private static CloudRunnerRegisterWebCommand ParseCloudRunnerRegistration(JsonElement root)
    {
        var capabilities = ParseBoundedStringArray(
            root,
            "capabilities",
            maximumCount: 64,
            maximumCharacters: 128);
        if (capabilities.Count == 0 ||
            capabilities.Distinct(StringComparer.Ordinal).Count() != capabilities.Count)
        {
            throw Invalid("The cloud runner capabilities are invalid.");
        }
        return new CloudRunnerRegisterWebCommand(
            RequiredMaintenanceRequestId(root),
            RequiredCloudIdentifier(root, "runnerId"),
            capabilities);
    }

    private static CloudRunnerQueueWebCommand ParseCloudRunnerQueue(JsonElement root) => new(
        RequiredMaintenanceRequestId(root),
        RequiredCloudIdentifier(root, "requiredCapability"),
        RequiredBoundedString(root, "task", 64 * 1024));

    private static CloudRunnerClaimWebCommand ParseCloudRunnerClaim(JsonElement root)
    {
        var leaseSeconds = RequiredPositiveInt32(root, "leaseSeconds", 600);
        if (leaseSeconds < 10)
        {
            throw Invalid("The cloud runner lease duration is invalid.");
        }
        return new(
            RequiredMaintenanceRequestId(root),
            RequiredCloudIdentifier(root, "runnerId"),
            leaseSeconds);
    }

    private static CloudRunnerCompleteWebCommand ParseCloudRunnerComplete(JsonElement root) => new(
        RequiredMaintenanceRequestId(root),
        RequiredCloudIdentifier(root, "claimHandle"),
        RequiredCloudIdentifier(root, "jobId"),
        RequiredBoundedString(root, "result", 256 * 1024));

    private static CloudAutomationCreateWebCommand ParseCloudAutomationCreate(JsonElement root) => new(
        RequiredMaintenanceRequestId(root),
        RequiredBoundedString(root, "name", 128),
        RequiredPositiveInt32(root, "intervalSeconds", 2_678_400),
        RequiredCloudIdentifier(root, "requiredCapability"),
        RequiredBoundedString(root, "task", 64 * 1024));

    private static MemoryListWebCommand ParseMemoryList(JsonElement root) => new(
        RequiredMaintenanceRequestId(root),
        RequiredNonNegativeInt32(root, "workspaceGeneration"),
        RequiredMemorySessionId(root));

    private static MemoryReadWebCommand ParseMemoryRead(JsonElement root) => new(
        RequiredMaintenanceRequestId(root),
        RequiredNonNegativeInt32(root, "workspaceGeneration"),
        RequiredMemorySessionId(root),
        RequiredMemoryFileId(root));

    private static MemoryWriteWebCommand ParseMemoryWrite(JsonElement root) => new(
        RequiredMaintenanceRequestId(root),
        RequiredNonNegativeInt32(root, "workspaceGeneration"),
        RequiredMemorySessionId(root),
        RequiredMemoryFileId(root),
        RequiredMemoryContent(root),
        RequiredBoolean(root, "confirmed"),
        OptionalMemoryConfirmationToken(root));

    private static MemoryDeleteWebCommand ParseMemoryDelete(JsonElement root) => new(
        RequiredMaintenanceRequestId(root),
        RequiredNonNegativeInt32(root, "workspaceGeneration"),
        RequiredMemorySessionId(root),
        RequiredMemoryFileId(root),
        RequiredBoolean(root, "confirmed"),
        OptionalMemoryConfirmationToken(root));

    private static ExtensionsListWebCommand ParseExtensionsList(JsonElement root)
    {
        var requestId = RequiredMaintenanceRequestId(root);
        var sessionId = OptionalBoundedString(root, "sessionId", MaximumSessionIdCharacters);
        ValidateExtensionSessionId(sessionId, required: false);
        var useCache = root.TryGetProperty("useCache", out _)
            ? OptionalBoolean(root, "useCache")
            : true;
        return new(
            requestId,
            RequiredNonNegativeInt32(root, "workspaceGeneration"),
            sessionId,
            useCache);
    }

    private static ExtensionsActionWebCommand ParseExtensionsAction(JsonElement root)
    {
        var requestId = RequiredMaintenanceRequestId(root);
        var sessionId = RequiredBoundedString(root, "sessionId", MaximumSessionIdCharacters);
        ValidateExtensionSessionId(sessionId, required: true);
        var scope = RequiredString(root, "scope") switch
        {
            "mcp" => ExtensionScope.Mcp,
            "skills" => ExtensionScope.Skills,
            "hooks" => ExtensionScope.Hooks,
            "plugins" => ExtensionScope.Plugins,
            "marketplace" => ExtensionScope.Marketplace,
            _ => throw Invalid("The extension scope is not supported."),
        };
        var action = RequiredBoundedString(root, "action", MaximumExtensionNameCharacters);
        var payload = RequiredObject(root, "payload");
        if (payload.GetRawText().Length > MaximumExtensionPayloadCharacters)
        {
            throw Invalid("The extension action payload exceeds the maximum supported size.");
        }

        ValidateExtensionAction(scope, action, payload);
        return new(
            requestId,
            RequiredNonNegativeInt32(root, "workspaceGeneration"),
            sessionId,
            scope,
            action,
            RequiredBoolean(root, "confirmed"),
            payload.Clone());
    }

    private static void ValidateExtensionAction(
        ExtensionScope scope,
        string action,
        JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("The extension action payload must be an object.");
        }

        switch (scope)
        {
            case ExtensionScope.Mcp:
                switch (action)
                {
                    case "toggle":
                        ValidatePayloadProperties(payload, "serverName", "enabled");
                        ValidateExtensionIdentifier(RequiredPayloadString(payload, "serverName"), "MCP server name");
                        _ = RequiredPayloadBoolean(payload, "enabled");
                        return;
                    case "upsert_stdio":
                        ValidatePayloadProperties(payload, "serverName", "command", "args", "environment", "workingDirectory", "enabled", "startupTimeoutSeconds", "toolTimeoutSeconds", "toolTimeouts", "exposeImageBase64");
                        ValidateMcpStdioPayload(payload);
                        return;
                    case "upsert_http":
                        ValidatePayloadProperties(payload, "serverName", "url", "bearerTokenEnvironmentVariable", "headers", "oauthClientId", "oauthClientSecretEnvironmentVariable", "oauthScopes", "enabled", "startupTimeoutSeconds", "toolTimeoutSeconds", "toolTimeouts", "exposeImageBase64");
                        ValidateMcpHttpPayload(payload);
                        return;
                    case "delete":
                        ValidatePayloadProperties(payload, "serverName");
                        ValidateExtensionIdentifier(RequiredPayloadString(payload, "serverName"), "MCP server name");
                        return;
                    default:
                        throw Invalid("The MCP extension action is not supported.");
                }
            case ExtensionScope.Skills:
                switch (action)
                {
                    case "add_path":
                    case "remove_path":
                        ValidatePayloadProperties(payload, "path");
                        ValidateExtensionPath(RequiredPayloadString(payload, "path"), "skill path");
                        return;
                    case "reset":
                        ValidatePayloadProperties(payload);
                        return;
                    default:
                        throw Invalid("The skills extension action is not supported.");
                }
            case ExtensionScope.Hooks:
                switch (action)
                {
                    case "reload":
                    case "trust":
                    case "untrust":
                        ValidatePayloadProperties(payload);
                        return;
                    case "enable":
                    case "disable":
                        ValidatePayloadProperties(payload, "hookName");
                        ValidateExtensionIdentifier(RequiredPayloadString(payload, "hookName"), "hook name");
                        return;
                    case "add":
                    case "remove":
                        ValidatePayloadProperties(payload, "path");
                        ValidateExtensionPath(RequiredPayloadString(payload, "path"), "hook path");
                        return;
                    case "toggle_source":
                        ValidatePayloadProperties(payload, "hookNames", "disableSource");
                        ValidateRequiredIdentifierArray(payload, "hookNames", "hook name");
                        _ = RequiredPayloadBoolean(payload, "disableSource");
                        return;
                    default:
                        throw Invalid("The hooks extension action is not supported.");
                }
            case ExtensionScope.Plugins:
                switch (action)
                {
                    case "reload":
                        ValidatePayloadProperties(payload);
                        return;
                    case "enable":
                    case "disable":
                        ValidatePayloadProperties(payload, "pluginId");
                        ValidateExtensionIdentifier(RequiredPayloadString(payload, "pluginId"), "plugin ID");
                        return;
                    case "add":
                    case "remove":
                        ValidatePayloadProperties(payload, "path");
                        ValidateExtensionPath(RequiredPayloadString(payload, "path"), "plugin path");
                        return;
                    case "install":
                        ValidatePayloadProperties(payload, "source");
                        ValidatePluginSource(RequiredPayloadString(payload, "source"));
                        return;
                    case "update":
                        ValidatePayloadProperties(payload, "pluginId");
                        if (payload.TryGetProperty("pluginId", out _))
                        {
                            ValidateExtensionIdentifier(
                                RequiredPayloadString(payload, "pluginId"),
                                "plugin ID");
                        }
                        return;
                    default:
                        throw Invalid("The plugins extension action is not supported.");
                }
            case ExtensionScope.Marketplace:
                if (action == "refresh")
                {
                    ValidatePayloadProperties(payload, "source");
                    if (payload.TryGetProperty("source", out _))
                    {
                        ValidateMarketplaceSource(RequiredPayloadString(payload, "source"));
                    }
                    return;
                }
                if (action is not ("install" or "update" or "uninstall"))
                {
                    throw Invalid("The marketplace extension action is not supported.");
                }
                ValidatePayloadProperties(
                    payload,
                    "source",
                    "relativePath");
                ValidateMarketplaceSource(RequiredPayloadString(payload, "source"));
                ValidateMarketplaceRelativePath(RequiredPayloadString(payload, "relativePath"));
                return;
            default:
                throw Invalid("The extension scope is not supported.");
        }
    }

    private static void ValidateMcpStdioPayload(JsonElement payload)
    {
        ValidateExtensionIdentifier(RequiredPayloadString(payload, "serverName"), "MCP server name");
        ValidateExtensionText(RequiredPayloadString(payload, "command"), MaximumExtensionTextCharacters, "MCP command");
        ValidateOptionalStringArray(payload, "args", MaximumExtensionListItems, MaximumExtensionTextCharacters);
        ValidateEnvironmentReferences(payload, "environment");
        ValidateOptionalPath(payload, "workingDirectory", "MCP working directory");
        ValidateOptionalBoolean(payload, "enabled");
        ValidateOptionalTimeout(payload, "startupTimeoutSeconds");
        ValidateOptionalTimeout(payload, "toolTimeoutSeconds");
        ValidateToolTimeouts(payload);
        ValidateOptionalBoolean(payload, "exposeImageBase64");
    }

    private static void ValidateMcpHttpPayload(JsonElement payload)
    {
        ValidateExtensionIdentifier(RequiredPayloadString(payload, "serverName"), "MCP server name");
        ValidateExtensionUrl(RequiredPayloadString(payload, "url"), "MCP URL");
        ValidateOptionalEnvironmentName(payload, "bearerTokenEnvironmentVariable");
        ValidateHeaderReferences(payload, "headers");
        ValidateOptionalText(payload, "oauthClientId", MaximumExtensionNameCharacters, "OAuth client ID");
        ValidateOptionalEnvironmentName(payload, "oauthClientSecretEnvironmentVariable");
        ValidateOptionalStringArray(payload, "oauthScopes", 128, MaximumExtensionNameCharacters);
        ValidateOptionalBoolean(payload, "enabled");
        ValidateOptionalTimeout(payload, "startupTimeoutSeconds");
        ValidateOptionalTimeout(payload, "toolTimeoutSeconds");
        ValidateToolTimeouts(payload);
        ValidateOptionalBoolean(payload, "exposeImageBase64");
    }

    private static void ValidateToolTimeouts(JsonElement payload)
    {
        if (!payload.TryGetProperty("toolTimeouts", out var value))
        {
            return;
        }
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() > MaximumExtensionListItems)
        {
            throw Invalid("The MCP tool timeout map is invalid.");
        }
        foreach (var property in value.EnumerateObject())
        {
            ValidateExtensionIdentifier(property.Name, "MCP tool name");
            if (property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetUInt64(out var seconds) || seconds is 0 or > 86_400)
            {
                throw Invalid("The MCP tool timeout map is invalid.");
            }
        }
    }

    private static void ValidateEnvironmentReferences(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var value))
        {
            return;
        }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumExtensionListItems)
        {
            throw Invalid($"The extension field '{propertyName}' is invalid.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Invalid($"The extension field '{propertyName}' is invalid.");
            }
            ValidatePayloadProperties(item, "name", "sourceVariable");
            var name = RequiredPayloadString(item, "name");
            ValidateEnvironmentName(name, "environment variable name");
            if (!names.Add(name))
            {
                throw Invalid($"The extension field '{propertyName}' contains a duplicate name.");
            }
            ValidateEnvironmentName(RequiredPayloadString(item, "sourceVariable"), "environment source variable");
        }
    }

    private static void ValidateHeaderReferences(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var value))
        {
            return;
        }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumExtensionListItems)
        {
            throw Invalid($"The extension field '{propertyName}' is invalid.");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Invalid($"The extension field '{propertyName}' is invalid.");
            }
            ValidatePayloadProperties(item, "name", "sourceVariable");
            var name = RequiredPayloadString(item, "name");
            ValidateExtensionText(name, 256, "header name");
            if (name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) ||
                !names.Add(name))
            {
                throw Invalid($"The extension field '{propertyName}' is invalid.");
            }
            ValidateEnvironmentName(RequiredPayloadString(item, "sourceVariable"), "environment source variable");
        }
    }

    private static void ValidateOptionalEnvironmentName(JsonElement payload, string propertyName)
    {
        if (payload.TryGetProperty(propertyName, out _))
        {
            ValidateEnvironmentName(RequiredPayloadString(payload, propertyName), propertyName);
        }
    }

    private static void ValidateOptionalPath(JsonElement payload, string propertyName, string name)
    {
        if (payload.TryGetProperty(propertyName, out _))
        {
            ValidateExtensionPath(RequiredPayloadString(payload, propertyName), name);
        }
    }

    private static void ValidateOptionalTimeout(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var value))
        {
            return;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var seconds) || seconds is 0 or > 86_400)
        {
            throw Invalid($"The extension field '{propertyName}' is invalid.");
        }
    }

    private static void ValidateOptionalBoolean(JsonElement payload, string propertyName)
    {
        if (payload.TryGetProperty(propertyName, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid($"The extension field '{propertyName}' is invalid.");
        }
    }

    private static void ValidateOptionalText(JsonElement payload, string propertyName, int maximum, string name)
    {
        if (payload.TryGetProperty(propertyName, out _))
        {
            ValidateExtensionText(RequiredPayloadString(payload, propertyName), maximum, name);
        }
    }

    private static void ValidateOptionalStringArray(JsonElement payload, string propertyName, int maximumCount, int maximumCharacters)
    {
        if (!payload.TryGetProperty(propertyName, out var value))
        {
            return;
        }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximumCount)
        {
            throw Invalid($"The extension field '{propertyName}' is invalid.");
        }
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw Invalid($"The extension field '{propertyName}' is invalid.");
            }
            ValidateExtensionText(item.GetString()!, maximumCharacters, propertyName);
        }
    }

    private static void ValidateRequiredIdentifierArray(
        JsonElement payload,
        string propertyName,
        string itemName)
    {
        if (!payload.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() is 0 or > MaximumExtensionListItems)
        {
            throw Invalid($"The extension field '{propertyName}' is invalid.");
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text)
            {
                throw Invalid($"The extension field '{propertyName}' is invalid.");
            }
            ValidateExtensionIdentifier(text, itemName);
            if (!values.Add(text))
            {
                throw Invalid($"The extension field '{propertyName}' contains a duplicate value.");
            }
        }
    }

    private static JsonElement RequiredObject(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }
        throw Invalid($"The web message is missing a valid '{propertyName}' field.");
    }

    private static string RequiredPayloadString(JsonElement payload, string propertyName)
    {
        if (payload.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } text &&
            !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
        throw Invalid($"The extension payload is missing a valid '{propertyName}' field.");
    }

    private static bool RequiredPayloadBoolean(JsonElement payload, string propertyName)
    {
        if (payload.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }
        throw Invalid($"The extension payload is missing a valid '{propertyName}' field.");
    }

    private static void ValidatePayloadProperties(JsonElement payload, params string[] allowed) =>
        ValidateAllowedProperties(payload, allowed);

    private static void ValidateExtensionSessionId(string? value, bool required)
    {
        if (value is null)
        {
            if (required)
            {
                throw Invalid("The extension session ID is required.");
            }
            return;
        }
        ValidateExtensionText(value, MaximumSessionIdCharacters, "session ID");
    }

    private static void ValidateExtensionIdentifier(string value, string name)
    {
        ValidateExtensionText(value, MaximumExtensionNameCharacters, name);
        if (value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_' or '.' or ':' or '/' or '[' or ']' or '@')) ||
            value.Split(['/', '\\']).Any(static part => part is ".." or "."))
        {
            throw Invalid($"The extension {name} is invalid.");
        }
    }

    private static void ValidateExtensionPath(string value, string name)
    {
        ValidateExtensionText(value, MaximumPathCharacters, name);
        if (value.Split(['/', '\\']).Any(static part => part is ".." or "."))
        {
            throw Invalid($"The extension {name} is invalid.");
        }
    }

    private static void ValidateExtensionText(string value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximum ||
            value.Any(character =>
                char.IsControl(character) ||
                char.GetUnicodeCategory(character) is
                    UnicodeCategory.Format or
                    UnicodeCategory.LineSeparator or
                    UnicodeCategory.ParagraphSeparator))
        {
            throw Invalid($"The extension {name} is invalid.");
        }
    }

    private static void ValidateEnvironmentName(string value, string name)
    {
        ValidateExtensionText(value, 128, name);
        if (!char.IsAsciiLetter(value[0]) && value[0] != '_')
        {
            throw Invalid($"The extension {name} is invalid.");
        }
        if (value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw Invalid($"The extension {name} is invalid.");
        }
    }

    private static void ValidateExtensionUrl(string value, string name)
    {
        ValidateExtensionText(value, 2_048, name);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length > 0 ||
            uri.Fragment.Length > 0 ||
            (uri.Scheme is not ("https" or "http")) ||
            (uri.Scheme == "http" && !IsLoopback(uri.Host)))
        {
            throw Invalid($"The extension {name} is invalid.");
        }
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
        string.Equals(host, "::1", StringComparison.Ordinal);

    private static void ValidateMarketplaceSource(string value)
    {
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("Marketplace file sources are not supported by the web bridge.");
        }
        if (Path.IsPathRooted(value))
        {
            ValidateExtensionPath(value, "marketplace source");
            return;
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is not ("https" or "http") ||
                (uri.Scheme == "http" && !IsLoopback(uri.Host)) ||
                uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            {
                throw Invalid("The marketplace source is invalid.");
            }
            return;
        }
        ValidateExtensionPath(value, "marketplace source");
    }

    private static void ValidatePluginSource(string value)
    {
        ValidateExtensionText(value, 8_192, "plugin source");
        if (value.Contains("git@", StringComparison.Ordinal))
        {
            var colon = value.IndexOf(':', 4);
            if (!value.StartsWith("git@", StringComparison.Ordinal) ||
                colon <= 4 ||
                colon == value.Length - 1 ||
                value[4..colon].Any(character =>
                    char.IsWhiteSpace(character) || character is '/' or '\\' or '@' or '#' or '?'))
            {
                throw Invalid("The plugin Git source is invalid.");
            }
            return;
        }
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("Plugin file URI sources are not supported by the web bridge.");
        }
        if (Path.IsPathRooted(value))
        {
            ValidateExtensionPath(value, "plugin source");
            return;
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is not ("https" or "http") ||
                (uri.Scheme == "http" && !IsLoopback(uri.Host)) ||
                uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            {
                throw Invalid("The plugin source is invalid.");
            }
            return;
        }
        ValidateExtensionPath(value, "plugin source");
    }

    private static void ValidateMarketplaceRelativePath(string value)
    {
        ValidateExtensionText(value, MaximumPathCharacters, "marketplace relative path");
        if (Path.IsPathRooted(value) || value.Split(['/', '\\']).Any(static part => part is ".." or "."))
        {
            throw Invalid("The marketplace relative path is unsafe.");
        }
    }

    private static PromptWebCommand ParsePrompt(JsonElement root)
    {
        var profile = ParseExecutionProfile(RequiredString(root, "executionProfile"));
        var sessionMode = RequiredString(root, "sessionMode") switch
        {
            "default" => SessionMode.Default,
            "plan" => SessionMode.Plan,
            _ => throw Invalid("The session mode is not supported."),
        };

        var attachments = ParsePromptAttachmentReferences(root);
        var text = RequiredStringAllowEmpty(root, "text");
        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
        {
            throw Invalid("The prompt must contain text or an image attachment.");
        }

        return new(
            text,
            profile,
            OptionalBoolean(root, "nativeRiskAcknowledged"),
            RequiredNonNegativeInt32(root, "workspaceGeneration"),
            sessionMode,
            AttachmentReferenceItems: attachments);
    }

    private static IReadOnlyList<NativeImageAttachmentReference>
        ParsePromptAttachmentReferences(JsonElement root)
    {
        if (!root.TryGetProperty("attachments", out var value))
        {
            return [];
        }
        if (value.ValueKind is not JsonValueKind.Array)
        {
            throw Invalid("The prompt attachments field must be an array.");
        }

        var attachments = new List<NativeImageAttachmentReference>();
        foreach (var candidate in value.EnumerateArray())
        {
            if (candidate.ValueKind is not JsonValueKind.Object)
            {
                throw Invalid("Each prompt attachment must be an object.");
            }
            ValidateAllowedProperties(candidate, ["token", "name", "mimeType", "size"]);
            attachments.Add(new NativeImageAttachmentReference(
                RequiredImageAttachmentToken(candidate, "token"),
                RequiredBoundedString(
                    candidate,
                    "name",
                    NativeImageAttachmentStore.MaximumAttachmentNameLength),
                RequiredString(candidate, "mimeType"),
                RequiredPositiveInt64(
                    candidate,
                    "size",
                    NativeImageAttachmentStore.MaximumAttachmentBytes)));
        }

        try
        {
            ValidateImageAttachmentReferences(attachments);
        }
        catch (InvalidDataException exception)
        {
            throw Invalid("The prompt attachments are invalid.", exception);
        }
        return attachments;
    }

    private static DiscardImageAttachmentsWebCommand ParseDiscardImageAttachments(
        JsonElement root)
    {
        if (!root.TryGetProperty("tokens", out var value) ||
            value.ValueKind is not JsonValueKind.Array)
        {
            throw Invalid("The attachment discard tokens must be an array.");
        }

        var tokens = value.EnumerateArray()
            .Select(candidate => RequiredImageAttachmentToken(candidate))
            .ToArray();
        if (tokens.Length > NativeImageAttachmentStore.MaximumAttachmentCount ||
            tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Length)
        {
            throw Invalid("The attachment discard token list is invalid.");
        }
        return new DiscardImageAttachmentsWebCommand(tokens);
    }

    private static SaveUiPreferencesWebCommand ParseUiPreferences(JsonElement root)
    {
        try
        {
            return new SaveUiPreferencesWebCommand(new UiPreferences(
                RequiredString(root, "language"),
                RequiredStringAllowEmpty(root, "composerDraft"),
                RequiredString(root, "sessionMode") switch
                {
                    "default" => SessionMode.Default,
                    "plan" => SessionMode.Plan,
                    _ => throw Invalid("The session mode is not supported."),
                },
                ParseExecutionProfile(RequiredString(root, "executionProfile")),
                RequiredBoolean(root, "notificationsEnabled"),
                RequiredBoolean(root, "windowsAutomationEnabled"),
                RequiredBoolean(root, "backgroundUpdateChecksEnabled"),
                RequiredBoolean(root, "fullAccessEnabled"),
                RequiredNonNegativeInt32(root, "fontScalePercent")).Validate());
        }
        catch (ArgumentException exception)
        {
            throw Invalid("The UI preferences are invalid.", exception);
        }
    }

    private static SessionListWebCommand ParseSessionList(JsonElement root) => new(
        RequiredMaintenanceRequestId(root),
        RequiredStringAllowEmpty(root, "query"),
        OptionalNonEmptyString(root, "cursor"),
        RequiredPageSize(root, "limit"),
        OptionalBoolean(root, "archived"));

    private static SessionOpenWebCommand ParseSessionOpen(JsonElement root) => new(
        RequiredString(root, "sessionId"),
        RequiredString(root, "workspacePath"),
        ParseExecutionProfile(RequiredString(root, "executionProfile")));

    private static SessionNewWebCommand ParseSessionNew(JsonElement root) => new(
        ParseExecutionProfile(RequiredString(root, "executionProfile")));

    private static SessionRenameWebCommand ParseSessionRename(JsonElement root) => new(
        RequiredMaintenanceRequestId(root),
        RequiredString(root, "sessionId"),
        RequiredString(root, "title"),
        RequiredString(root, "workspacePath"));

    private static SessionArchiveWebCommand ParseSessionArchive(JsonElement root) => new(
        RequiredMaintenanceRequestId(root),
        RequiredString(root, "sessionId"),
        RequiredBoolean(root, "archived"));

    private static SessionForkWebCommand ParseSessionFork(JsonElement root) => new(
        RequiredString(root, "sessionId"),
        RequiredString(root, "sourceWorkspacePath"),
        RequiredString(root, "targetWorkspacePath"),
        OptionalNonNegativeInt32(root, "targetPromptIndex"));

    private static SessionCompactWebCommand ParseSessionCompact(JsonElement root) => new(
        RequiredString(root, "sessionId"),
        OptionalNonEmptyString(root, "userContext"));

    private static SessionRewindWebCommand ParseSessionRewind(JsonElement root) => new(
        RequiredString(root, "sessionId"),
        RequiredNonNegativeInt32(root, "targetPromptIndex"),
        RequiredString(root, "mode") switch
        {
            "all" => SessionRewindMode.All,
            "conversation_only" => SessionRewindMode.ConversationOnly,
            "files_only" => SessionRewindMode.FilesOnly,
            _ => throw Invalid("The session rewind mode is not supported."),
        },
        RequiredBoolean(root, "force"));

    private static ExecutionProfile ParseExecutionProfile(string value) => value switch
    {
        "NativeProtected" => ExecutionProfile.NativeProtected,
        "WslStrict" => ExecutionProfile.WslStrict,
        _ => throw Invalid("The execution profile is not supported."),
    };

    private static PermissionRespondWebCommand ParsePermissionResponse(JsonElement root)
    {
        var requestId = RequiredString(root, "requestId");
        var outcome = RequiredString(root, "outcome");
        return outcome switch
        {
            "selected" => new PermissionRespondWebCommand(
                requestId,
                PermissionDecision.Selected(RequiredString(root, "optionId"))),
            "cancelled" when !root.TryGetProperty("optionId", out _) =>
                new PermissionRespondWebCommand(requestId, PermissionDecision.Cancelled),
            "cancelled" => throw Invalid(
                "A cancelled permission response must not include an 'optionId' field."),
            _ => throw Invalid("The permission response outcome is not supported."),
        };
    }

    private static WindowsAutomationWebCommand ParseWindowsAutomation(JsonElement root)
    {
        var action = RequiredString(root, "action") switch
        {
            "focus-window" => WindowsAutomationAction.FocusWindow,
            "invoke" => WindowsAutomationAction.Invoke,
            "set-value" => WindowsAutomationAction.SetValue,
            _ => throw Invalid("The Windows Automation action is not supported."),
        };
        string? value = null;
        if (root.TryGetProperty("value", out _))
        {
            value = RequiredStringAllowEmpty(root, "value");
            if (value.Length > 8 * 1024)
            {
                throw Invalid("The Windows Automation value is invalid.");
            }
        }

        try
        {
            return new WindowsAutomationWebCommand(
                RequiredMaintenanceRequestId(root),
                action,
                RequiredPositiveInt32(root, "processId", int.MaxValue),
                OptionalBoundedString(root, "automationId", 256),
                OptionalBoundedString(root, "name", 256),
                value);
        }
        catch (ArgumentException exception)
        {
            throw Invalid("The Windows Automation request is invalid.", exception);
        }
    }

    private static string PermissionOptionKindName(PermissionOptionKind kind) => kind switch
    {
        PermissionOptionKind.AllowOnce => "allow_once",
        PermissionOptionKind.AllowAlways => "allow_always",
        PermissionOptionKind.RejectOnce => "reject_once",
        PermissionOptionKind.RejectAlways => "reject_always",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string WindowsAutomationActionName(WindowsAutomationAction action) =>
        action switch
        {
            WindowsAutomationAction.FocusWindow => "focus-window",
            WindowsAutomationAction.Invoke => "invoke",
            WindowsAutomationAction.SetValue => "set-value",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    private static string ExecutionProfileName(ExecutionProfile profile) => profile switch
    {
        ExecutionProfile.NativeProtected => "NativeProtected",
        ExecutionProfile.WslStrict => "WslStrict",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    private static string SessionModeName(SessionMode mode) => mode switch
    {
        SessionMode.Default => "default",
        SessionMode.Plan => "plan",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string RewindModeName(SessionRewindMode mode) => mode switch
    {
        SessionRewindMode.All => "all",
        SessionRewindMode.ConversationOnly => "conversation_only",
        SessionRewindMode.FilesOnly => "files_only",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static object ProjectBackgroundTask(BackgroundTaskSnapshot task) => new
    {
        task.TaskId,
        command = task.UserFacingCommand,
        task.WorkingDirectory,
        task.StartedAt,
        task.EndedAt,
        task.Output,
        task.Truncated,
        task.ExitCode,
        task.Signal,
        task.Completed,
        kind = BackgroundTaskKindName(task.Kind),
        task.ExplicitlyKilled,
        task.OwnerSessionId,
    };

    private static object ProjectSubagent(SubagentSnapshot subagent) => new
    {
        subagent.SubagentId,
        subagent.ParentSessionId,
        subagent.ChildSessionId,
        subagent.SubagentType,
        subagent.Description,
        subagent.StartedAt,
        durationMs = checked((long)subagent.Duration.TotalMilliseconds),
        status = SubagentStatusName(subagent.Status),
        subagent.TurnCount,
        subagent.ToolCallCount,
        subagent.TokensUsed,
        subagent.ContextWindowTokens,
        subagent.ContextUsagePercent,
        subagent.ToolsUsed,
        subagent.ErrorCount,
        subagent.Output,
        subagent.WorktreePath,
        subagent.FailureError,
        subagent.CancelReason,
        subagent.ForkContextSource,
        subagent.ForkParentPromptId,
        subagent.ResumedFrom,
    };

    private static string BackgroundTaskKindName(BackgroundTaskKind kind) => kind switch
    {
        BackgroundTaskKind.Bash => "bash",
        BackgroundTaskKind.Monitor => "monitor",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string BackgroundTaskKillOutcomeName(BackgroundTaskKillOutcome outcome) =>
        outcome switch
        {
            BackgroundTaskKillOutcome.Killed => "killed",
            BackgroundTaskKillOutcome.AlreadyExited => "already_exited",
            BackgroundTaskKillOutcome.NotFound => "not_found",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };

    private static string SubagentStatusName(SubagentStatus status) => status switch
    {
        SubagentStatus.Initializing => "initializing",
        SubagentStatus.Running => "running",
        SubagentStatus.Completed => "completed",
        SubagentStatus.Failed => "failed",
        SubagentStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string SubagentCancelOutcomeName(SubagentCancelOutcome outcome) => outcome switch
    {
        SubagentCancelOutcome.Cancelled => "cancelled",
        SubagentCancelOutcome.AlreadyFinished => "already_finished",
        SubagentCancelOutcome.NotFound => "not_found",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static string RuntimeDashboardOperationName(RuntimeDashboardOperation operation) =>
        operation switch
        {
            RuntimeDashboardOperation.Refresh => "refresh",
            RuntimeDashboardOperation.TaskKill => "task_kill",
            RuntimeDashboardOperation.SubagentGet => "subagent_get",
            RuntimeDashboardOperation.SubagentCancel => "subagent_cancel",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static object ProjectRuntimeCommand(RuntimeCommand command) => new
    {
        command.Name,
        command.Description,
        input = command.Input is null ? null : new { command.Input.Hint },
        skill = command.Skill is null
            ? null
            : new
            {
                scope = RuntimeSkillScopeName(command.Skill.Scope),
                command.Skill.Path,
            },
    };

    private static string RuntimeSkillScopeName(RuntimeSkillScope scope) => scope switch
    {
        RuntimeSkillScope.Local => "local",
        RuntimeSkillScope.Repo => "repo",
        RuntimeSkillScope.User => "user",
        RuntimeSkillScope.Plugin => "plugin",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    private static string WorktreeCreateStatusName(WorktreeCreateStatus status) => status switch
    {
        WorktreeCreateStatus.Creating => "creating",
        WorktreeCreateStatus.Exists => "exists",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string WorktreeKindName(WorktreeKind kind) => kind switch
    {
        WorktreeKind.Session => "session",
        WorktreeKind.Ab => "ab",
        WorktreeKind.Pool => "pool",
        WorktreeKind.Fork => "fork",
        WorktreeKind.Manual => "manual",
        WorktreeKind.Subagent => "subagent",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string WorktreeCreationTypeName(WorktreeCreationType creationType) =>
        creationType switch
        {
            WorktreeCreationType.Linked => "linked",
            WorktreeCreationType.Standalone => "standalone",
            WorktreeCreationType.Git => "git",
            _ => throw new ArgumentOutOfRangeException(nameof(creationType)),
        };

    private static string WorktreeRecordStatusName(WorktreeRecordStatus status) => status switch
    {
        WorktreeRecordStatus.Alive => "alive",
        WorktreeRecordStatus.Dead => "dead",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string WorktreeApplyStatusName(WorktreeApplyStatus status) => status switch
    {
        WorktreeApplyStatus.Success => "success",
        WorktreeApplyStatus.Conflicts => "conflicts",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string WorktreeChangeTypeName(WorktreeChangeType changeType) => changeType switch
    {
        WorktreeChangeType.Create => "create",
        WorktreeChangeType.Edit => "edit",
        WorktreeChangeType.Delete => "delete",
        WorktreeChangeType.Rename => "rename",
        WorktreeChangeType.Copy => "copy",
        WorktreeChangeType.TypeChange => "type_change",
        WorktreeChangeType.Untracked => "untracked",
        _ => throw new ArgumentOutOfRangeException(nameof(changeType)),
    };

    private static string WorktreeOperationName(WorktreeOperation operation) => operation switch
    {
        WorktreeOperation.Create => "create",
        WorktreeOperation.List => "list",
        WorktreeOperation.Show => "show",
        WorktreeOperation.Apply => "apply",
        WorktreeOperation.Remove => "remove",
        WorktreeOperation.Gc => "gc",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static void ValidateCommandProperties(
        JsonElement root,
        string type,
        bool includesDocumentToken)
    {
        string[] properties = type switch
        {
            "ui/ready" => [],
            "ui/modal" => ["isOpen"],
            "ui/preferences/save" =>
                [
                    "language", "composerDraft", "sessionMode", "executionProfile",
                    "notificationsEnabled", "windowsAutomationEnabled",
                    "backgroundUpdateChecksEnabled", "fullAccessEnabled",
                    "fontScalePercent"
                ],
            "attachment/select" => ["requestId"],
            "attachment/discard" => ["tokens"],
            "workspace/select" => [],
            "workspace/recent/open" => ["path"],
            "workspace/recent/remove" => ["path"],
            "workspace/context/instructions/list" =>
                ["requestId", "workspaceGeneration"],
            "workspace/context/file/read" =>
                ["requestId", "workspaceGeneration", "relativePath"],
            "workspace/context/instructions/write" =>
                ["requestId", "workspaceGeneration", "relativePath", "content"],
            "workspace/context/file/search" =>
                ["requestId", "workspaceGeneration", "query"],
            "provider/save" =>
                [
                    "baseUrl", "model", "backend", "allowInsecureTransport",
                    "useExistingCredential", "replaceCredential"
                ],
            "session/list" => ["requestId", "query", "cursor", "limit", "archived"],
            "session/open" => ["sessionId", "workspacePath", "executionProfile"],
            "session/new" => ["executionProfile"],
            "session/rename" => ["requestId", "sessionId", "title", "workspacePath"],
            "session/archive" => ["requestId", "sessionId", "archived"],
            "session/fork" =>
                ["sessionId", "sourceWorkspacePath", "targetWorkspacePath", "targetPromptIndex"],
            "session/compact" => ["sessionId", "userContext"],
            "session/rewind/points" => ["sessionId"],
            "session/rewind" => ["sessionId", "targetPromptIndex", "mode", "force"],
            "runtime/dashboard/refresh" => ["sessionId"],
            "runtime/task/kill" => ["sessionId", "taskId"],
            "runtime/subagent/get" => ["sessionId", "subagentId"],
            "runtime/subagent/cancel" => ["sessionId", "subagentId"],
            "runtime/commands/list" => ["workspaceGeneration"],
            "runtime/memory/flush" => ["sessionId"],
            "memory/list" => ["requestId", "workspaceGeneration", "sessionId"],
            "memory/read" =>
                ["requestId", "workspaceGeneration", "sessionId", "fileId"],
            "memory/write" =>
                [
                    "requestId", "workspaceGeneration", "sessionId", "fileId", "content",
                    "confirmed", "confirmationToken",
                ],
            "memory/delete" =>
                [
                    "requestId", "workspaceGeneration", "sessionId", "fileId", "confirmed",
                    "confirmationToken",
                ],
            "worktree/create" =>
                [
                    "workspaceGeneration",
                    "sessionId",
                    "copyMode",
                    "gitReference",
                    "copyIgnoredInBackground",
                    "ignoredSkipPatterns",
                    "creationType",
                    "label",
                    "destinationPath",
                ],
            "worktree/list" => ["workspaceGeneration", "includeAll", "types"],
            "worktree/show" => ["workspaceGeneration", "idOrPath"],
            "worktree/apply" => ["workspaceGeneration", "sessionId", "worktreePath", "mode"],
            "worktree/remove" => ["workspaceGeneration", "idOrPath", "force", "dryRun"],
            "worktree/gc" =>
                ["workspaceGeneration", "dryRun", "maximumAgeSeconds", "force"],
            "session/export" => ["requestId", "sessionId"],
            "session/import" or "backup/create" or "backup/restore" or
                "update/check" or "update/apply" => ["requestId"],
            "cloud/profile/get" or "cloud/profile/save-local" or
                "cloud/pairing/export" or "cloud/pairing/import" or
                "cloud/handoff/receive" or "cloud/policy/get" or
                "cloud/automation/list" => ["requestId"],
            "cloud/profile/save-remote" => ["requestId", "baseUri", "teamId", "deviceId"],
            "cloud/session/upload" => ["requestId", "sessionId"],
            "cloud/session/download" => ["requestId", "remoteSessionId"],
            "cloud/session/delete" => ["requestId", "remoteSessionId"],
            "cloud/session/export" => ["requestId", "sessionId"],
            "cloud/handoff/create" => ["requestId", "sessionId", "targetDeviceId"],
            "cloud/policy/update" =>
                [
                    "requestId",
                    "allowedExecutionProfiles",
                    "remoteRunnerEnabled",
                    "uiAutomationEnabled",
                    "maximumConcurrentJobs",
                    "allowedPluginPublishers",
                ],
            "cloud/runner/register" => ["requestId", "runnerId", "capabilities"],
            "cloud/runner/queue" => ["requestId", "requiredCapability", "task"],
            "cloud/runner/claim" => ["requestId", "runnerId", "leaseSeconds"],
            "cloud/runner/complete" => ["requestId", "claimHandle", "jobId", "result"],
            "cloud/automation/disable" => ["requestId", "automationId"],
            "cloud/automation/create" =>
                ["requestId", "name", "intervalSeconds", "requiredCapability", "task"],
            "extensions/list" => ["requestId", "workspaceGeneration", "sessionId", "useCache"],
            "extensions/action" =>
                ["requestId", "workspaceGeneration", "sessionId", "scope", "action", "confirmed", "payload"],
            "engine/prompt" =>
                [
                    "text",
                    "executionProfile",
                    "nativeRiskAcknowledged",
                    "workspaceGeneration",
                    "sessionMode",
                    "attachments",
                ],
            "engine/cancel" => ["sessionId"],
            "windows/automation/execute" =>
                ["requestId", "action", "processId", "automationId", "name", "value"],
            "permission/respond" => ["requestId", "outcome", "optionId"],
            _ => throw Invalid("The web message command type is not supported."),
        };
        ValidateAllowedProperties(
            root,
            includesDocumentToken
                ? ["schemaVersion", "type", "documentToken", .. properties]
                : ["schemaVersion", "type", .. properties]);
    }

    private static void ValidateDocumentToken(
        JsonElement root,
        string? expectedDocumentToken)
    {
        if (!IsDocumentToken(expectedDocumentToken) ||
            !root.TryGetProperty("documentToken", out var tokenProperty) ||
            tokenProperty.ValueKind != JsonValueKind.String)
        {
            throw Invalid("The web message document token is invalid.");
        }

        var suppliedDocumentToken = tokenProperty.GetString();
        if (!IsDocumentToken(suppliedDocumentToken) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedDocumentToken!),
                Encoding.ASCII.GetBytes(suppliedDocumentToken!)))
        {
            throw Invalid("The web message document token is invalid.");
        }
    }

    private static bool IsDocumentToken(string? documentToken) =>
        documentToken is { Length: DocumentTokenCharacters } &&
        documentToken.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static void ValidateAllowedProperties(
        JsonElement value,
        IReadOnlyCollection<string> allowedProperties)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Invalid("The web message contains an unsupported field.");
            }
        }
    }

    private static void ValidateNoDuplicateProperties(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                    {
                        if (!names.Add(property.Name))
                        {
                            throw Invalid("The web message contains a duplicate field.");
                        }
                        ValidateNoDuplicateProperties(property.Value);
                    }
                    break;
                }
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    ValidateNoDuplicateProperties(item);
                }
                break;
        }
    }

    private static string RequiredString(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } text &&
            !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        throw Invalid($"The web message is missing a valid '{propertyName}' field.");
    }

    private static string RequiredBoundedString(
        JsonElement root,
        string propertyName,
        int maximumCharacters)
    {
        var value = RequiredString(root, propertyName);
        if (value.Length <= maximumCharacters)
        {
            return value;
        }
        throw Invalid($"The web message has an invalid '{propertyName}' field.");
    }

    private static string RequiredMaintenanceRequestId(JsonElement root)
    {
        var requestId = RequiredString(root, "requestId");
        ValidateMaintenanceRequestId(requestId);
        return requestId;
    }

    private static string RequiredImageAttachmentToken(
        JsonElement root,
        string propertyName)
    {
        var token = RequiredString(root, propertyName);
        ValidateImageAttachmentToken(token);
        return token;
    }

    private static string RequiredImageAttachmentToken(JsonElement value)
    {
        if (value.ValueKind is not JsonValueKind.String || value.GetString() is not { } token)
        {
            throw Invalid("The image attachment token is invalid.");
        }
        ValidateImageAttachmentToken(token);
        return token;
    }

    private static string RequiredWorkspaceContextRequestId(JsonElement root)
    {
        var requestId = RequiredBoundedString(
            root,
            "requestId",
            MaximumExtensionRequestIdCharacters);
        if (Guid.TryParseExact(requestId, "D", out _))
        {
            return requestId;
        }

        throw Invalid("The workspace context request identifier is invalid.");
    }

    private static string RequiredWorkspaceRelativePath(
        JsonElement root,
        string propertyName)
    {
        var relativePath = RequiredBoundedString(root, propertyName, MaximumPathCharacters);
        ValidateWorkspaceRelativePath(relativePath);
        return relativePath;
    }

    private static string RequiredWorkspaceInstructionPath(
        JsonElement root,
        string propertyName)
    {
        var relativePath = RequiredWorkspaceRelativePath(root, propertyName);
        ValidateWorkspaceInstructionPath(relativePath);
        return relativePath;
    }

    private static string RequiredWorkspaceContextContent(JsonElement root)
    {
        var content = RequiredStringAllowEmpty(root, "content");
        ValidateWorkspaceContextContent(content);
        return content;
    }

    private static string RequiredMemorySessionId(JsonElement root)
    {
        var sessionId = RequiredBoundedString(
            root,
            "sessionId",
            MaximumSessionIdCharacters);
        ValidateMemorySessionId(sessionId);
        return sessionId;
    }

    private static MemoryFileId RequiredMemoryFileId(JsonElement root)
    {
        var value = RequiredBoundedString(
            root,
            "fileId",
            MaximumMemoryFileIdCharacters);
        try
        {
            return new MemoryFileId(value);
        }
        catch (ArgumentException exception)
        {
            throw Invalid("The memory file identifier is invalid.", exception);
        }
    }

    private static string RequiredMemoryContent(JsonElement root)
    {
        var content = RequiredStringAllowEmpty(root, "content");
        ValidateMemoryContent(content);
        return content;
    }

    private static string? OptionalMemoryConfirmationToken(JsonElement root)
    {
        var token = OptionalBoundedString(
            root,
            "confirmationToken",
            MemoryConfirmationTokenCharacters);
        if (token is null)
        {
            return null;
        }
        ValidateMemoryConfirmationToken(token);
        return token;
    }

    private static string RequiredWorkspaceContextQuery(JsonElement root)
    {
        var query = RequiredBoundedString(
            root,
            "query",
            MaximumWorkspaceContextQueryCharacters);
        if (!query.Any(char.IsControl))
        {
            return query;
        }

        throw Invalid("The workspace context query is invalid.");
    }

    private static void ValidateWorkspaceRelativePath(string relativePath)
    {
        if (relativePath.Contains('\\') ||
            relativePath.Contains(':') ||
            relativePath.StartsWith('/') ||
            relativePath.EndsWith('/') ||
            relativePath.Any(char.IsControl))
        {
            throw Invalid("The workspace context path is invalid.");
        }

        var segments = relativePath.Split('/');
        if (segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".."))
        {
            throw Invalid("The workspace context path is invalid.");
        }
    }

    private static void ValidateWorkspaceInstructionPath(string relativePath)
    {
        ValidateWorkspaceRelativePath(relativePath);
        if (!string.Equals(
                relativePath.Split('/')[^1],
                "AGENTS.md",
                StringComparison.Ordinal))
        {
            throw Invalid("Only AGENTS.md can be read or written through workspace context.");
        }
    }

    private static void ValidateWorkspaceContextContent(string content)
    {
        if (content.Length > MaximumWorkspaceContextContentCharacters ||
            content.Any(character =>
                character == '\0' ||
                (char.IsControl(character) && character is not ('\r' or '\n' or '\t'))))
        {
            throw Invalid("The workspace context content is invalid.");
        }

        try
        {
            if (WorkspaceContextUtf8.GetByteCount(content) >
                WorkspaceContextService.MaximumReadableFileBytes)
            {
                throw Invalid("The workspace context content is too large.");
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw Invalid("The workspace context content is not valid UTF-8 text.", exception);
        }
    }

    private static void ValidateMemoryContent(string content)
    {
        if (content.Any(character =>
                character == '\0' ||
                (char.IsControl(character) && character is not ('\r' or '\n' or '\t'))))
        {
            throw Invalid("The memory content is invalid.");
        }

        try
        {
            if (WorkspaceContextUtf8.GetByteCount(content) > MaximumMemoryContentBytes)
            {
                throw Invalid("The memory content is too large.");
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw Invalid("The memory content is not valid UTF-8 text.", exception);
        }
    }

    private static bool OptionalBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid($"The web message has an invalid '{propertyName}' field."),
        };
    }

    private static string RequiredStringAllowEmpty(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } text)
        {
            return text;
        }

        throw Invalid($"The web message is missing a valid '{propertyName}' field.");
    }

    private static bool RequiredBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            throw Invalid($"The web message is missing a valid '{propertyName}' field.");
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid($"The web message has an invalid '{propertyName}' field."),
        };
    }

    private static string? OptionalNonEmptyString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } text &&
            !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        throw Invalid($"The web message has an invalid '{propertyName}' field.");
    }

    private static string? OptionalBoundedString(
        JsonElement root,
        string propertyName,
        int maximumCharacters)
    {
        var value = OptionalNonEmptyString(root, propertyName);
        if (value is null || value.Length <= maximumCharacters)
        {
            return value;
        }
        throw Invalid($"The web message has an invalid '{propertyName}' field.");
    }

    private static int RequiredNonNegativeInt32(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var number) &&
            number >= 0)
        {
            return number;
        }

        throw Invalid($"The web message is missing a valid '{propertyName}' field.");
    }

    private static int RequiredPositiveInt32(
        JsonElement root,
        string propertyName,
        int maximumValue)
    {
        var value = RequiredNonNegativeInt32(root, propertyName);
        if (value is > 0 && value <= maximumValue)
        {
            return value;
        }
        throw Invalid($"The web message has an invalid '{propertyName}' field.");
    }

    private static long RequiredPositiveInt64(
        JsonElement root,
        string propertyName,
        long maximumValue)
    {
        if (root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var number) &&
            number is > 0 && number <= maximumValue)
        {
            return number;
        }
        throw Invalid($"The web message has an invalid '{propertyName}' field.");
    }

    private static string RequiredCloudIdentifier(JsonElement root, string propertyName)
    {
        var value = RequiredBoundedString(root, propertyName, 128);
        if (value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            return value;
        }
        throw Invalid($"The web message has an invalid '{propertyName}' field.");
    }

    private static int RequiredPageSize(JsonElement root, string propertyName)
    {
        var value = RequiredNonNegativeInt32(root, propertyName);
        if (value is >= 1 and <= 100)
        {
            return value;
        }

        throw Invalid($"The web message has an invalid '{propertyName}' field.");
    }

    private static int? OptionalNonNegativeInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var number) &&
            number >= 0)
        {
            return number;
        }
        throw Invalid($"The web message has an invalid '{propertyName}' field.");
    }

    private static long? OptionalPositiveInt64(
        JsonElement root,
        string propertyName,
        long maximumValue)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var number) &&
            number is > 0 && number <= maximumValue)
        {
            return number;
        }
        throw Invalid($"The web message has an invalid '{propertyName}' field.");
    }

    private static InvalidDataException Invalid(string message, Exception? inner = null) =>
        new(message, inner);

    private static object ProjectImageAttachmentsChanged(
        ImageAttachmentsChangedWebEvent value)
    {
        ValidateMaintenanceRequestId(value.RequestId);
        ValidateImageAttachmentReferences(value.Attachments);
        if (value.Cancelled && value.Error is not null)
        {
            throw Invalid("A cancelled image selection cannot contain an error.");
        }

        return new
        {
            schemaVersion = SchemaVersion,
            type = "attachment/changed",
            value.RequestId,
            attachments = value.Attachments.Select(reference => new
            {
                reference.Token,
                reference.Name,
                reference.MimeType,
                reference.Size,
            }),
            value.Cancelled,
            error = value.Error is null ? null : ImageAttachmentErrorName(value.Error.Value),
        };
    }

    private static object ProjectMemoryCapabilities(MemoryCapabilitiesWebEvent value)
    {
        ValidateMemorySessionId(value.SessionId);
        ArgumentNullException.ThrowIfNull(value.Capabilities);
        var capabilities = value.Capabilities;
        if (capabilities.SchemaVersion is not (0 or 1) ||
            (capabilities.SchemaVersion == 0 &&
             (capabilities.List || capabilities.Read || capabilities.Write ||
               capabilities.Delete || capabilities.MutationConfirmationRequired)) ||
            ((capabilities.Write || capabilities.Delete) &&
             !capabilities.MutationConfirmationRequired) ||
            (capabilities.MutationConfirmationRequired &&
             !capabilities.Write && !capabilities.Delete))
        {
            throw Invalid("The memory capabilities are invalid.");
        }

        return new
        {
            schemaVersion = SchemaVersion,
            type = "memory/capabilities",
            value.SessionId,
            memory = new
            {
                capabilities.SchemaVersion,
                capabilities.List,
                capabilities.Read,
                capabilities.Write,
                capabilities.Delete,
                capabilities.MutationConfirmationRequired,
            },
        };
    }

    private static object ProjectMemoryListed(MemoryListedWebEvent value)
    {
        ValidateMemoryEnvelope(
            value.RequestId,
            value.WorkspaceGeneration,
            value.SessionId);
        ArgumentNullException.ThrowIfNull(value.Listing);
        if (value.Listing.Files.Count > MaximumMemoryFiles)
        {
            throw Invalid("The memory file list is too large.");
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in value.Listing.Files)
        {
            ValidateMemoryFile(file);
            if (!identifiers.Add(file.Id.Value))
            {
                throw Invalid("The memory file list contains a duplicate identifier.");
            }
        }

        return new
        {
            schemaVersion = SchemaVersion,
            type = "memory/listed",
            value.RequestId,
            value.WorkspaceGeneration,
            value.SessionId,
            files = value.Listing.Files.Select(ProjectMemoryFile).ToArray(),
            value.Listing.Truncated,
        };
    }

    private static object ProjectMemoryDocument(MemoryDocumentWebEvent value)
    {
        ValidateMemoryEnvelope(
            value.RequestId,
            value.WorkspaceGeneration,
            value.SessionId);
        ArgumentNullException.ThrowIfNull(value.Document);
        ValidateMemoryFile(value.Document.File);
        ValidateMemoryContent(value.Document.Content);
        if (value.Document.File.ByteLength !=
            (ulong)WorkspaceContextUtf8.GetByteCount(value.Document.Content))
        {
            throw Invalid("The memory document byte length is inconsistent.");
        }

        return new
        {
            schemaVersion = SchemaVersion,
            type = "memory/document",
            value.RequestId,
            value.WorkspaceGeneration,
            value.SessionId,
            file = ProjectMemoryFile(value.Document.File),
            value.Document.Content,
        };
    }

    private static object ProjectMemoryMutation(MemoryMutationWebEvent value)
    {
        ValidateMemoryEnvelope(
            value.RequestId,
            value.WorkspaceGeneration,
            value.SessionId);
        if (value.Operation is not (MemoryOperation.Write or MemoryOperation.Delete))
        {
            throw Invalid("The memory mutation operation is invalid.");
        }
        ArgumentNullException.ThrowIfNull(value.FileId);
        ArgumentNullException.ThrowIfNull(value.Result);
        ValidateMemoryFileId(value.FileId);
        ValidateMemoryMessage(value.Result.Message);
        if (value.Result.Status is not (
                MemoryMutationStatus.ConfirmationRequired or
                MemoryMutationStatus.Success or
                MemoryMutationStatus.NotFound))
        {
            throw Invalid("The memory mutation status is invalid.");
        }
        if (value.Result.Status is not MemoryMutationStatus.Success && value.Result.File is not null)
        {
            throw Invalid("The memory mutation result is inconsistent.");
        }
        if (value.Result.Status is MemoryMutationStatus.ConfirmationRequired)
        {
            if (value.ConfirmationToken is null)
            {
                throw Invalid("The memory confirmation result is missing its host token.");
            }
            ValidateMemoryConfirmationToken(value.ConfirmationToken);
        }
        else if (value.ConfirmationToken is not null)
        {
            throw Invalid("The memory mutation result contains an unexpected host token.");
        }
        if (value.Result.File is not null)
        {
            ValidateMemoryFile(value.Result.File);
            if (value.Result.File.Id != value.FileId)
            {
                throw Invalid("The memory mutation result belongs to another file.");
            }
        }

        return new
        {
            schemaVersion = SchemaVersion,
            type = "memory/mutation",
            value.RequestId,
            value.WorkspaceGeneration,
            value.SessionId,
            operation = MemoryOperationName(value.Operation),
            fileId = value.FileId.Value,
            status = value.Result.Status switch
            {
                MemoryMutationStatus.ConfirmationRequired => "confirmation_required",
                MemoryMutationStatus.Success => "success",
                MemoryMutationStatus.NotFound => "not_found",
                _ => throw Invalid("The memory mutation status is invalid."),
            },
            value.Result.Message,
            file = value.Result.File is null ? null : ProjectMemoryFile(value.Result.File),
            value.ConfirmationToken,
        };
    }

    private static object ProjectMemoryError(MemoryErrorWebEvent value)
    {
        ValidateMemoryEnvelope(
            value.RequestId,
            value.WorkspaceGeneration,
            value.SessionId);
        ValidateMemoryMessage(value.Message);
        if ((value.Operation is MemoryOperation.List) != (value.FileId is null))
        {
            throw Invalid("The memory error target is inconsistent.");
        }
        if (value.FileId is not null)
        {
            ValidateMemoryFileId(value.FileId);
        }

        return new
        {
            schemaVersion = SchemaVersion,
            type = "memory/error",
            value.RequestId,
            value.WorkspaceGeneration,
            value.SessionId,
            operation = MemoryOperationName(value.Operation),
            fileId = value.FileId?.Value,
            value.Message,
        };
    }

    private static object ProjectMemoryFile(MemoryFileDescriptor value) => new
    {
        id = value.Id.Value,
        scope = value.Scope switch
        {
            MemoryFileScope.Global => "global",
            MemoryFileScope.Workspace => "workspace",
            MemoryFileScope.Session => "session",
            _ => throw Invalid("The memory file scope is invalid."),
        },
        value.Name,
        value.ByteLength,
        value.ModifiedAt,
        value.Writable,
    };

    private static void ValidateMemoryEnvelope(
        string requestId,
        int workspaceGeneration,
        string sessionId)
    {
        ValidateMaintenanceRequestId(requestId);
        if (workspaceGeneration < 0)
        {
            throw Invalid("The memory workspace generation is invalid.");
        }
        ValidateMemorySessionId(sessionId);
    }

    private static void ValidateMemorySessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            sessionId.Length > MaximumSessionIdCharacters ||
            sessionId.Any(character =>
                char.IsControl(character) ||
                char.GetUnicodeCategory(character) is
                    UnicodeCategory.Format or
                    UnicodeCategory.LineSeparator or
                    UnicodeCategory.ParagraphSeparator))
        {
            throw Invalid("The memory session identifier is invalid.");
        }
    }

    private static void ValidateMemoryFileId(MemoryFileId fileId)
    {
        ArgumentNullException.ThrowIfNull(fileId);
        if (fileId.Value.Length > MaximumMemoryFileIdCharacters)
        {
            throw Invalid("The memory file identifier is invalid.");
        }
        try
        {
            _ = new MemoryFileId(fileId.Value);
        }
        catch (ArgumentException exception)
        {
            throw Invalid("The memory file identifier is invalid.", exception);
        }
    }

    private static void ValidateMemoryFile(MemoryFileDescriptor file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ValidateMemoryFileId(file.Id);
        var expectedScope = file.Id.Value switch
        {
            "global" => MemoryFileScope.Global,
            "workspace" => MemoryFileScope.Workspace,
            _ => MemoryFileScope.Session,
        };
        if (file.Scope != expectedScope ||
            string.IsNullOrWhiteSpace(file.Name) ||
            file.Name.Length > MaximumMemoryFileNameCharacters ||
            file.Name.Any(character =>
                char.IsControl(character) ||
                char.GetUnicodeCategory(character) is
                    UnicodeCategory.Format or
                    UnicodeCategory.LineSeparator or
                    UnicodeCategory.ParagraphSeparator) ||
            file.ByteLength > MaximumMemoryContentBytes)
        {
            throw Invalid("The memory file metadata is invalid.");
        }
    }

    private static void ValidateMemoryMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message) ||
            message.Length > MaximumMemoryMessageCharacters ||
            message.Any(character =>
                character == '\0' ||
                (char.IsControl(character) && character is not ('\r' or '\n' or '\t')) ||
                char.GetUnicodeCategory(character) is
                    UnicodeCategory.Format or
                    UnicodeCategory.LineSeparator or
                    UnicodeCategory.ParagraphSeparator))
        {
            throw Invalid("The memory result message is invalid.");
        }
    }

    private static void ValidateMemoryConfirmationToken(string token)
    {
        if (token.Length != MemoryConfirmationTokenCharacters ||
            token.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw Invalid("The memory confirmation token is invalid.");
        }
    }

    private static void ValidateImageAttachmentReferences(
        IReadOnlyList<NativeImageAttachmentReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        if (references.Count > NativeImageAttachmentStore.MaximumAttachmentCount)
        {
            throw Invalid("The image attachment list is too large.");
        }

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var reference in references)
        {
            ArgumentNullException.ThrowIfNull(reference);
            ValidateImageAttachmentToken(reference.Token);
            if (!tokens.Add(reference.Token) ||
                !names.Add(reference.Name) ||
                string.IsNullOrWhiteSpace(reference.Name) ||
                reference.Name.Length > NativeImageAttachmentStore.MaximumAttachmentNameLength ||
                reference.Name.Any(char.IsControl) ||
                reference.Name.IndexOfAny(['/', '\\']) >= 0 ||
                ExpectedImageMimeType(reference.Name) != reference.MimeType ||
                reference.Size is <= 0 or > NativeImageAttachmentStore.MaximumAttachmentBytes)
            {
                throw Invalid("The image attachment metadata is invalid.");
            }

            totalBytes = checked(totalBytes + reference.Size);
            if (totalBytes > NativeImageAttachmentStore.MaximumTotalBytes)
            {
                throw Invalid("The image attachment total size is too large.");
            }
        }
    }

    private static void ValidateImageAttachmentToken(string token)
    {
        if (string.IsNullOrEmpty(token) ||
            token.Length != NativeImageAttachmentStore.AttachmentTokenLength ||
            token.Any(character =>
                !char.IsAsciiDigit(character) && character is not (>= 'A' and <= 'F')))
        {
            throw Invalid("The image attachment token is invalid.");
        }
    }

    private static string? ExpectedImageMimeType(string name) =>
        Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null,
        };

    private static string ImageAttachmentErrorName(ImageAttachmentError value) => value switch
    {
        ImageAttachmentError.UnsupportedType => "unsupported_type",
        ImageAttachmentError.TooMany => "too_many",
        ImageAttachmentError.TooLarge => "too_large",
        ImageAttachmentError.TotalTooLarge => "total_too_large",
        ImageAttachmentError.DuplicateName => "duplicate_name",
        ImageAttachmentError.ContentMismatch => "content_mismatch",
        ImageAttachmentError.ReadFailed => "read_failed",
        _ => throw Invalid("The image attachment error is invalid."),
    };

    private static string MemoryOperationName(MemoryOperation value) => value switch
    {
        MemoryOperation.List => "list",
        MemoryOperation.Read => "read",
        MemoryOperation.Write => "write",
        MemoryOperation.Delete => "delete",
        _ => throw Invalid("The memory operation is invalid."),
    };

    private static object ProjectWorkspaceContextFiles(
        string type,
        string requestId,
        int workspaceGeneration,
        string? query,
        IReadOnlyList<WorkspaceContextFile> files)
    {
        ValidateWorkspaceContextEnvelope(requestId, workspaceGeneration);
        if (query is not null &&
            (string.IsNullOrWhiteSpace(query) ||
             query.Length > MaximumWorkspaceContextQueryCharacters ||
             query.Any(char.IsControl)))
        {
            throw Invalid("The workspace context query is invalid.");
        }
        if (files.Count > MaximumWorkspaceContextFiles)
        {
            throw Invalid("The workspace context file list is too large.");
        }

        foreach (var file in files)
        {
            ValidateWorkspaceContextFile(file);
        }

        return new
        {
            schemaVersion = SchemaVersion,
            type,
            requestId,
            workspaceGeneration,
            query,
            files = files.Select(file => new
            {
                file.RelativePath,
                file.ByteLength,
                file.LastWriteTime,
            }),
        };
    }

    private static object ProjectWorkspaceContextRead(WorkspaceFileReadWebEvent value)
    {
        ValidateWorkspaceContextEnvelope(value.RequestId, value.WorkspaceGeneration);
        ValidateWorkspaceInstructionPath(value.RelativePath);
        ValidateWorkspaceContextContent(value.Content);

        return new
        {
            schemaVersion = SchemaVersion,
            type = "workspace/context/file/read",
            value.RequestId,
            value.WorkspaceGeneration,
            value.RelativePath,
            value.Content,
        };
    }

    private static object ProjectWorkspaceContextWrite(
        WorkspaceInstructionsWriteWebEvent value)
    {
        ValidateWorkspaceContextEnvelope(value.RequestId, value.WorkspaceGeneration);
        ValidateWorkspaceInstructionPath(value.RelativePath);

        return new
        {
            schemaVersion = SchemaVersion,
            type = "workspace/context/instructions/write",
            value.RequestId,
            value.WorkspaceGeneration,
            value.RelativePath,
        };
    }

    private static object ProjectWorkspaceContextError(WorkspaceContextErrorWebEvent value)
    {
        ValidateWorkspaceContextEnvelope(value.RequestId, value.WorkspaceGeneration);
        return new
        {
            schemaVersion = SchemaVersion,
            type = "workspace/context/error",
            value.RequestId,
            value.WorkspaceGeneration,
            operation = value.Operation switch
            {
                WorkspaceContextOperation.InstructionsList => "instructions-list",
                WorkspaceContextOperation.FileRead => "file-read",
                WorkspaceContextOperation.InstructionsWrite => "instructions-write",
                WorkspaceContextOperation.FileSearch => "file-search",
                _ => throw Invalid("The workspace context operation is invalid."),
            },
        };
    }

    private static void ValidateWorkspaceContextEnvelope(
        string requestId,
        int workspaceGeneration)
    {
        if (!Guid.TryParseExact(requestId, "D", out _) || workspaceGeneration < 0)
        {
            throw Invalid("The workspace context envelope is invalid.");
        }
    }

    private static void ValidateWorkspaceContextFile(WorkspaceContextFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ValidateWorkspaceRelativePath(file.RelativePath);
        if (file.ByteLength < 0)
        {
            throw Invalid("The workspace context file metadata is invalid.");
        }
    }
}

public abstract record WebCommand;

public sealed record UiReadyWebCommand : WebCommand;

public sealed record ModalStateWebCommand(bool IsOpen) : WebCommand;

public sealed record SaveUiPreferencesWebCommand(UiPreferences Preferences) : WebCommand;

public sealed record SelectWorkspaceWebCommand : WebCommand;

public sealed record OpenRecentWorkspaceWebCommand(string Path) : WebCommand;

public sealed record RemoveRecentWorkspaceWebCommand(string Path) : WebCommand;

public sealed record SaveProviderWebCommand(
    ProviderProfile Profile,
    bool UseExistingCredential,
    bool ReplaceCredential) : WebCommand
{
    public string BaseUrl => Profile.BaseUrl;

    public string Model => Profile.Model;

    public string Backend => Profile.Backend switch
    {
        ProviderBackend.ChatCompletions => "chat_completions",
        ProviderBackend.Responses => "responses",
        _ => throw new ArgumentOutOfRangeException(nameof(Profile)),
    };

    public bool AllowInsecureTransport => Profile.AllowInsecureTransport;
}

public sealed record SessionListWebCommand(
    string RequestId,
    string Query,
    string? Cursor,
    int Limit,
    bool Archived = false) : WebCommand;

public sealed record SessionOpenWebCommand(
    string SessionId,
    string WorkspacePath,
    ExecutionProfile ExecutionProfile) : WebCommand;

public sealed record SessionNewWebCommand(
    ExecutionProfile ExecutionProfile) : WebCommand;

public sealed record SessionRenameWebCommand(
    string RequestId,
    string SessionId,
    string Title,
    string WorkspacePath) : WebCommand;

public sealed record SessionArchiveWebCommand(
    string RequestId,
    string SessionId,
    bool Archived) : WebCommand;

public sealed record SessionForkWebCommand(
    string SessionId,
    string SourceWorkspacePath,
    string TargetWorkspacePath,
    int? TargetPromptIndex = null) : WebCommand;

public sealed record SessionCompactWebCommand(string SessionId, string? UserContext) : WebCommand;

public sealed record SessionRewindPointsWebCommand(string SessionId) : WebCommand;

public sealed record SessionRewindWebCommand(
    string SessionId,
    int TargetPromptIndex,
    SessionRewindMode Mode,
    bool Force) : WebCommand;

public sealed record RuntimeDashboardRefreshWebCommand(string SessionId) : WebCommand;

public sealed record RuntimeTaskKillWebCommand(string SessionId, string TaskId) : WebCommand;

public sealed record RuntimeSubagentGetWebCommand(
    string SessionId,
    string SubagentId) : WebCommand;

public sealed record RuntimeSubagentCancelWebCommand(
    string SessionId,
    string SubagentId) : WebCommand;

public sealed record RuntimeCommandsListWebCommand(int WorkspaceGeneration) : WebCommand;

public sealed record MemoryFlushWebCommand(string SessionId) : WebCommand;

public abstract record MemoryWebCommand(
    string RequestId,
    int WorkspaceGeneration,
    string SessionId) : WebCommand;

public sealed record MemoryListWebCommand(
    string RequestId,
    int WorkspaceGeneration,
    string SessionId) : MemoryWebCommand(RequestId, WorkspaceGeneration, SessionId);

public sealed record MemoryReadWebCommand(
    string RequestId,
    int WorkspaceGeneration,
    string SessionId,
    MemoryFileId FileId) : MemoryWebCommand(RequestId, WorkspaceGeneration, SessionId);

public sealed record MemoryWriteWebCommand(
    string RequestId,
    int WorkspaceGeneration,
    string SessionId,
    MemoryFileId FileId,
    string Content,
    bool Confirmed,
    string? ConfirmationToken = null) : MemoryWebCommand(RequestId, WorkspaceGeneration, SessionId);

public sealed record MemoryDeleteWebCommand(
    string RequestId,
    int WorkspaceGeneration,
    string SessionId,
    MemoryFileId FileId,
    bool Confirmed,
    string? ConfirmationToken = null) : MemoryWebCommand(RequestId, WorkspaceGeneration, SessionId);

public sealed record WorktreeCreateWebCommand(
    int WorkspaceGeneration,
    string SessionId,
    WorktreeCopyMode CopyMode,
    string? GitReference,
    bool CopyIgnoredInBackground,
    IReadOnlyList<string> IgnoredSkipPatterns,
    WorktreeCreationType? CreationType,
    string? Label,
    string? DestinationPath = null) : WebCommand;

public sealed record WorktreeListWebCommand(
    int WorkspaceGeneration,
    bool IncludeAll,
    IReadOnlyList<WorktreeKind> Types) : WebCommand;

public sealed record WorktreeShowWebCommand(
    int WorkspaceGeneration,
    string IdOrPath) : WebCommand;

public sealed record WorktreeApplyWebCommand(
    int WorkspaceGeneration,
    string SessionId,
    string WorktreePath,
    WorktreeApplyMode Mode) : WebCommand;

public sealed record WorktreeRemoveWebCommand(
    int WorkspaceGeneration,
    string IdOrPath,
    bool Force,
    bool DryRun) : WebCommand;

public sealed record WorktreeGcWebCommand(
    int WorkspaceGeneration,
    bool DryRun,
    long? MaximumAgeSeconds,
    bool Force) : WebCommand;

public abstract record MaintenanceWebCommand(string RequestId) : WebCommand;

public sealed record SessionExportWebCommand(string RequestId, string SessionId)
    : MaintenanceWebCommand(RequestId);

public sealed record SessionImportWebCommand(string RequestId)
    : MaintenanceWebCommand(RequestId);

public sealed record BackupCreateWebCommand(string RequestId)
    : MaintenanceWebCommand(RequestId);

public sealed record BackupRestoreWebCommand(string RequestId)
    : MaintenanceWebCommand(RequestId);

public sealed record UpdateCheckWebCommand(string RequestId)
    : MaintenanceWebCommand(RequestId);

public sealed record UpdateApplyWebCommand(string RequestId)
    : MaintenanceWebCommand(RequestId);

public sealed record SelectImageAttachmentsWebCommand(string RequestId) : WebCommand;

public sealed record DiscardImageAttachmentsWebCommand(IReadOnlyList<string> Tokens) : WebCommand;

public sealed record PromptWebCommand(
    string Text,
    ExecutionProfile ExecutionProfile,
    bool NativeRiskAcknowledged = false,
    int WorkspaceGeneration = 1,
    SessionMode SessionMode = SessionMode.Default,
    IReadOnlyList<PromptAttachment>? AttachmentItems = null,
    IReadOnlyList<NativeImageAttachmentReference>? AttachmentReferenceItems = null) : WebCommand
{
    public IReadOnlyList<PromptAttachment> Attachments { get; } = AttachmentItems ?? [];

    public IReadOnlyList<PromptAttachment> ResolvedAttachments { get; init; } = [];

    public IReadOnlyList<NativeImageAttachmentReference> AttachmentReferences { get; } =
        AttachmentReferenceItems ?? [];
}

public sealed record CancelWebCommand(string SessionId) : WebCommand;

public sealed record PermissionRespondWebCommand(
    string RequestId,
    PermissionDecision Decision) : WebCommand;

public abstract record WebEvent;

public sealed record ImageAttachmentsChangedWebEvent(
    string RequestId,
    IReadOnlyList<NativeImageAttachmentReference> Attachments,
    bool Cancelled = false,
    ImageAttachmentError? Error = null) : WebEvent;

public sealed record EngineStatusWebEvent(
    string Status,
    string? Message = null,
    string? SessionId = null,
    IReadOnlyList<ExecutionProfile>? ExecutionProfiles = null,
    string? WslStrictReason = null,
    bool? ImagePrompts = null,
    IReadOnlyCollection<SessionMode>? SessionModes = null,
    long? EngineEpoch = null)
    : WebEvent;

public sealed record WorkspaceSelectedWebEvent(string Path, int WorkspaceGeneration) : WebEvent;

public sealed record RecentWorkspacesChangedWebEvent(IReadOnlyList<string> Paths) : WebEvent;

public sealed record EngineCapabilitiesChangedWebEvent(
    string SessionId,
    bool ImagePrompts,
    IReadOnlyCollection<SessionMode> SessionModes) : WebEvent;

public sealed record CredentialStatusWebEvent(string Status, string? Message = null) : WebEvent;

public sealed record ProviderStatusWebEvent(
    string Status,
    string BaseUrl,
    string Model,
    string Backend,
    bool AllowInsecureTransport,
    bool HasCredential = false,
    string? Message = null) : WebEvent
{
    public ProviderStatusWebEvent(
        string status,
        ProviderProfile profile,
        bool HasCredential = false,
        string? Message = null)
        : this(
            status,
            profile.BaseUrl,
            profile.Model,
            profile.Backend switch
            {
                ProviderBackend.ChatCompletions => "chat_completions",
                ProviderBackend.Responses => "responses",
                _ => throw new ArgumentOutOfRangeException(nameof(profile)),
            },
            profile.AllowInsecureTransport,
            HasCredential,
            Message)
    {
    }
}

public sealed record SessionUpdateWebEvent(
    string SessionId,
    string UpdateKind,
    string? Text = null,
    object? Update = null,
    long EngineEpoch = 0) : WebEvent;

public sealed record PromptCompletedWebEvent(string SessionId, string StopReason) : WebEvent;

public sealed record SessionModeChangedWebEvent(
    string SessionId,
    SessionMode Mode,
    bool PlanAvailable) : WebEvent;

public sealed record SessionListChangedWebEvent(
    IReadOnlyList<SessionSummary> Sessions,
    string? NextCursor = null,
    string? RequestId = null) : WebEvent;

public sealed record SessionListErrorWebEvent(
    string RequestId,
    string Message) : WebEvent;

public sealed record SessionActiveChangedWebEvent(
    string SessionId,
    string WorkspacePath,
    long EngineEpoch = 0) : WebEvent;

public sealed record SessionRenamedWebEvent(
    string RequestId,
    string SessionId,
    string Title) : WebEvent;

public sealed record SessionArchiveChangedWebEvent(
    string RequestId,
    string SessionId,
    bool Archived) : WebEvent;

public sealed record SessionOperationErrorWebEvent(
    string RequestId,
    string Operation,
    string SessionId,
    string Message) : WebEvent;

public sealed record SessionForkedWebEvent(SessionForkResult Result) : WebEvent;

public sealed record SessionCompactedWebEvent(string SessionId) : WebEvent;

public sealed record SessionRewindPointsWebEvent(
    string SessionId,
    IReadOnlyList<SessionRewindPoint> Points) : WebEvent;

public sealed record SessionRewindPointsErrorWebEvent(
    string SessionId,
    string Message) : WebEvent;

public sealed record SessionRewoundWebEvent(
    string SessionId,
    SessionRewindResult Result) : WebEvent;

public sealed record RuntimeDashboardChangedWebEvent(
    string SessionId,
    IReadOnlyList<BackgroundTaskSnapshot> BackgroundTasks,
    IReadOnlyList<SubagentSnapshot> Subagents) : WebEvent;

public sealed record RuntimeTaskKilledWebEvent(
    string SessionId,
    string TaskId,
    BackgroundTaskKillOutcome Outcome) : WebEvent;

public sealed record RuntimeSubagentDetailWebEvent(
    string SessionId,
    string SubagentId,
    SubagentSnapshot? Snapshot) : WebEvent;

public sealed record RuntimeSubagentCancelledWebEvent(
    string SessionId,
    string SubagentId,
    SubagentCancelResult Result) : WebEvent;

public enum RuntimeDashboardOperation
{
    Refresh,
    TaskKill,
    SubagentGet,
    SubagentCancel,
}

public sealed record RuntimeDashboardErrorWebEvent(
    string SessionId,
    string Message,
    RuntimeDashboardOperation Operation,
    string? ItemId = null) : WebEvent;

public sealed record RuntimeCommandsChangedWebEvent(
    int WorkspaceGeneration,
    IReadOnlyList<RuntimeCommand> Commands) : WebEvent;

public sealed record RuntimeCommandsErrorWebEvent(
    int WorkspaceGeneration,
    string Message) : WebEvent;

public sealed record WorktreeCreatedWebEvent(
    int WorkspaceGeneration,
    WorktreeCreateResult Result) : WebEvent;

public sealed record WorktreeListChangedWebEvent(
    int WorkspaceGeneration,
    IReadOnlyList<WorktreeRecord> Worktrees) : WebEvent;

public sealed record WorktreeDetailWebEvent(
    int WorkspaceGeneration,
    WorktreeRecord? Worktree) : WebEvent;

public sealed record WorktreeAppliedWebEvent(
    int WorkspaceGeneration,
    WorktreeApplyResult Result) : WebEvent;

public sealed record WorktreeRemovedWebEvent(
    int WorkspaceGeneration,
    string IdOrPath,
    WorktreeRemoveResult Result) : WebEvent;

public sealed record WorktreeGcCompletedWebEvent(
    int WorkspaceGeneration,
    WorktreeGcResult Result) : WebEvent;

public enum WorktreeOperation
{
    Create,
    List,
    Show,
    Apply,
    Remove,
    Gc,
}

public sealed record WorktreeErrorWebEvent(
    int WorkspaceGeneration,
    string Message,
    WorktreeOperation Operation,
    string? ItemId = null) : WebEvent;

public sealed record SessionExportedWebEvent(
    string RequestId,
    string SessionId,
    string FileName) : WebEvent;

public sealed record SessionImportedWebEvent(
    string RequestId,
    string SessionId,
    string WorkspacePath) : WebEvent;

public sealed record BackupCompletedWebEvent(
    string RequestId,
    string Operation,
    int FileCount,
    long TotalBytes,
    bool RestartRequired) : WebEvent;

public sealed record UpdateStatusWebEvent(
    string RequestId,
    string Status,
    string? Version = null) : WebEvent;

public sealed record BackgroundUpdateAvailableWebEvent(string Version) : WebEvent;

public sealed record MaintenanceErrorWebEvent(
    string RequestId,
    string Operation) : WebEvent;

public sealed record MaintenanceCancelledWebEvent(
    string RequestId,
    string Operation) : WebEvent;

public sealed record MemoryFlushStatusWebEvent(
    string SessionId,
    string Status,
    string? Message = null) : WebEvent;

public enum MemoryOperation
{
    List,
    Read,
    Write,
    Delete,
}

public sealed record MemoryCapabilitiesWebEvent(
    string SessionId,
    MemoryManagementCapabilities Capabilities) : WebEvent;

public sealed record MemoryListedWebEvent(
    string RequestId,
    int WorkspaceGeneration,
    string SessionId,
    MemoryFileListing Listing) : WebEvent;

public sealed record MemoryDocumentWebEvent(
    string RequestId,
    int WorkspaceGeneration,
    string SessionId,
    MemoryFileDocument Document) : WebEvent;

public sealed record MemoryMutationWebEvent(
    string RequestId,
    int WorkspaceGeneration,
    string SessionId,
    MemoryOperation Operation,
    MemoryFileId FileId,
    MemoryMutationResult Result,
    string? ConfirmationToken = null) : WebEvent;

public sealed record MemoryErrorWebEvent(
    string RequestId,
    int WorkspaceGeneration,
    string SessionId,
    MemoryOperation Operation,
    string Message,
    MemoryFileId? FileId = null) : WebEvent;

public sealed record UiPreferencesChangedWebEvent(
    UiPreferences Preferences,
    bool RestartRequired = false) : WebEvent;

public sealed record PermissionRequestedWebEvent(
    string RequestId,
    string SessionId,
    string ToolCallId,
    string Title,
    IReadOnlyList<PermissionOption> Options,
    IReadOnlyList<string> Locations,
    string? ToolKind = null,
    JsonElement? RawInput = null) : WebEvent;
