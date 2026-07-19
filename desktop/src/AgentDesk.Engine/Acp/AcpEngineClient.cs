using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using AgentDesk.Core.Engine;
using AgentDesk.Engine.Transport;

namespace AgentDesk.Engine.Acp;

public sealed partial class AcpEngineClient : IEngineClient
{
    private const int SupportedProtocolVersion = 1;
    private const string ApiKeyAuthMethod = "xai.api_key";
    private const string ClientVersion = "0.1.0";
    private const int MaximumWorkingDirectoryLength = 32767;
    private const int MaximumSessionIdLength = 512;
    private const int MaximumRuntimeCommandCount = 4096;
    private const int MaximumRuntimeCommandNameLength = 256;
    private const int MaximumRuntimeCommandDescriptionLength = 4096;
    private const int MaximumRuntimeCommandHintLength = 2048;
    private const int MaximumRuntimeSkillPathLength = 32767;
    private const int MaximumRuntimeItemCount = 4096;
    private const int MaximumRuntimeIdLength = 512;
    private const int MaximumRuntimeDescriptionLength = 16 * 1024;
    private const int MaximumRuntimeCommandTextLength = 32767;
    private const int MaximumRuntimeOutputLength = 2 * 1024 * 1024;
    private const int MaximumWorktreeCount = 4096;
    private const int MaximumWorktreeIdLength = 512;
    private const int MaximumWorktreeRepositoryNameLength = 256;
    private const int MaximumWorktreeGitReferenceLength = 512;
    private const int MaximumWorktreeCommitLength = 128;
    private const int MaximumWorktreeLabelLength = 256;
    private const int MaximumWorktreeSkipPatternCount = 256;
    private const int MaximumWorktreeSkipPatternLength = 1024;
    private const int MaximumWorktreeChangeCount = 10_000;
    private const int MaximumWorktreeTextLength = 2 * 1024 * 1024;
    private const int MaximumWorktreeAggregateTextLength = 16 * 1024 * 1024;
    private static readonly TimeSpan MaximumSubagentWait = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaximumWorktreeGcAge = TimeSpan.FromDays(3650);

    private readonly NdjsonRpcConnection _connection;
    private readonly ConcurrentDictionary<string, PendingPermission> _pendingPermissions = new();
    private bool _apiKeyAuthenticationAvailable;
    private string? _desktopApiKey;

    public AcpEngineClient(Stream input, Stream output, string? desktopApiKey = null)
    {
        _connection = new NdjsonRpcConnection(input, output);
        _connection.NotificationReceived += OnNotificationReceived;
        _connection.RequestReceived += OnRequestReceived;
        _connection.Faulted += OnConnectionFaulted;
        _desktopApiKey = string.IsNullOrWhiteSpace(desktopApiKey) ? null : desktopApiKey;
    }

    public event EventHandler<EngineEvent>? EventReceived;

    public event EventHandler<PermissionRequest>? PermissionRequested;

    public event EventHandler<EngineFaultedEventArgs>? Faulted;

    public EngineCapabilities Capabilities { get; private set; } = EngineCapabilities.Uninitialized;

    public async Task<EngineCapabilities> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        await SeedDesktopCredentialAsync(cancellationToken).ConfigureAwait(false);

        var response = await _connection.SendRequestAsync(
            "initialize",
            new
            {
                protocolVersion = SupportedProtocolVersion,
                clientCapabilities = new
                {
                    fs = new
                    {
                        readTextFile = false,
                        writeTextFile = false,
                    },
                    terminal = false,
                    _meta = new Dictionary<string, bool>
                    {
                        ["x.ai/incrementalBashOutput"] = true,
                    },
                },
                clientInfo = new
                {
                    name = "agentdesk",
                    title = "AgentDesk",
                    version = ClientVersion,
                },
                _meta = new
                {
                    startupHints = new
                    {
                        nonInteractive = true,
                        skipGitStatus = true,
                        skipProjectLayout = true,
                    },
                    clientType = "generic",
                    clientIdentifier = "agentdesk",
                    clientVersion = ClientVersion,
                },
            },
            cancellationToken).ConfigureAwait(false);

        var protocolVersion = ReadRequiredInt32(response, "protocolVersion");
        if (protocolVersion != SupportedProtocolVersion)
        {
            throw new NotSupportedException(
                $"ACP protocol version {protocolVersion} is not supported. Expected version {SupportedProtocolVersion}.");
        }

        var agentCapabilities = TryGetObject(response, "agentCapabilities");
        var promptCapabilities = agentCapabilities is { } capabilities
            ? TryGetObject(capabilities, "promptCapabilities")
            : null;

        _apiKeyAuthenticationAvailable = HasAuthenticationMethod(response, ApiKeyAuthMethod);
        Capabilities = new EngineCapabilities(
            protocolVersion,
            LoadSession: ReadBoolean(agentCapabilities, "loadSession"),
            ImagePrompts: ReadBoolean(promptCapabilities, "image"),
            AudioPrompts: ReadBoolean(promptCapabilities, "audio"),
            EmbeddedContextPrompts: ReadBoolean(promptCapabilities, "embeddedContext"),
            AgentDeskExtensions: false,
            AgentDeskHealth: false);

        var extensionInitialize = await ProbeExtensionAsync(
            "agentdesk/v1/initialize",
            new
            {
                protocolVersion = SupportedProtocolVersion,
                client = new { name = "agentdesk", version = ClientVersion },
            },
            cancellationToken).ConfigureAwait(false);

        var sessionModes = ParseSessionModes(extensionInitialize);
        var memoryCapabilities = ParseMemoryCapabilities(extensionInitialize);
        var health = extensionInitialize is not null
            ? await ReadHealthAsync(cancellationToken).ConfigureAwait(false)
            : null;

        Capabilities = Capabilities with
        {
            AgentDeskExtensions = extensionInitialize is not null,
            AgentDeskHealth = health is not null,
            StrictSandboxActive = health?.StrictSandboxActive ?? false,
            SessionModes = sessionModes,
            Memory = memoryCapabilities,
        };
        return Capabilities;
    }

    private async Task SeedDesktopCredentialAsync(CancellationToken cancellationToken)
    {
        var apiKey = Interlocked.Exchange(ref _desktopApiKey, null);
        if (apiKey is null)
        {
            return;
        }

        var response = await _connection.SendRequestAsync(
            "agentdesk/v1/credential",
            new
            {
                protocolVersion = SupportedProtocolVersion,
                apiKey,
            },
            cancellationToken).ConfigureAwait(false);

        if (!ReadBoolean(response, "credentialAccepted") ||
            !string.Equals(
                ReadRequiredString(response, "authMethodId"),
                ApiKeyAuthMethod,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The AgentDesk sidecar did not accept the desktop credential bridge.");
        }
    }

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        if (!_apiKeyAuthenticationAvailable)
        {
            throw new NotSupportedException(
                $"The engine did not advertise the required '{ApiKeyAuthMethod}' authentication method.");
        }

        _ = await _connection.SendRequestAsync(
            "authenticate",
            new
            {
                methodId = ApiKeyAuthMethod,
                _meta = new { headless = true },
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionId> NewSessionAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var response = await _connection.SendRequestAsync(
            "session/new",
            new
            {
                cwd = workingDirectory,
                mcpServers = Array.Empty<object>(),
            },
            cancellationToken).ConfigureAwait(false);

        return new SessionId(ReadRequiredString(response, "sessionId"));
    }

    public async Task<EngineSessionDocument> ExportSessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ValidateSessionId(sessionId.Value, nameof(sessionId));
        var response = await _connection.SendRequestAsync(
            "agentdesk/v1/session/export",
            new { sessionId = sessionId.Value },
            cancellationToken).ConfigureAwait(false);
        if (ReadRequiredInt32(response, "schemaVersion") != 1 ||
            !response.TryGetProperty("session", out var session) ||
            session.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The engine returned an invalid AgentDesk session export.");
        }

        var utf8Json = JsonSerializer.SerializeToUtf8Bytes(session);
        if (utf8Json.Length > EngineSessionDocument.MaximumBytes)
        {
            throw new InvalidDataException("The engine session export exceeded the size limit.");
        }
        return EngineSessionDocument.FromUtf8Json(utf8Json);
    }

    public async Task<SessionId> ImportSessionAsync(
        EngineSessionDocument document,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var cwd = ValidateOptionalWorkingDirectory(workingDirectory) ??
            throw new ArgumentException("The working directory is invalid.", nameof(workingDirectory));
        using var parsed = JsonDocument.Parse(document.ExportUtf8Json());
        var response = await _connection.SendRequestAsync(
            "agentdesk/v1/session/import",
            new
            {
                cwd,
                session = parsed.RootElement.Clone(),
            },
            cancellationToken).ConfigureAwait(false);
        if (ReadRequiredInt32(response, "schemaVersion") != 1)
        {
            throw new InvalidDataException(
                "The engine returned an unsupported AgentDesk session import response.");
        }
        return new SessionId(ReadRequiredString(response, "sessionId"));
    }

    public async Task<WorktreeCreateResult> CreateWorktreeAsync(
        WorktreeCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SessionId);
        ValidateSessionId(request.SessionId.Value, nameof(request));
        var sourcePath = ValidateWorktreePath(request.SourcePath, nameof(request));
        var destinationPath = request.DestinationPath is null
            ? null
            : ValidateWorktreePath(request.DestinationPath, nameof(request));
        var copyMode = WorktreeCopyModeName(request.CopyMode);
        var gitReference = ValidateOptionalGitReference(request.GitReference, nameof(request));
        var skipPatterns = ValidateWorktreeSkipPatterns(
            request.IgnoredSkipPatterns,
            nameof(request));
        var creationType = request.CreationType is { } type
            ? WorktreeCreationTypeName(type)
            : null;
        var label = ValidateOptionalWorktreeLabel(request.Label, nameof(request));

        var response = await _connection.SendRequestAsync(
            "x.ai/git/worktree/create",
            new
            {
                sessionId = request.SessionId.Value,
                sourcePath,
                worktreePath = destinationPath,
                copyMode,
                gitRef = gitReference,
                copyIgnoredInBackground = request.CopyIgnoredInBackground,
                ignoredSkipPatterns = skipPatterns,
                worktreeType = creationType,
                label,
            },
            cancellationToken).ConfigureAwait(false);
        return ParseWorktreeCreateResult(
            ReadWorktreeExtensionResult(response),
            request.SessionId);
    }

    public async Task<IReadOnlyList<WorktreeRecord>> ListWorktreesAsync(
        WorktreeListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var repository = ValidateOptionalRepositoryFilter(request.Repository, nameof(request));
        var types = ValidateWorktreeKinds(request.Types, nameof(request));
        var response = await _connection.SendRequestAsync(
            "x.ai/git/worktree/list",
            new Dictionary<string, object?>
            {
                ["repo"] = repository,
                ["type"] = types,
                // The current Rust WorktreeListReq has no rename_all attribute.
                ["include_all"] = request.IncludeAll,
            },
            cancellationToken).ConfigureAwait(false);
        var result = ReadWorktreeExtensionResult(response);
        if (result.ValueKind != JsonValueKind.Array ||
            result.GetArrayLength() > MaximumWorktreeCount)
        {
            throw new InvalidDataException(
                "The worktree list response did not contain a bounded array.");
        }

        var records = new List<WorktreeRecord>(result.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in result.EnumerateArray())
        {
            var record = ParseWorktreeRecord(item);
            if (!ids.Add(record.Id))
            {
                throw new InvalidDataException(
                    "The worktree list response contained a duplicate worktree ID.");
            }
            records.Add(record);
        }
        return records;
    }

    public async Task<WorktreeRecord?> ShowWorktreeAsync(
        WorktreeShowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var idOrPath = ValidateWorktreeSelector(request.IdOrPath, nameof(request));
        var response = await _connection.SendRequestAsync(
            "x.ai/git/worktree/show",
            new { idOrPath },
            cancellationToken).ConfigureAwait(false);
        var result = ReadWorktreeExtensionResult(response, allowNull: true);
        return result.ValueKind == JsonValueKind.Null ? null : ParseWorktreeRecord(result);
    }

    public async Task<WorktreeApplyResult> ApplyWorktreeAsync(
        WorktreeApplyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SessionId);
        ValidateSessionId(request.SessionId.Value, nameof(request));
        var worktreePath = ValidateWorktreePath(request.WorktreePath, nameof(request));
        var mode = WorktreeApplyModeName(request.Mode);
        var response = await _connection.SendRequestAsync(
            "x.ai/git/worktree/apply",
            new
            {
                sessionId = request.SessionId.Value,
                worktreePath,
                mode,
            },
            cancellationToken).ConfigureAwait(false);
        return ParseWorktreeApplyResult(ReadWorktreeExtensionResult(response));
    }

    public async Task<WorktreeRemoveResult> RemoveWorktreeAsync(
        WorktreeRemoveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var idOrPath = ValidateWorktreeSelector(request.IdOrPath, nameof(request));
        var response = await _connection.SendRequestAsync(
            "x.ai/git/worktree/remove",
            new
            {
                idOrPath,
                force = request.Force,
                dryRun = request.DryRun,
            },
            cancellationToken).ConfigureAwait(false);
        return ParseWorktreeRemoveResult(ReadWorktreeExtensionResult(response));
    }

    public async Task<WorktreeGcResult> GcWorktreesAsync(
        WorktreeGcRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var maximumAge = FormatWorktreeGcAge(request.MaximumAge, nameof(request));
        var response = await _connection.SendRequestAsync(
            "x.ai/git/worktree/gc",
            new
            {
                dryRun = request.DryRun,
                maxAge = maximumAge,
                force = request.Force,
            },
            cancellationToken).ConfigureAwait(false);
        return ParseWorktreeGcResult(ReadWorktreeExtensionResult(response));
    }

    public async Task LoadSessionAsync(
        SessionId sessionId,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        _ = await _connection.SendRequestAsync(
            "session/load",
            new
            {
                mcpServers = Array.Empty<object>(),
                cwd = workingDirectory,
                sessionId = sessionId.Value,
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RuntimeCommand>> ListRuntimeCommandsAsync(
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var cwd = ValidateOptionalWorkingDirectory(workingDirectory);
        var response = await _connection.SendRequestAsync(
            "x.ai/commands/list",
            new { cwd },
            cancellationToken).ConfigureAwait(false);
        var commands = ReadRequiredArray(response, "commands");
        if (commands.GetArrayLength() > MaximumRuntimeCommandCount)
        {
            throw new InvalidDataException(
                "The runtime command catalog exceeded the supported command count.");
        }

        var parsed = new List<RuntimeCommand>(commands.GetArrayLength());
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in commands.EnumerateArray())
        {
            var item = ParseRuntimeCommand(command);
            if (!names.Add(item.Name))
            {
                throw new InvalidDataException(
                    "The runtime command catalog contained a duplicate command name.");
            }
            parsed.Add(item);
        }
        return parsed;
    }

    public async Task<IReadOnlyList<BackgroundTaskSnapshot>> ListBackgroundTasksAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ValidateSessionId(sessionId.Value, nameof(sessionId));
        var response = await _connection.SendRequestAsync(
            "x.ai/task/list",
            new { sessionId = sessionId.Value },
            cancellationToken).ConfigureAwait(false);
        var result = ReadRequiredExtensionResult(response);
        var tasks = ReadRequiredArray(result, "tasks");
        EnsureRuntimeItemCount(tasks, "task");

        var parsed = new List<BackgroundTaskSnapshot>(tasks.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in tasks.EnumerateArray())
        {
            var snapshot = ParseBackgroundTask(item);
            if (!ids.Add(snapshot.TaskId))
            {
                throw new InvalidDataException(
                    "The background task response contained a duplicate task ID.");
            }
            parsed.Add(snapshot);
        }
        return parsed;
    }

    public async Task<BackgroundTaskKillOutcome> KillBackgroundTaskAsync(
        SessionId sessionId,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ValidateSessionId(sessionId.Value, nameof(sessionId));
        ValidateRuntimeId(taskId, nameof(taskId));
        var response = await _connection.SendRequestAsync(
            "x.ai/task/kill",
            new { sessionId = sessionId.Value, taskId },
            cancellationToken).ConfigureAwait(false);
        var result = ReadRequiredExtensionResult(response);
        if (!string.Equals(ReadRequiredString(result, "taskId"), taskId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The engine returned a different background task ID.");
        }
        return ReadRequiredString(result, "outcome") switch
        {
            "killed" => BackgroundTaskKillOutcome.Killed,
            "already_exited" => BackgroundTaskKillOutcome.AlreadyExited,
            "not_found" => BackgroundTaskKillOutcome.NotFound,
            _ => throw new InvalidDataException(
                "The engine returned an unsupported background task kill outcome."),
        };
    }

    public async Task<IReadOnlyList<SubagentSnapshot>> ListRunningSubagentsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ValidateSessionId(sessionId.Value, nameof(sessionId));
        var response = await _connection.SendRequestAsync(
            "x.ai/subagent/list_running",
            new { sessionId = sessionId.Value },
            cancellationToken).ConfigureAwait(false);
        var result = ReadRequiredExtensionResult(response);
        EnsureExtensionSession(result, sessionId);
        var subagents = ReadRequiredArray(result, "subagents");
        EnsureRuntimeItemCount(subagents, "subagent");

        var parsed = new List<SubagentSnapshot>(subagents.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in subagents.EnumerateArray())
        {
            var snapshot = ParseRunningSubagent(item);
            if (!string.Equals(
                    snapshot.ParentSessionId,
                    sessionId.Value,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The running subagent response contained an item from another session.");
            }
            if (!ids.Add(snapshot.SubagentId))
            {
                throw new InvalidDataException(
                    "The running subagent response contained a duplicate subagent ID.");
            }
            parsed.Add(snapshot);
        }
        return parsed;
    }

    public async Task<SubagentSnapshot?> GetSubagentAsync(
        SessionId sessionId,
        string subagentId,
        bool block = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ValidateSessionId(sessionId.Value, nameof(sessionId));
        ValidateRuntimeId(subagentId, nameof(subagentId));
        if (timeout is { } wait && (wait <= TimeSpan.Zero || wait > MaximumSubagentWait))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "The subagent wait timeout must be greater than zero and no more than ten minutes.");
        }
        if (!block && timeout is not null)
        {
            throw new ArgumentException(
                "A subagent timeout can only be used with a blocking request.",
                nameof(timeout));
        }

        var response = await _connection.SendRequestAsync(
            "x.ai/subagent/get",
            new
            {
                sessionId = sessionId.Value,
                subagentId,
                block,
                timeoutMs = timeout is null ? (long?)null : checked((long)timeout.Value.TotalMilliseconds),
            },
            cancellationToken).ConfigureAwait(false);
        var result = ReadRequiredExtensionResult(response);
        EnsureExtensionSession(result, sessionId);
        if (!result.TryGetProperty("snapshot", out var snapshot) ||
            snapshot.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (snapshot.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The subagent response did not contain a valid snapshot object.");
        }
        var parsed = ParseSubagentSnapshot(snapshot);
        if (!string.Equals(parsed.SubagentId, subagentId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The engine returned a different subagent ID.");
        }
        if (!string.Equals(parsed.ParentSessionId, sessionId.Value, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The engine returned a subagent from another session.");
        }
        return parsed;
    }

    public async Task<SubagentCancelResult> CancelSubagentAsync(
        SessionId sessionId,
        string subagentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ValidateSessionId(sessionId.Value, nameof(sessionId));
        ValidateRuntimeId(subagentId, nameof(subagentId));
        var response = await _connection.SendRequestAsync(
            "x.ai/subagent/cancel",
            new { sessionId = sessionId.Value, subagentId },
            cancellationToken).ConfigureAwait(false);
        var result = ReadRequiredExtensionResult(response);
        EnsureExtensionSession(result, sessionId);
        if (!string.Equals(
                ReadRequiredString(result, "subagentId"),
                subagentId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The engine returned a different subagent ID.");
        }
        var outcome = TryGetObject(result, "outcome") ?? throw new InvalidDataException(
            "The subagent cancel response did not contain an outcome object.");
        return ReadRequiredString(outcome, "kind") switch
        {
            "cancelled" => new SubagentCancelResult(SubagentCancelOutcome.Cancelled),
            "not_found" => new SubagentCancelResult(SubagentCancelOutcome.NotFound),
            "already_finished" => ReadAlreadyFinishedSubagentResult(outcome),
            _ => throw new InvalidDataException(
                "The engine returned an unsupported subagent cancel outcome."),
        };
    }

    public async Task<SessionPage> ListSessionsAsync(
        string? workingDirectory,
        string? query,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The page size must be between 1 and 100.");
        }

        var response = await _connection.SendRequestAsync(
            "x.ai/session/list",
            new
            {
                cwd = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                query = string.IsNullOrWhiteSpace(query) ? null : query,
                cursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor,
                limit,
            },
            cancellationToken).ConfigureAwait(false);
        var result = TryGetObject(response, "result") ?? throw new InvalidDataException(
            "The session catalog response did not contain a result object.");
        if (!result.TryGetProperty("sessions", out var sessions) ||
            sessions.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "The session catalog response did not contain a sessions array.");
        }

        var parsed = new List<SessionSummary>();
        foreach (var item in sessions.EnumerateArray())
        {
            parsed.Add(ParseSessionSummary(item));
        }

        return new SessionPage(parsed, ReadOptionalString(result, "nextCursor"));
    }

    public async Task RenameSessionAsync(
        SessionId sessionId,
        string title,
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var response = await _connection.SendRequestAsync(
            "x.ai/session/rename",
            new
            {
                sessionId = sessionId.Value,
                title = title.Trim(),
                cwd = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                kind = "build",
            },
            cancellationToken).ConfigureAwait(false);
        if (!ReadRequiredBoolean(response, "success"))
        {
            throw new InvalidDataException("The engine did not confirm the session rename.");
        }
    }

    public async Task<SessionForkResult> ForkSessionAsync(
        SessionId sourceSessionId,
        string sourceWorkingDirectory,
        string targetWorkingDirectory,
        int? targetPromptIndex = null,
        string? modelId = null,
        string? sessionKind = null,
        string? sourceWorkspacePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceWorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetWorkingDirectory);
        if (targetPromptIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPromptIndex));
        }

        var response = await _connection.SendRequestAsync(
            "x.ai/session/fork",
            new
            {
                source_session_id = sourceSessionId.Value,
                source_cwd = sourceWorkingDirectory,
                new_cwd = targetWorkingDirectory,
                new_session_id = (string?)null,
                new_model_id = string.IsNullOrWhiteSpace(modelId) ? null : modelId,
                target_prompt_index = targetPromptIndex,
                session_kind = string.IsNullOrWhiteSpace(sessionKind) ? null : sessionKind,
                source_workspace_dir = string.IsNullOrWhiteSpace(sourceWorkspacePath)
                    ? null
                    : sourceWorkspacePath,
            },
            cancellationToken).ConfigureAwait(false);
        var chatMessagesCopied = ReadRequiredNonNegativeInt32(response, "chatMessagesCopied");
        var updatesCopied = ReadRequiredNonNegativeInt32(response, "updatesCopied");
        return new SessionForkResult(
            new SessionId(ReadRequiredString(response, "newSessionId")),
            ReadRequiredString(response, "newCwd"),
            ReadRequiredString(response, "parentSessionId"),
            chatMessagesCopied,
            updatesCopied,
            ReadRequiredBoolean(response, "planStateCopied"),
            ReadOptionalString(response, "newModelId"));
    }

    public async Task CompactSessionAsync(
        SessionId sessionId,
        string? userContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        _ = await _connection.SendRequestAsync(
            "x.ai/compact_conversation",
            new
            {
                sessionId = sessionId.Value,
                userContext = string.IsNullOrWhiteSpace(userContext) ? null : userContext,
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task FlushMemoryAsync(
        SessionId activeSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeSessionId);
        ValidateSessionId(activeSessionId.Value, nameof(activeSessionId));
        _ = await _connection.SendRequestAsync(
            "x.ai/memory/flush",
            new { session_id = activeSessionId.Value },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionRewindPoint>> GetRewindPointsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        var response = await _connection.SendRequestAsync(
            "x.ai/rewind/points",
            new { sessionId = sessionId.Value },
            cancellationToken).ConfigureAwait(false);
        var points = ReadRequiredArray(response, "rewind_points");
        var parsed = new List<SessionRewindPoint>();
        foreach (var point in points.EnumerateArray())
        {
            parsed.Add(
                new SessionRewindPoint(
                    ReadRequiredNonNegativeInt32(point, "prompt_index"),
                    ReadRequiredTimestamp(point, "created_at"),
                    ReadRequiredNonNegativeInt32(point, "num_file_snapshots"),
                    ReadRequiredBoolean(point, "has_file_changes"),
                    ReadOptionalString(point, "prompt_preview")));
        }
        return parsed;
    }

    public async Task<SessionRewindResult> RewindSessionAsync(
        SessionId sessionId,
        int targetPromptIndex,
        SessionRewindMode mode,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(targetPromptIndex);
        var modeName = RewindModeName(mode);
        var response = await _connection.SendRequestAsync(
            "x.ai/rewind/execute",
            new
            {
                sessionId = sessionId.Value,
                targetPromptIndex,
                force,
                mode = modeName,
            },
            cancellationToken).ConfigureAwait(false);
        var conflictsElement = ReadRequiredArray(response, "conflicts");
        var conflicts = new List<SessionRewindConflict>();
        foreach (var conflict in conflictsElement.EnumerateArray())
        {
            conflicts.Add(
                new SessionRewindConflict(
                    ReadRequiredString(conflict, "path"),
                    ReadRequiredString(conflict, "conflict_type")));
        }

        return new SessionRewindResult(
            ReadRequiredBoolean(response, "success"),
            ReadRequiredNonNegativeInt32(response, "target_prompt_index"),
            ParseRewindMode(ReadRequiredString(response, "mode")),
            ReadRequiredStringArray(response, "reverted_files"),
            ReadRequiredStringArray(response, "clean_files"),
            conflicts,
            ReadOptionalString(response, "prompt_text"),
            ReadOptionalString(response, "error"));
    }

    private static SessionSummary ParseSessionSummary(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The session catalog contains a non-object row.");
        }

        var sessionId = ReadRequiredString(item, "sessionId");
        var workspacePath = ReadRequiredString(item, "cwd");
        var title = ReadOptionalString(item, "title") ??
            ReadOptionalString(item, "summary") ??
            ReadOptionalString(item, "firstPrompt") ??
            sessionId;
        var createdAt = ReadRequiredTimestamp(item, "createdAt");
        var updatedAt = ReadRequiredTimestamp(item, "updatedAt");
        var messageCount = ReadRequiredInt32(item, "numMessages");
        if (messageCount < 0)
        {
            throw new InvalidDataException("The session catalog contains a negative message count.");
        }

        return new SessionSummary(
            new SessionId(sessionId),
            title,
            workspacePath,
            createdAt,
            updatedAt,
            messageCount,
            ModelId: ReadOptionalString(item, "modelId"),
            ParentSessionId: ReadOptionalString(item, "parentSessionId"),
            Branch: ReadOptionalString(item, "branch"),
            WorktreeLabel: ReadOptionalString(item, "worktreeLabel"),
            SourceWorkspacePath: ReadOptionalString(item, "sourceWorkspaceDir"));
    }

    private static RuntimeCommand ParseRuntimeCommand(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The runtime command catalog contained a non-object command.");
        }

        var name = ReadRequiredBoundedString(
            item,
            "name",
            MaximumRuntimeCommandNameLength,
            allowEmpty: false);
        if (name.Any(char.IsWhiteSpace) || name.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "The runtime command catalog contained an invalid command name.");
        }

        var description = ReadRequiredBoundedString(
            item,
            "description",
            MaximumRuntimeCommandDescriptionLength,
            allowEmpty: true);
        var input = ParseRuntimeCommandInput(item);
        var skill = ParseRuntimeSkillMetadata(item);
        return new RuntimeCommand(name, description, input, skill);
    }

    private static RuntimeCommandInput? ParseRuntimeCommandInput(JsonElement item)
    {
        if (!item.TryGetProperty("input", out var input))
        {
            throw new InvalidDataException(
                "The runtime command catalog did not contain an input field.");
        }
        if (input.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The runtime command catalog contained an invalid command input.");
        }

        return new RuntimeCommandInput(
            ReadRequiredBoundedString(
                input,
                "hint",
                MaximumRuntimeCommandHintLength,
                allowEmpty: true));
    }

    private static RuntimeSkillMetadata? ParseRuntimeSkillMetadata(JsonElement item)
    {
        if (!item.TryGetProperty("_meta", out var metadata) ||
            metadata.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (metadata.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The runtime command catalog contained invalid command metadata.");
        }

        var hasScope = metadata.TryGetProperty("scope", out var scopeElement);
        var hasPath = metadata.TryGetProperty("path", out var pathElement);
        if (!hasScope && !hasPath)
        {
            return null;
        }
        if (!hasScope || !hasPath ||
            scopeElement.ValueKind != JsonValueKind.String ||
            pathElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                "The runtime command catalog contained incomplete skill metadata.");
        }

        var scope = scopeElement.GetString() switch
        {
            "local" => RuntimeSkillScope.Local,
            "repo" => RuntimeSkillScope.Repo,
            "user" => RuntimeSkillScope.User,
            "plugin" => RuntimeSkillScope.Plugin,
            _ => throw new InvalidDataException(
                "The runtime command catalog contained an unsupported skill scope."),
        };
        var path = pathElement.GetString();
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > MaximumRuntimeSkillPathLength ||
            path.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "The runtime command catalog contained an invalid skill path.");
        }

        return new RuntimeSkillMetadata(scope, path);
    }

    private static BackgroundTaskSnapshot ParseBackgroundTask(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The background task response contained a non-object task.");
        }

        return new BackgroundTaskSnapshot(
            ReadRequiredRuntimeId(item, "task_id"),
            ReadRequiredBoundedText(
                item,
                "command",
                MaximumRuntimeCommandTextLength,
                allowEmpty: false),
            ReadOptionalBoundedText(
                item,
                "display_command",
                MaximumRuntimeCommandTextLength),
            ReadRequiredBoundedString(
                item,
                "cwd",
                MaximumWorkingDirectoryLength,
                allowEmpty: false),
            ReadRequiredSystemTime(item, "start_time"),
            ReadOptionalSystemTime(item, "end_time"),
            ReadRequiredBoundedText(
                item,
                "output",
                MaximumRuntimeOutputLength,
                allowEmpty: true),
            ReadRequiredBoundedString(
                item,
                "output_file",
                MaximumWorkingDirectoryLength,
                allowEmpty: false),
            ReadRequiredBoolean(item, "truncated"),
            ReadOptionalInt32(item, "exit_code"),
            ReadOptionalBoundedText(item, "signal", 256),
            ReadRequiredBoolean(item, "completed"),
            ReadRequiredString(item, "kind") switch
            {
                "bash" => BackgroundTaskKind.Bash,
                "monitor" => BackgroundTaskKind.Monitor,
                _ => throw new InvalidDataException(
                    "The engine returned an unsupported background task kind."),
            },
            ReadRequiredBoolean(item, "explicitly_killed"),
            ReadOptionalBoundedText(item, "owner_session_id", MaximumSessionIdLength));
    }

    private static SubagentSnapshot ParseRunningSubagent(JsonElement item) =>
        new(
            ReadRequiredRuntimeId(item, "subagentId"),
            ReadRequiredBoundedString(
                item,
                "parentSessionId",
                MaximumSessionIdLength,
                allowEmpty: false),
            ReadRequiredBoundedString(
                item,
                "childSessionId",
                MaximumSessionIdLength,
                allowEmpty: false),
            ReadRequiredBoundedString(
                item,
                "subagentType",
                256,
                allowEmpty: false),
            ReadRequiredBoundedText(
                item,
                "description",
                MaximumRuntimeDescriptionLength,
                allowEmpty: false),
            ReadRequiredEpochMilliseconds(item, "startedAtEpochMs"),
            ReadRequiredDuration(item, "durationMs"),
            SubagentStatus.Running,
            TurnCount: ReadRequiredNonNegativeInt32(item, "turnCount"),
            ToolCallCount: ReadRequiredNonNegativeInt32(item, "toolCallCount"),
            TokensUsed: ReadRequiredUInt64(item, "tokensUsed"),
            ContextWindowTokens: ReadRequiredUInt64(item, "contextWindowTokens"),
            ContextUsagePercent: ReadRequiredPercentage(item, "contextUsagePct"),
            ToolsUsed: ReadRequiredStringArray(item, "toolsUsed"),
            ErrorCount: ReadRequiredNonNegativeInt32(item, "errorCount"));

    private static SubagentSnapshot ParseSubagentSnapshot(JsonElement item)
    {
        var status = ParseSubagentStatus(ReadRequiredString(item, "status"));
        var common = new
        {
            SubagentId = ReadRequiredRuntimeId(item, "subagentId"),
            ParentSessionId = ReadRequiredBoundedString(
                item,
                "parentSessionId",
                MaximumSessionIdLength,
                allowEmpty: true),
            ChildSessionId = ReadRequiredBoundedString(
                item,
                "childSessionId",
                MaximumSessionIdLength,
                allowEmpty: true),
            SubagentType = ReadRequiredBoundedString(
                item,
                "subagentType",
                256,
                allowEmpty: false),
            Description = ReadRequiredBoundedText(
                item,
                "description",
                MaximumRuntimeDescriptionLength,
                allowEmpty: false),
            StartedAt = ReadRequiredEpochMilliseconds(item, "startedAtEpochMs"),
            Duration = ReadRequiredDuration(item, "durationMs"),
        };

        return new SubagentSnapshot(
            common.SubagentId,
            common.ParentSessionId,
            common.ChildSessionId,
            common.SubagentType,
            common.Description,
            common.StartedAt,
            common.Duration,
            status,
            TurnCount: status switch
            {
                SubagentStatus.Running => ReadRequiredNonNegativeInt32(item, "turnCount"),
                SubagentStatus.Completed => ReadRequiredNonNegativeInt32(item, "turns"),
                _ => null,
            },
            ToolCallCount: status switch
            {
                SubagentStatus.Running => ReadRequiredNonNegativeInt32(item, "toolCallCount"),
                SubagentStatus.Completed => ReadRequiredNonNegativeInt32(item, "toolCalls"),
                _ => null,
            },
            TokensUsed: status == SubagentStatus.Running
                ? ReadRequiredUInt64(item, "tokensUsed")
                : null,
            ContextWindowTokens: status == SubagentStatus.Running
                ? ReadRequiredUInt64(item, "contextWindowTokens")
                : null,
            ContextUsagePercent: status == SubagentStatus.Running
                ? ReadRequiredPercentage(item, "contextUsagePct")
                : null,
            ToolsUsed: status == SubagentStatus.Running
                ? ReadRequiredStringArray(item, "toolsUsed")
                : null,
            ErrorCount: status == SubagentStatus.Running
                ? ReadRequiredNonNegativeInt32(item, "errorCount")
                : null,
            Output: status == SubagentStatus.Completed
                ? ReadRequiredBoundedText(
                    item,
                    "output",
                    MaximumRuntimeOutputLength,
                    allowEmpty: true)
                : null,
            WorktreePath: ReadOptionalBoundedText(
                item,
                "worktreePath",
                MaximumWorkingDirectoryLength),
            FailureError: status == SubagentStatus.Failed
                ? ReadRequiredBoundedText(
                    item,
                    "failureError",
                    MaximumRuntimeDescriptionLength,
                    allowEmpty: false)
                : null,
            CancelReason: ReadOptionalBoundedText(
                item,
                "cancelReason",
                MaximumRuntimeDescriptionLength),
            ForkContextSource: ReadOptionalBoundedText(
                item,
                "forkContextSource",
                MaximumRuntimeDescriptionLength),
            ForkParentPromptId: ReadOptionalBoundedText(
                item,
                "forkParentPromptId",
                MaximumRuntimeIdLength),
            ResumedFrom: ReadOptionalBoundedText(
                item,
                "resumedFrom",
                MaximumRuntimeIdLength));
    }

    private static SubagentStatus ParseSubagentStatus(string status) => status switch
    {
        "initializing" => SubagentStatus.Initializing,
        "running" => SubagentStatus.Running,
        "completed" => SubagentStatus.Completed,
        "failed" => SubagentStatus.Failed,
        "cancelled" => SubagentStatus.Cancelled,
        _ => throw new InvalidDataException("The engine returned an unsupported subagent status."),
    };

    private static SubagentCancelResult ReadAlreadyFinishedSubagentResult(JsonElement outcome)
    {
        var status = ParseSubagentStatus(ReadRequiredString(outcome, "status"));
        if (status is SubagentStatus.Initializing or SubagentStatus.Running)
        {
            throw new InvalidDataException(
                "The engine returned a non-terminal status for an already-finished subagent.");
        }
        return new SubagentCancelResult(SubagentCancelOutcome.AlreadyFinished, status);
    }

    public async Task SetSessionModeAsync(
        SessionId sessionId,
        SessionMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        var modeId = mode switch
        {
            SessionMode.Default => "default",
            SessionMode.Plan => "plan",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        _ = await _connection.SendRequestAsync(
            "session/set_mode",
            new
            {
                sessionId = sessionId.Value,
                modeId,
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PromptResult> PromptAsync(
        SessionId sessionId,
        string text,
        CancellationToken cancellationToken = default) =>
        await PromptWithAttachmentsAsync(
            sessionId,
            text,
            Array.Empty<PromptAttachment>(),
            cancellationToken).ConfigureAwait(false);

    public async Task<PromptResult> PromptWithAttachmentsAsync(
        SessionId sessionId,
        string text,
        IReadOnlyList<PromptAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(attachments);
        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
        {
            throw new ArgumentException(
                "A prompt must contain text or at least one image.",
                nameof(text));
        }

        var prompt = BuildPromptBlocks(text, attachments);

        var response = await _connection.SendRequestAsync(
            "session/prompt",
            new
            {
                sessionId = sessionId.Value,
                prompt,
            },
            cancellationToken).ConfigureAwait(false);

        var rawStopReason = ReadRequiredString(response, "stopReason");
        return new PromptResult(MapStopReason(rawStopReason), rawStopReason);
    }

    private static IReadOnlyList<object> BuildPromptBlocks(
        string text,
        IReadOnlyList<PromptAttachment> attachments)
    {
        _ = PromptAttachmentPolicy.Validate(attachments);

        var blocks = new List<object>(attachments.Count + 1);
        if (!string.IsNullOrWhiteSpace(text))
        {
            blocks.Add(new { type = "text", text });
        }

        foreach (var attachment in attachments)
        {
            blocks.Add(new
            {
                type = "image",
                data = attachment.Base64Data,
                mimeType = attachment.MimeType,
            });
        }

        return blocks;
    }

    public async Task CancelAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        try
        {
            await _connection.SendNotificationAsync(
                    "session/cancel",
                    new { sessionId = sessionId.Value },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CancelPendingPermissions(sessionId);
        }
    }

    public Task<bool> RespondToPermissionAsync(
        string requestId,
        PermissionDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_pendingPermissions.TryGetValue(requestId, out var pending))
        {
            return Task.FromResult(false);
        }

        if (decision.Kind == PermissionDecisionKind.Selected &&
            (decision.OptionId is null || !pending.OptionIds.Contains(decision.OptionId)))
        {
            pending.Completion.TrySetResult(PermissionDecision.Cancelled);
            return Task.FromResult(false);
        }

        return Task.FromResult(pending.Completion.TrySetResult(decision));
    }

    private async Task<JsonElement?> ProbeExtensionAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _connection.SendRequestAsync(method, parameters, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonRpcException exception) when (exception.Code == -32601)
        {
            return null;
        }
    }

    private static IReadOnlyCollection<SessionMode> ParseSessionModes(JsonElement? initialize)
    {
        if (initialize is null ||
            !initialize.Value.TryGetProperty("sessionModes", out var modes))
        {
            return [SessionMode.Default];
        }

        if (modes.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "The AgentDesk sessionModes capability must be an array.");
        }

        var supportsPlan = false;
        foreach (var item in modes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    "The AgentDesk sessionModes capability contains a non-string value.");
            }

            supportsPlan |= string.Equals(item.GetString(), "plan", StringComparison.Ordinal);
        }

        return supportsPlan
            ? [SessionMode.Default, SessionMode.Plan]
            : [SessionMode.Default];
    }

    private static MemoryManagementCapabilities ParseMemoryCapabilities(JsonElement? initialize)
    {
        if (initialize is null ||
            !initialize.Value.TryGetProperty("memory", out var memory))
        {
            return MemoryManagementCapabilities.Unsupported;
        }
        if (memory.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The AgentDesk memory capability must be an object.");
        }

        EnsureOnlyProperties(
            memory,
            "AgentDesk memory capability",
            "schemaVersion",
            "list",
            "read",
            "write",
            "delete",
            "mutationConfirmationRequired");
        var schemaVersion = ReadRequiredInt32(memory, "schemaVersion");
        if (schemaVersion != 1)
        {
            throw new NotSupportedException(
                $"AgentDesk Memory schema version {schemaVersion} is not supported.");
        }

        var capabilities = new MemoryManagementCapabilities(
            schemaVersion,
            ReadRequiredBoolean(memory, "list"),
            ReadRequiredBoolean(memory, "read"),
            ReadRequiredBoolean(memory, "write"),
            ReadRequiredBoolean(memory, "delete"),
            ReadRequiredBoolean(memory, "mutationConfirmationRequired"));
        if ((capabilities.Write || capabilities.Delete) &&
            !capabilities.MutationConfirmationRequired)
        {
            throw new InvalidDataException(
                "The AgentDesk memory capability advertised unchecked mutation.");
        }
        return capabilities;
    }

    private async Task<HealthAttestation?> ReadHealthAsync(
        CancellationToken cancellationToken)
    {
        JsonElement response;
        try
        {
            response = await _connection.SendRequestAsync(
                    "agentdesk/v1/health",
                    new { },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonRpcException exception) when (exception.Code == -32601)
        {
            return null;
        }

        if (!string.Equals(
                ReadRequiredString(response, "status"),
                "ok",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The AgentDesk engine health status was not 'ok'.");
        }

        var sandbox = TryGetObject(response, "sandbox") ?? throw new InvalidDataException(
            "The AgentDesk engine health response did not contain sandbox attestation.");
        var configuredProfile = ReadRequiredString(sandbox, "configuredProfile");
        var active = ReadRequiredBoolean(sandbox, "active");
        var childNetworkRestricted = ReadRequiredBoolean(
            sandbox,
            "childNetworkRestricted");
        var enforcementRequired = ReadRequiredBoolean(sandbox, "enforcementRequired");
        var activeProfile = sandbox.TryGetProperty("activeProfile", out var activeProfileElement) &&
            activeProfileElement.ValueKind == JsonValueKind.String
                ? activeProfileElement.GetString()
                : null;
        return new HealthAttestation(
            active &&
            childNetworkRestricted &&
            enforcementRequired &&
            string.Equals(configuredProfile, "strict", StringComparison.Ordinal) &&
            string.Equals(activeProfile, "strict", StringComparison.Ordinal));
    }

    private void OnNotificationReceived(object? sender, JsonRpcNotification notification)
    {
        if ((!string.Equals(notification.Method, "session/update", StringComparison.Ordinal) &&
             !string.Equals(notification.Method, "x.ai/session/update", StringComparison.Ordinal)) ||
            notification.Parameters.ValueKind != JsonValueKind.Object ||
            !notification.Parameters.TryGetProperty("sessionId", out var sessionIdElement) ||
            sessionIdElement.ValueKind != JsonValueKind.String ||
            sessionIdElement.GetString() is not { Length: > 0 } sessionId ||
            !notification.Parameters.TryGetProperty("update", out var update) ||
            update.ValueKind != JsonValueKind.Object ||
            !update.TryGetProperty("sessionUpdate", out var updateKindElement) ||
            updateKindElement.ValueKind != JsonValueKind.String ||
            updateKindElement.GetString() is not { Length: > 0 } updateKind)
        {
            return;
        }

        JsonElement? metadata = notification.Parameters.TryGetProperty("_meta", out var metadataElement)
            ? metadataElement.Clone()
            : null;
        EventReceived?.Invoke(
            this,
            new EngineEvent(
                new SessionId(sessionId),
                updateKind,
                update.Clone(),
                metadata));
    }

    private void OnRequestReceived(object? sender, JsonRpcRequestEventArgs request)
    {
        if (string.Equals(request.Method, "session/request_permission", StringComparison.Ordinal))
        {
            _ = request.TryHandle(
                cancellationToken => HandlePermissionRequestAsync(
                    request.Parameters,
                    cancellationToken));
        }
    }

    private void OnConnectionFaulted(object? sender, EngineFaultedEventArgs args)
    {
        var handlers = Faulted;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<EngineFaultedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception)
            {
                // One host subscriber must not prevent the others from observing the fault.
            }
        }
    }

    private async Task<JsonRpcResponse> HandlePermissionRequestAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var parsed = TryParsePermissionRequest(parameters);
        if (parsed.Status == PermissionParseStatus.UnknownOptionKind)
        {
            return CancelledPermissionResponse();
        }

        if (parsed.Status == PermissionParseStatus.Invalid || parsed.Request is null)
        {
            return JsonRpcResponse.Failure(-32602, "Invalid params");
        }

        var request = parsed.Request;
        var pending = new PendingPermission(
            request,
            request.Options.Select(option => option.OptionId).ToHashSet(StringComparer.Ordinal));
        if (!_pendingPermissions.TryAdd(request.RequestId, pending))
        {
            return JsonRpcResponse.Failure(-32603, "Internal error");
        }

        try
        {
            if (!RaisePermissionRequested(request))
            {
                return CancelledPermissionResponse();
            }

            PermissionDecision decision;
            try
            {
                decision = await pending.Completion.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                decision = PermissionDecision.Cancelled;
            }

            return decision.Kind == PermissionDecisionKind.Selected &&
                decision.OptionId is { } optionId &&
                pending.OptionIds.Contains(optionId)
                    ? SelectedPermissionResponse(optionId)
                    : CancelledPermissionResponse();
        }
        finally
        {
            _pendingPermissions.TryRemove(request.RequestId, out _);
        }
    }

    private bool RaisePermissionRequested(PermissionRequest request)
    {
        var handlers = PermissionRequested;
        if (handlers is null)
        {
            return false;
        }

        var delivered = false;
        foreach (EventHandler<PermissionRequest> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, request);
                delivered = true;
            }
            catch (Exception)
            {
            }
        }

        return delivered;
    }

    private void CancelPendingPermissions(SessionId sessionId)
    {
        foreach (var pending in _pendingPermissions.Values)
        {
            if (string.Equals(
                pending.Request.SessionId.Value,
                sessionId.Value,
                StringComparison.Ordinal))
            {
                pending.Completion.TrySetResult(PermissionDecision.Cancelled);
            }
        }
    }

    private static PermissionParseResult TryParsePermissionRequest(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !TryReadNonEmptyString(parameters, "sessionId", out var sessionId) ||
            !parameters.TryGetProperty("toolCall", out var toolCall) ||
            toolCall.ValueKind != JsonValueKind.Object ||
            !TryReadNonEmptyString(toolCall, "toolCallId", out var toolCallId) ||
            !parameters.TryGetProperty("options", out var optionsElement) ||
            optionsElement.ValueKind != JsonValueKind.Array)
        {
            return PermissionParseResult.Invalid;
        }

        var options = new List<PermissionOption>();
        foreach (var optionElement in optionsElement.EnumerateArray())
        {
            if (optionElement.ValueKind != JsonValueKind.Object ||
                !TryReadNonEmptyString(optionElement, "optionId", out var optionId) ||
                !TryReadNonEmptyString(optionElement, "name", out var optionName) ||
                !TryReadNonEmptyString(optionElement, "kind", out var rawKind))
            {
                return PermissionParseResult.Invalid;
            }

            if (!TryMapPermissionOptionKind(rawKind, out var kind))
            {
                return PermissionParseResult.UnknownOptionKind;
            }

            options.Add(new PermissionOption(optionId, optionName, kind));
        }

        if (options.Count == 0)
        {
            return PermissionParseResult.Invalid;
        }

        var title = TryReadNonEmptyString(toolCall, "title", out var parsedTitle)
            ? parsedTitle
            : toolCallId;
        var toolKind = TryReadNonEmptyString(toolCall, "kind", out var parsedKind)
            ? parsedKind
            : null;
        JsonElement? rawInput = toolCall.TryGetProperty("rawInput", out var rawInputElement)
            ? rawInputElement.Clone()
            : null;
        var locations = ReadLocations(toolCall);
        var request = new PermissionRequest(
            Guid.NewGuid().ToString("N"),
            new SessionId(sessionId),
            toolCallId,
            title,
            options,
            locations,
            toolKind,
            rawInput);
        return new PermissionParseResult(PermissionParseStatus.Valid, request);
    }

    private static IReadOnlyList<string> ReadLocations(JsonElement toolCall)
    {
        if (!toolCall.TryGetProperty("locations", out var locationsElement) ||
            locationsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var locations = new List<string>();
        foreach (var location in locationsElement.EnumerateArray())
        {
            if (location.ValueKind != JsonValueKind.Object ||
                !TryReadNonEmptyString(location, "path", out var path))
            {
                continue;
            }

            locations.Add(
                location.TryGetProperty("line", out var lineElement) &&
                lineElement.TryGetUInt32(out var line)
                    ? $"{path}:{line}"
                    : path);
        }

        return locations;
    }

    private static bool TryMapPermissionOptionKind(
        string rawKind,
        out PermissionOptionKind kind)
    {
        kind = rawKind switch
        {
            "allow_once" => PermissionOptionKind.AllowOnce,
            "allow_always" => PermissionOptionKind.AllowAlways,
            "reject_once" => PermissionOptionKind.RejectOnce,
            "reject_always" => PermissionOptionKind.RejectAlways,
            _ => default,
        };
        return rawKind is "allow_once" or "allow_always" or "reject_once" or "reject_always";
    }

    private static bool TryReadNonEmptyString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            property.GetString() is { } text &&
            !string.IsNullOrWhiteSpace(text))
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static JsonRpcResponse SelectedPermissionResponse(string optionId) =>
        JsonRpcResponse.Success(new
        {
            outcome = new { outcome = "selected", optionId },
        });

    private static JsonRpcResponse CancelledPermissionResponse() =>
        JsonRpcResponse.Success(new
        {
            outcome = new { outcome = "cancelled" },
        });

    private static WorktreeCreateResult ParseWorktreeCreateResult(
        JsonElement result,
        SessionId expectedSessionId)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The worktree creation response did not contain an object.");
        }

        var rawStatus = ReadRequiredString(result, "status");
        var status = rawStatus switch
        {
            "creating" => WorktreeCreateStatus.Creating,
            "exists" => WorktreeCreateStatus.Exists,
            _ => throw new InvalidDataException(
                "The engine returned an unsupported worktree creation status."),
        };
        EnsureOnlyProperties(
            result,
            "worktree creation response",
            status == WorktreeCreateStatus.Exists
                ? ["status", "sessionId", "worktreePath", "commit", "sourceGitRoot"]
                : ["status", "sessionId", "worktreePath", "sourceGitRoot"]);

        var sessionId = ReadRequiredBoundedControlFreeString(
            result,
            "sessionId",
            MaximumSessionIdLength);
        if (!string.Equals(sessionId, expectedSessionId.Value, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The worktree creation response belonged to another session.");
        }

        var commit = status == WorktreeCreateStatus.Exists
            ? ReadRequiredCommit(result, "commit")
            : null;
        return new WorktreeCreateResult(
            status,
            new SessionId(sessionId),
            ReadRequiredWorktreePath(result, "worktreePath"),
            ReadOptionalWorktreePath(result, "sourceGitRoot"),
            commit);
    }

    private static WorktreeRecord ParseWorktreeRecord(JsonElement item)
    {
        EnsureOnlyProperties(
            item,
            "worktree record",
            "id",
            "path",
            "source_repo",
            "repo_name",
            "kind",
            "creation_mode",
            "git_ref",
            "head_commit",
            "session_id",
            "creator_pid",
            "created_at",
            "last_accessed_at",
            "status",
            "metadata");

        var gitReference = ReadOptionalBoundedControlFreeString(
            item,
            "git_ref",
            MaximumWorktreeGitReferenceLength);
        if (gitReference is not null)
        {
            try
            {
                _ = ValidateOptionalGitReference(gitReference, "git_ref");
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "The worktree record contained an invalid git reference.",
                    exception);
            }
        }

        var sessionId = ReadOptionalBoundedControlFreeString(
            item,
            "session_id",
            MaximumSessionIdLength);
        return new WorktreeRecord(
            ReadRequiredWorktreeId(item, "id"),
            ReadRequiredWorktreePath(item, "path"),
            ReadRequiredWorktreePath(item, "source_repo"),
            ReadRequiredBoundedControlFreeString(
                item,
                "repo_name",
                MaximumWorktreeRepositoryNameLength),
            ParseWorktreeKind(ReadRequiredString(item, "kind")),
            ParseWorktreeCreationType(ReadRequiredString(item, "creation_mode")),
            gitReference,
            ReadOptionalCommit(item, "head_commit"),
            sessionId is null ? null : new SessionId(sessionId),
            ReadOptionalUInt32(item, "creator_pid"),
            ReadRequiredUnixTimestamp(item, "created_at"),
            ReadOptionalUnixTimestamp(item, "last_accessed_at"),
            ParseWorktreeRecordStatus(ReadRequiredString(item, "status")),
            ParseWorktreeMetadata(item));
    }

    private static WorktreeMetadata? ParseWorktreeMetadata(JsonElement item)
    {
        if (!item.TryGetProperty("metadata", out var metadata) ||
            metadata.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        EnsureOnlyProperties(metadata, "worktree metadata", "label", "user_provided");
        return new WorktreeMetadata(
            ReadRequiredBoundedControlFreeString(
                metadata,
                "label",
                MaximumWorktreeLabelLength),
            ReadRequiredBoolean(metadata, "user_provided"));
    }

    private static WorktreeApplyResult ParseWorktreeApplyResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The worktree apply response did not contain an object.");
        }

        var status = ReadRequiredString(result, "status") switch
        {
            "success" => WorktreeApplyStatus.Success,
            "conflicts" => WorktreeApplyStatus.Conflicts,
            _ => throw new InvalidDataException(
                "The engine returned an unsupported worktree apply status."),
        };
        EnsureOnlyProperties(
            result,
            "worktree apply response",
            status == WorktreeApplyStatus.Success
                ? ["status", "files", "gitRoot"]
                : ["status", "files", "conflicts"]);

        var remainingText = MaximumWorktreeAggregateTextLength;
        var files = ParseWorktreeFileChanges(
            ReadRequiredArray(result, "files"),
            ref remainingText);
        if (status == WorktreeApplyStatus.Success)
        {
            return new WorktreeApplyResult(
                status,
                files,
                Array.Empty<WorktreeConflict>(),
                ReadRequiredWorktreePath(result, "gitRoot"));
        }

        var conflicts = ReadRequiredArray(result, "conflicts");
        if (conflicts.GetArrayLength() > MaximumWorktreeChangeCount)
        {
            throw new InvalidDataException(
                "The worktree apply response contained too many conflicts.");
        }
        var parsedConflicts = new List<WorktreeConflict>(conflicts.GetArrayLength());
        foreach (var conflict in conflicts.EnumerateArray())
        {
            parsedConflicts.Add(ParseWorktreeConflict(conflict, ref remainingText));
        }
        return new WorktreeApplyResult(status, files, parsedConflicts);
    }

    private static IReadOnlyList<WorktreeFileChange> ParseWorktreeFileChanges(
        JsonElement files,
        ref int remainingText)
    {
        if (files.GetArrayLength() > MaximumWorktreeChangeCount)
        {
            throw new InvalidDataException(
                "The worktree apply response contained too many file changes.");
        }

        var parsed = new List<WorktreeFileChange>(files.GetArrayLength());
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files.EnumerateArray())
        {
            var change = ParseWorktreeFileChange(file, ref remainingText);
            if (!paths.Add(change.Path))
            {
                throw new InvalidDataException(
                    "The worktree apply response contained a duplicate file path.");
            }
            parsed.Add(change);
        }
        return parsed;
    }

    private static WorktreeFileChange ParseWorktreeFileChange(
        JsonElement file,
        ref int remainingText)
    {
        EnsureOnlyProperties(
            file,
            "worktree file change",
            "path",
            "oldPath",
            "type",
            "staged",
            "additions",
            "deletions",
            "patch",
            "patchBytes",
            "patchLines",
            "oldText",
            "newText");
        return new WorktreeFileChange(
            ReadRequiredRelativeWorktreePath(file, "path"),
            ReadOptionalRelativeWorktreePath(file, "oldPath"),
            ParseWorktreeChangeType(ReadRequiredString(file, "type")),
            ReadOptionalBoolean(file, "staged"),
            ReadRequiredUInt64(file, "additions"),
            ReadRequiredUInt64(file, "deletions"),
            ReadOptionalBoundedWorktreeText(file, "patch", ref remainingText),
            ReadOptionalUInt64(file, "patchBytes"),
            ReadOptionalUInt64(file, "patchLines"),
            ReadOptionalBoundedWorktreeText(file, "oldText", ref remainingText),
            ReadOptionalBoundedWorktreeText(file, "newText", ref remainingText));
    }

    private static WorktreeConflict ParseWorktreeConflict(
        JsonElement conflict,
        ref int remainingText)
    {
        EnsureOnlyProperties(
            conflict,
            "worktree conflict",
            "path",
            "type",
            "base",
            "ours",
            "theirs");
        return new WorktreeConflict(
            ReadRequiredRelativeWorktreePath(conflict, "path"),
            ParseWorktreeChangeType(ReadRequiredString(conflict, "type")),
            ReadOptionalBoundedWorktreeText(conflict, "base", ref remainingText),
            ReadOptionalBoundedWorktreeText(conflict, "ours", ref remainingText),
            ReadOptionalBoundedWorktreeText(conflict, "theirs", ref remainingText));
    }

    private static WorktreeRemoveResult ParseWorktreeRemoveResult(JsonElement result)
    {
        EnsureOnlyProperties(result, "worktree removal response", "removed", "resolvedPath");
        return new WorktreeRemoveResult(
            ReadRequiredBoolean(result, "removed"),
            ReadOptionalWorktreePath(result, "resolvedPath"));
    }

    private static WorktreeGcResult ParseWorktreeGcResult(JsonElement result)
    {
        EnsureOnlyProperties(
            result,
            "worktree garbage collection response",
            "dead_removed",
            "expired_removed",
            "skipped_alive",
            "remove_failed");
        return new WorktreeGcResult(
            ReadRequiredUInt64(result, "dead_removed"),
            ReadRequiredUInt64(result, "expired_removed"),
            ReadRequiredUInt64(result, "skipped_alive"),
            ReadRequiredUInt64(result, "remove_failed"));
    }

    private static JsonElement ReadWorktreeExtensionResult(
        JsonElement response,
        bool allowNull = false)
    {
        EnsureOnlyProperties(response, "worktree extension response", "result", "error");
        if (response.TryGetProperty("error", out var error) &&
            error.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidDataException("The worktree extension returned an error.");
        }
        if (!response.TryGetProperty("result", out var result) ||
            (!allowNull && result.ValueKind == JsonValueKind.Null))
        {
            throw new InvalidDataException(
                "The worktree extension response did not contain a result.");
        }
        return result;
    }

    private static void EnsureOnlyProperties(
        JsonElement element,
        string context,
        params string[] allowedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The {context} was not an object.");
        }
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"The {context} contained an unsupported field.");
            }
        }
    }

    private static string ValidateWorktreePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > MaximumWorkingDirectoryLength ||
            path.Any(char.IsControl) ||
            !string.Equals(path, path.Trim(), StringComparison.Ordinal) ||
            path.Contains('"') ||
            !IsAbsoluteWorktreePath(path) ||
            HasTraversalSegments(path) ||
            IsUnsafeWindowsWorktreePath(path))
        {
            throw new ArgumentException("The worktree path is invalid.", parameterName);
        }
        return path;
    }

    private static string ValidateWorktreeSelector(string selector, string parameterName)
    {
        if (IsAbsoluteWorktreePath(selector))
        {
            return ValidateWorktreePath(selector, parameterName);
        }
        return ValidateWorktreeIdentifier(selector, parameterName);
    }

    private static string ValidateWorktreeIdentifier(string identifier, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(identifier) ||
            IsAbsoluteWorktreePath(identifier) ||
            identifier.Length > MaximumWorktreeIdLength ||
            identifier.Any(char.IsControl) ||
            identifier[0] is '-' or '.' ||
            identifier.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("The worktree identifier is invalid.", parameterName);
        }
        return identifier;
    }

    private static string? ValidateOptionalRepositoryFilter(
        string? repository,
        string parameterName)
    {
        if (repository is null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(repository) ||
            repository.Length > MaximumWorktreeRepositoryNameLength ||
            repository.Any(char.IsControl) ||
            repository.Contains('/') ||
            repository.Contains('\\') ||
            !string.Equals(repository, repository.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The worktree repository filter is invalid.",
                parameterName);
        }
        return repository;
    }

    private static IReadOnlyList<string> ValidateWorktreeKinds(
        IReadOnlyList<WorktreeKind>? kinds,
        string parameterName)
    {
        if (kinds is null || kinds.Count == 0)
        {
            return Array.Empty<string>();
        }
        if (kinds.Count > Enum.GetValues<WorktreeKind>().Length ||
            kinds.Distinct().Count() != kinds.Count)
        {
            throw new ArgumentException("The worktree kind filter is invalid.", parameterName);
        }
        return kinds.Select(WorktreeKindName).ToArray();
    }

    private static IReadOnlyList<string> ValidateWorktreeSkipPatterns(
        IReadOnlyList<string>? patterns,
        string parameterName)
    {
        if (patterns is null || patterns.Count == 0)
        {
            return Array.Empty<string>();
        }
        if (patterns.Count > MaximumWorktreeSkipPatternCount)
        {
            throw new ArgumentException(
                "The worktree ignored-file pattern list is too large.",
                parameterName);
        }

        var validated = new string[patterns.Count];
        for (var index = 0; index < patterns.Count; index++)
        {
            var pattern = patterns[index];
            if (string.IsNullOrWhiteSpace(pattern) ||
                pattern.Length > MaximumWorktreeSkipPatternLength ||
                pattern.Any(char.IsControl) ||
                IsAbsoluteWorktreePath(pattern) ||
                HasTraversalSegments(pattern))
            {
                throw new ArgumentException(
                    "A worktree ignored-file pattern is invalid.",
                    parameterName);
            }
            validated[index] = pattern;
        }
        return validated;
    }

    private static string? ValidateOptionalWorktreeLabel(
        string? label,
        string parameterName)
    {
        if (label is null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(label) ||
            label.Length > MaximumWorktreeLabelLength ||
            label.Any(char.IsControl) ||
            label.Contains('/') ||
            label.Contains('\\') ||
            label is "." or ".." ||
            !string.Equals(label, label.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The worktree label is invalid.", parameterName);
        }
        return label;
    }

    private static string? ValidateOptionalGitReference(
        string? gitReference,
        string parameterName)
    {
        if (gitReference is null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(gitReference) ||
            gitReference.Length > MaximumWorktreeGitReferenceLength ||
            gitReference.Any(char.IsControl) ||
            !char.IsAsciiLetterOrDigit(gitReference[0]) ||
            gitReference.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '/' and not '.' and not '_' and not '-') ||
            gitReference.Contains("..", StringComparison.Ordinal) ||
            gitReference.Contains("@{", StringComparison.Ordinal) ||
            gitReference.Contains("//", StringComparison.Ordinal) ||
            gitReference.EndsWith("/", StringComparison.Ordinal) ||
            gitReference.EndsWith(".", StringComparison.Ordinal))
        {
            throw new ArgumentException("The git reference is invalid.", parameterName);
        }
        foreach (var segment in gitReference.Split('/'))
        {
            if (segment.Length == 0 ||
                segment.StartsWith(".", StringComparison.Ordinal) ||
                segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The git reference is invalid.", parameterName);
            }
        }
        return gitReference;
    }

    private static string? FormatWorktreeGcAge(TimeSpan? maximumAge, string parameterName)
    {
        if (maximumAge is null)
        {
            return null;
        }
        if (maximumAge <= TimeSpan.Zero || maximumAge > MaximumWorktreeGcAge)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                maximumAge,
                "The worktree garbage-collection age is outside the supported range.");
        }
        if (maximumAge.Value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException(
                "The worktree garbage-collection age must use whole seconds.",
                parameterName);
        }

        var seconds = checked((long)maximumAge.Value.TotalSeconds);
        if (seconds % 86400 == 0)
        {
            return $"{seconds / 86400}d";
        }
        if (seconds % 3600 == 0)
        {
            return $"{seconds / 3600}h";
        }
        if (seconds % 60 == 0)
        {
            return $"{seconds / 60}m";
        }
        return $"{seconds}s";
    }

    private static bool IsAbsoluteWorktreePath(string path) =>
        !string.IsNullOrEmpty(path) &&
        (path[0] == '/' ||
         (path.Length >= 2 && path[0] == '\\' && path[1] == '\\') ||
         (path.Length >= 3 &&
          char.IsAsciiLetter(path[0]) &&
          path[1] == ':' &&
          path[2] is '\\' or '/'));

    private static bool IsUnsafeWindowsWorktreePath(string path)
    {
        var isDrivePath = path.Length >= 3 &&
            char.IsAsciiLetter(path[0]) &&
            path[1] == ':' &&
            path[2] is '\\' or '/';
        var isUncPath = path.Length >= 2 && path[0] == '\\' && path[1] == '\\';
        if (!isDrivePath && !isUncPath)
        {
            return false;
        }
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            path.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            path[(isDrivePath ? 2 : 0)..].Contains(':'))
        {
            return true;
        }

        foreach (var component in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (component.Length == 2 && component[1] == ':' && char.IsAsciiLetter(component[0]))
            {
                continue;
            }
            if (component.EndsWith(' ') || component.EndsWith('.'))
            {
                return true;
            }
            var deviceName = component.Split('.', 2)[0];
            if (IsWindowsReservedDeviceName(deviceName))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsWindowsReservedDeviceName(string value)
    {
        if (value.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return value.Length == 4 &&
            (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            value[3] is >= '1' and <= '9';
    }

    private static bool HasTraversalSegments(string value) =>
        value.Split(['/', '\\'], StringSplitOptions.None)
            .Any(segment => segment is "." or "..");

    private static string ReadRequiredWorktreeId(JsonElement element, string propertyName)
    {
        var value = ReadRequiredBoundedControlFreeString(
            element,
            propertyName,
            MaximumWorktreeIdLength);
        try
        {
            return ValidateWorktreeIdentifier(value, propertyName);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"The engine response did not contain a valid '{propertyName}'.",
                exception);
        }
    }

    private static string ReadRequiredWorktreePath(JsonElement element, string propertyName)
    {
        var value = ReadRequiredBoundedControlFreeString(
            element,
            propertyName,
            MaximumWorkingDirectoryLength);
        try
        {
            return ValidateWorktreePath(value, propertyName);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"The engine response contained an invalid '{propertyName}' path.",
                exception);
        }
    }

    private static string? ReadOptionalWorktreePath(JsonElement element, string propertyName)
    {
        var value = ReadOptionalBoundedControlFreeString(
            element,
            propertyName,
            MaximumWorkingDirectoryLength);
        if (value is null)
        {
            return null;
        }
        try
        {
            return ValidateWorktreePath(value, propertyName);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"The engine response contained an invalid '{propertyName}' path.",
                exception);
        }
    }

    private static string ReadRequiredRelativeWorktreePath(
        JsonElement element,
        string propertyName)
    {
        var path = ReadRequiredBoundedControlFreeString(
            element,
            propertyName,
            MaximumWorkingDirectoryLength);
        if (IsAbsoluteWorktreePath(path) || HasTraversalSegments(path))
        {
            throw new InvalidDataException(
                $"The engine response contained an invalid '{propertyName}' file path.");
        }
        return path;
    }

    private static string? ReadOptionalRelativeWorktreePath(
        JsonElement element,
        string propertyName)
    {
        var path = ReadOptionalBoundedControlFreeString(
            element,
            propertyName,
            MaximumWorkingDirectoryLength);
        if (path is not null && (IsAbsoluteWorktreePath(path) || HasTraversalSegments(path)))
        {
            throw new InvalidDataException(
                $"The engine response contained an invalid '{propertyName}' file path.");
        }
        return path;
    }

    private static string ReadRequiredBoundedControlFreeString(
        JsonElement element,
        string propertyName,
        int maximumLength)
    {
        var value = ReadRequiredBoundedString(
            element,
            propertyName,
            maximumLength,
            allowEmpty: false);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"The engine response did not contain a valid '{propertyName}'.");
        }
        return value;
    }

    private static string? ReadOptionalBoundedControlFreeString(
        JsonElement element,
        string propertyName,
        int maximumLength)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.ValueKind == JsonValueKind.String &&
            property.GetString() is { Length: > 0 } value &&
            value.Length <= maximumLength &&
            !value.Any(char.IsControl) &&
            !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
        throw new InvalidDataException(
            $"The engine response contained an invalid '{propertyName}'.");
    }

    private static string? ReadOptionalBoundedWorktreeText(
        JsonElement element,
        string propertyName,
        ref int remainingText)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } value ||
            value.Length > MaximumWorktreeTextLength ||
            value.Length > remainingText)
        {
            throw new InvalidDataException(
                $"The engine response contained an invalid '{propertyName}'.");
        }
        remainingText -= value.Length;
        return value;
    }

    private static string ReadRequiredCommit(JsonElement element, string propertyName) =>
        ReadCommit(element, propertyName, required: true)!;

    private static string? ReadOptionalCommit(JsonElement element, string propertyName) =>
        ReadCommit(element, propertyName, required: false);

    private static string? ReadCommit(
        JsonElement element,
        string propertyName,
        bool required)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            if (!required)
            {
                return null;
            }
            throw new InvalidDataException(
                $"The engine response did not contain a valid '{propertyName}'.");
        }
        if (property.ValueKind == JsonValueKind.String &&
            property.GetString() is { Length: >= 7 } value &&
            value.Length <= MaximumWorktreeCommitLength &&
            value.All(Uri.IsHexDigit))
        {
            return value;
        }
        throw new InvalidDataException(
            $"The engine response contained an invalid '{propertyName}'.");
    }

    private static bool? ReadOptionalBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return property.GetBoolean();
        }
        throw new InvalidDataException(
            $"The engine response contained an invalid '{propertyName}'.");
    }

    private static uint? ReadOptionalUInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.TryGetUInt32(out var value))
        {
            return value;
        }
        throw new InvalidDataException(
            $"The engine response contained an invalid '{propertyName}'.");
    }

    private static ulong? ReadOptionalUInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.TryGetUInt64(out var value))
        {
            return value;
        }
        throw new InvalidDataException(
            $"The engine response contained an invalid '{propertyName}'.");
    }

    private static DateTimeOffset ReadRequiredUnixTimestamp(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            !property.TryGetInt64(out var seconds) ||
            seconds < 0)
        {
            throw new InvalidDataException(
                $"The engine response contained an invalid '{propertyName}' timestamp.");
        }
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                $"The engine response contained an invalid '{propertyName}' timestamp.",
                exception);
        }
    }

    private static DateTimeOffset? ReadOptionalUnixTimestamp(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return ReadRequiredUnixTimestamp(element, propertyName);
    }

    private static string WorktreeCopyModeName(WorktreeCopyMode mode) => mode switch
    {
        WorktreeCopyMode.Clean => "clean",
        WorktreeCopyMode.Dirty => "dirty",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string WorktreeCreationTypeName(WorktreeCreationType type) => type switch
    {
        WorktreeCreationType.Linked => "linked",
        WorktreeCreationType.Standalone => "standalone",
        WorktreeCreationType.Git => "git",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static WorktreeCreationType ParseWorktreeCreationType(string type) => type switch
    {
        "linked" => WorktreeCreationType.Linked,
        "standalone" => WorktreeCreationType.Standalone,
        "git" => WorktreeCreationType.Git,
        _ => throw new InvalidDataException(
            "The engine returned an unsupported worktree creation mode."),
    };

    private static string WorktreeApplyModeName(WorktreeApplyMode mode) => mode switch
    {
        WorktreeApplyMode.Overwrite => "overwrite",
        WorktreeApplyMode.Merge => "merge",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
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

    private static WorktreeKind ParseWorktreeKind(string kind) => kind switch
    {
        "session" => WorktreeKind.Session,
        "ab" => WorktreeKind.Ab,
        "pool" => WorktreeKind.Pool,
        "fork" => WorktreeKind.Fork,
        "manual" => WorktreeKind.Manual,
        "subagent" => WorktreeKind.Subagent,
        _ => throw new InvalidDataException(
            "The engine returned an unsupported worktree kind."),
    };

    private static WorktreeRecordStatus ParseWorktreeRecordStatus(string status) => status switch
    {
        "alive" => WorktreeRecordStatus.Alive,
        "dead" => WorktreeRecordStatus.Dead,
        _ => throw new InvalidDataException(
            "The engine returned an unsupported worktree record status."),
    };

    private static WorktreeChangeType ParseWorktreeChangeType(string type) => type switch
    {
        "create" => WorktreeChangeType.Create,
        "edit" => WorktreeChangeType.Edit,
        "delete" => WorktreeChangeType.Delete,
        "rename" => WorktreeChangeType.Rename,
        "copy" => WorktreeChangeType.Copy,
        "typechange" => WorktreeChangeType.TypeChange,
        "untracked" => WorktreeChangeType.Untracked,
        _ => throw new InvalidDataException(
            "The engine returned an unsupported worktree change type."),
    };

    private static bool HasAuthenticationMethod(JsonElement response, string methodId)
    {
        if (!response.TryGetProperty("authMethods", out var authMethods) ||
            authMethods.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var method in authMethods.EnumerateArray())
        {
            if (method.ValueKind == JsonValueKind.Object &&
                method.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), methodId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonElement? TryGetObject(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Object
                ? property
                : null;
    }

    private static bool ReadBoolean(JsonElement? element, string propertyName)
    {
        return element is { ValueKind: JsonValueKind.Object } value &&
            value.TryGetProperty(propertyName, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            property.GetBoolean();
    }

    private static int ReadRequiredInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.TryGetInt32(out var value))
        {
            return value;
        }

        throw new InvalidDataException($"The engine response did not contain a valid '{propertyName}'.");
    }

    private static int ReadRequiredNonNegativeInt32(JsonElement element, string propertyName)
    {
        var value = ReadRequiredInt32(element, propertyName);
        if (value >= 0)
        {
            return value;
        }
        throw new InvalidDataException($"The engine response contained a negative '{propertyName}'.");
    }

    private static bool ReadRequiredBoolean(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return property.GetBoolean();
        }

        throw new InvalidDataException($"The engine response did not contain a valid '{propertyName}'.");
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            property.GetString() is { Length: > 0 } value)
        {
            return value;
        }

        throw new InvalidDataException($"The engine response did not contain a valid '{propertyName}'.");
    }

    private static string ReadRequiredBoundedString(
        JsonElement element,
        string propertyName,
        int maximumLength,
        bool allowEmpty)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            property.GetString() is { } value &&
            (allowEmpty || value.Length > 0) &&
            value.Length <= maximumLength &&
            !value.Any(char.IsControl))
        {
            return value;
        }

        throw new InvalidDataException(
            $"The engine response did not contain a valid '{propertyName}'.");
    }

    private static string ReadRequiredBoundedText(
        JsonElement element,
        string propertyName,
        int maximumLength,
        bool allowEmpty)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            property.GetString() is { } value &&
            (allowEmpty || value.Length > 0) &&
            value.Length <= maximumLength)
        {
            return value;
        }

        throw new InvalidDataException(
            $"The engine response did not contain a valid '{propertyName}'.");
    }

    private static string? ReadOptionalBoundedText(
        JsonElement element,
        string propertyName,
        int maximumLength)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.ValueKind == JsonValueKind.String &&
            property.GetString() is { Length: > 0 } value &&
            value.Length <= maximumLength)
        {
            return value;
        }
        throw new InvalidDataException(
            $"The engine response contained an invalid '{propertyName}'.");
    }

    private static string ReadRequiredRuntimeId(JsonElement element, string propertyName)
    {
        var value = ReadRequiredBoundedString(
            element,
            propertyName,
            MaximumRuntimeIdLength,
            allowEmpty: false);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"The engine response did not contain a valid '{propertyName}'.");
        }
        return value;
    }

    private static void ValidateRuntimeId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumRuntimeIdLength ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException("The runtime item ID is invalid.", parameterName);
        }
    }

    private static JsonElement ReadRequiredExtensionResult(JsonElement response) =>
        TryGetObject(response, "result") ?? throw new InvalidDataException(
            "The engine extension response did not contain a result object.");

    private static void EnsureExtensionSession(JsonElement result, SessionId expectedSessionId)
    {
        if (!string.Equals(
                ReadRequiredString(result, "sessionId"),
                expectedSessionId.Value,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The engine returned a response for another session.");
        }
    }

    private static void EnsureRuntimeItemCount(JsonElement items, string itemName)
    {
        if (items.GetArrayLength() > MaximumRuntimeItemCount)
        {
            throw new InvalidDataException(
                $"The engine returned too many {itemName} items.");
        }
    }

    private static int? ReadOptionalInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.TryGetInt32(out var value))
        {
            return value;
        }
        throw new InvalidDataException(
            $"The engine response contained an invalid '{propertyName}'.");
    }

    private static ulong ReadRequiredUInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.TryGetUInt64(out var value))
        {
            return value;
        }
        throw new InvalidDataException(
            $"The engine response did not contain a valid '{propertyName}'.");
    }

    private static byte ReadRequiredPercentage(JsonElement element, string propertyName)
    {
        var value = ReadRequiredNonNegativeInt32(element, propertyName);
        if (value <= 100)
        {
            return (byte)value;
        }
        throw new InvalidDataException(
            $"The engine response contained an invalid '{propertyName}' percentage.");
    }

    private static DateTimeOffset ReadRequiredSystemTime(
        JsonElement element,
        string propertyName)
    {
        var value = TryGetObject(element, propertyName) ?? throw new InvalidDataException(
            $"The engine response did not contain a valid '{propertyName}'.");
        var seconds = ReadRequiredUInt64(value, "secs_since_epoch");
        var nanoseconds = ReadRequiredUInt64(value, "nanos_since_epoch");
        if (seconds > long.MaxValue || nanoseconds >= 1_000_000_000)
        {
            throw new InvalidDataException(
                $"The engine response contained an invalid '{propertyName}' timestamp.");
        }
        try
        {
            return DateTimeOffset
                .FromUnixTimeSeconds((long)seconds)
                .AddTicks((long)(nanoseconds / 100));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                $"The engine response contained an invalid '{propertyName}' timestamp.",
                exception);
        }
    }

    private static DateTimeOffset? ReadOptionalSystemTime(
        JsonElement element,
        string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return ReadRequiredSystemTime(element, propertyName);
    }

    private static DateTimeOffset ReadRequiredEpochMilliseconds(
        JsonElement element,
        string propertyName)
    {
        var milliseconds = ReadRequiredUInt64(element, propertyName);
        if (milliseconds > long.MaxValue)
        {
            throw new InvalidDataException(
                $"The engine response contained an invalid '{propertyName}' timestamp.");
        }
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                $"The engine response contained an invalid '{propertyName}' timestamp.",
                exception);
        }
    }

    private static TimeSpan ReadRequiredDuration(JsonElement element, string propertyName)
    {
        var milliseconds = ReadRequiredUInt64(element, propertyName);
        try
        {
            return TimeSpan.FromTicks(checked((long)milliseconds * TimeSpan.TicksPerMillisecond));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                $"The engine response contained an invalid '{propertyName}' duration.",
                exception);
        }
    }

    private static string? ValidateOptionalWorkingDirectory(string? workingDirectory)
    {
        if (workingDirectory is null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(workingDirectory) ||
            workingDirectory.Length > MaximumWorkingDirectoryLength ||
            workingDirectory.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The working directory is invalid.",
                nameof(workingDirectory));
        }
        return workingDirectory;
    }

    private static void ValidateSessionId(string sessionId, string parameterName)
    {
        if (sessionId.Length > MaximumSessionIdLength || sessionId.Any(char.IsControl))
        {
            throw new ArgumentException("The session ID is invalid.", parameterName);
        }
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return property.GetString() is { Length: > 0 } value ? value : null;
        }

        throw new InvalidDataException($"The engine response contained an invalid '{propertyName}'.");
    }

    private static DateTimeOffset ReadRequiredTimestamp(JsonElement element, string propertyName)
    {
        var raw = ReadRequiredString(element, propertyName);
        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            return timestamp;
        }

        throw new InvalidDataException($"The engine response contained an invalid '{propertyName}' timestamp.");
    }

    private static JsonElement ReadRequiredArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Array)
        {
            return property;
        }
        throw new InvalidDataException($"The engine response did not contain a valid '{propertyName}' array.");
    }

    private static IReadOnlyList<string> ReadRequiredStringArray(
        JsonElement element,
        string propertyName)
    {
        var array = ReadRequiredArray(element, propertyName);
        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                item.GetString() is not { Length: > 0 } value)
            {
                throw new InvalidDataException(
                    $"The engine response contained an invalid '{propertyName}' item.");
            }
            values.Add(value);
        }
        return values;
    }

    private static string RewindModeName(SessionRewindMode mode) => mode switch
    {
        SessionRewindMode.All => "all",
        SessionRewindMode.ConversationOnly => "conversation_only",
        SessionRewindMode.FilesOnly => "files_only",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static SessionRewindMode ParseRewindMode(string mode) => mode switch
    {
        "all" => SessionRewindMode.All,
        "conversation_only" => SessionRewindMode.ConversationOnly,
        "files_only" => SessionRewindMode.FilesOnly,
        _ => throw new InvalidDataException("The engine returned an unsupported rewind mode."),
    };

    private static EngineStopReason MapStopReason(string stopReason) => stopReason switch
    {
        "end_turn" => EngineStopReason.EndTurn,
        "max_tokens" => EngineStopReason.MaxTokens,
        "max_turn_requests" => EngineStopReason.MaxTurnRequests,
        "refusal" => EngineStopReason.Refusal,
        "cancelled" => EngineStopReason.Cancelled,
        _ => EngineStopReason.Unknown,
    };

    private sealed record HealthAttestation(bool StrictSandboxActive);

    public async ValueTask DisposeAsync()
    {
        _ = Interlocked.Exchange(ref _desktopApiKey, null);
        _connection.NotificationReceived -= OnNotificationReceived;
        _connection.RequestReceived -= OnRequestReceived;
        _connection.Faulted -= OnConnectionFaulted;
        foreach (var pending in _pendingPermissions.Values)
        {
            pending.Completion.TrySetResult(PermissionDecision.Cancelled);
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class PendingPermission(
        PermissionRequest request,
        HashSet<string> optionIds)
    {
        public PermissionRequest Request { get; } = request;

        public HashSet<string> OptionIds { get; } = optionIds;

        public TaskCompletionSource<PermissionDecision> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private enum PermissionParseStatus
    {
        Valid,
        Invalid,
        UnknownOptionKind,
    }

    private sealed record PermissionParseResult(
        PermissionParseStatus Status,
        PermissionRequest? Request)
    {
        public static PermissionParseResult Invalid { get; } =
            new(PermissionParseStatus.Invalid, Request: null);

        public static PermissionParseResult UnknownOptionKind { get; } =
            new(PermissionParseStatus.UnknownOptionKind, Request: null);
    }
}
