using System.Text.Json;
using AgentDesk.App.Automation;
using AgentDesk.App.Cloud;
using AgentDesk.App.Maintenance;
using AgentDesk.App.Notifications;
using AgentDesk.App.Recovery;
using AgentDesk.App.Settings;
using AgentDesk.App.Workspace;
using AgentDesk.Core.Engine;
using AgentDesk.Core.Execution;
using AgentDesk.Core.Providers;
using AgentDesk.Core.Security;
using AgentDesk.Engine.Sidecar;
using AgentDesk.Platform.Windows.Credentials;
using AgentDesk.Platform.Windows.Sessions;
using AgentDesk.Platform.Windows.Settings;

namespace AgentDesk.App.Bridge;

public sealed partial class AgentDeskHostController :
    IAsyncDisposable,
    IAgentDeskMaintenanceHost,
    IAgentDeskCloudEngineHost
{
    private string BusyMessage => Message(
        "\u5df2\u6709\u4efb\u52a1\u6b63\u5728\u8fd0\u884c\u3002",
        "A task is already running.");
    private string ProviderSettingsErrorMessage => Message(
        "\u65e0\u6cd5\u8bfb\u53d6\u6216\u4fdd\u5b58\u6a21\u578b\u670d\u52a1\u8bbe\u7f6e\u3002",
        "The model provider settings could not be read or saved.");
    private string InsecureProviderMessage => Message(
        "API \u7aef\u70b9\u672a\u4f7f\u7528 HTTPS\uff0c\u8bf7\u5148\u660e\u786e\u5141\u8bb8\u660e\u6587 HTTP \u4f20\u8f93\u3002",
        "The API endpoint does not use HTTPS. Explicitly allow plain HTTP first.");
    private string EngineErrorMessage => Message(
        "\u5f15\u64ce\u64cd\u4f5c\u5931\u8d25\u3002",
        "The engine operation failed.");
    private string EngineConnectionLostMessage => Message(
        "\u5f15\u64ce\u8fde\u63a5\u5df2\u4e2d\u65ad\uff0c\u4e0b\u6b21\u8bf7\u6c42\u5c06\u81ea\u52a8\u91cd\u542f\u3002",
        "The engine connection was lost and will restart on the next request.");
    private string EngineStartingMessage => Message(
        "\u6b63\u5728\u542f\u52a8\u672c\u5730\u5f15\u64ce\u3002",
        "Starting the local engine.");
    private string RuntimeDashboardErrorMessage => Message(
        "\u65e0\u6cd5\u8bfb\u53d6\u6216\u66f4\u65b0\u8fd0\u884c\u4e2d\u7684\u4efb\u52a1\u3002",
        "The running tasks could not be read or updated.");
    private string RuntimeCommandsErrorMessage => Message(
        "\u65e0\u6cd5\u8bfb\u53d6\u8fd0\u884c\u65f6\u547d\u4ee4\u3002",
        "Runtime commands could not be loaded.");
    private string WorktreeErrorMessage => Message(
        "\u65e0\u6cd5\u5b8c\u6210\u5de5\u4f5c\u6811\u64cd\u4f5c\u3002",
        "The worktree operation could not be completed.");
    private string MemoryFlushErrorMessage => Message(
        "\u65e0\u6cd5\u7acb\u5373\u5237\u65b0\u4f1a\u8bdd\u8bb0\u5fc6\u3002",
        "Session memory could not be refreshed now.");
    private string ImagePromptsUnavailableMessage => Message(
        "\u5f53\u524d\u5f15\u64ce\u4e0d\u652f\u6301\u56fe\u7247\u63d0\u793a\u3002",
        "The current engine does not support image prompts.");
    private string NativeRiskAcknowledgementRequiredMessage => Message(
        "\u8bf7\u5148\u786e\u8ba4\u672c\u673a\u975e\u6c99\u7bb1\u6267\u884c\u98ce\u9669\u3002",
        "Confirm the risk of unsandboxed native execution first.");
    private string WslStrictUnavailableMessage => Message(
        "\u5f53\u524d\u7248\u672c\u4e2d WSL2 \u4e25\u683c\u6a21\u5f0f\u4e0d\u53ef\u7528\u3002",
        "WSL2 strict mode is unavailable in this build.");
    private string CloudPolicyExecutionDeniedMessage => Message(
        "当前团队策略不允许使用所选执行配置。",
        "The current team policy does not allow the selected execution profile.");
    private string WorkspaceChangedMessage => Message(
        "\u5de5\u4f5c\u533a\u5df2\u66f4\u65b0\uff0c\u8bf7\u91cd\u65b0\u786e\u8ba4\u540e\u518d\u6267\u884c\u3002",
        "The workspace changed. Confirm the request again before running it.");
    private string WorkspaceRequiredMessage => Message(
        "\u8bf7\u5148\u9009\u62e9\u5de5\u4f5c\u533a\u3002",
        "Select a workspace first.");

    private readonly AgentDeskHostOptions _options;
    private readonly ICredentialStore _credentialStore;
    private readonly IAgentDeskSidecarHostFactory _sidecarHostFactory;
    private readonly IProviderSettingsStore _providerSettingsStore;
    private readonly ISessionIndexStore _sessionIndexStore;
    private readonly IUiPreferencesStore _uiPreferencesStore;
    private readonly ICrashRecoveryStore _crashRecoveryStore;
    private readonly IUserNotificationService _notificationService;
    private readonly ExtensionApprovalHandler? _extensionApprovalHandler;
    private readonly AgentDeskCloudPolicyGate _cloudPolicyGate;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly object _eventGate = new();
    private readonly Dictionary<long, CancellationTokenSource> _runtimeOperations = [];
    private readonly HashSet<string> _runtimeRefreshSessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _extensionRequestIds = new(StringComparer.Ordinal);

    private string? _workspacePath;
    private int _workspaceGeneration;
    private bool _isCurrentWorkspaceTrusted;
    private IAgentDeskSidecarHost? _host;
    private EventHandler<SidecarExitedEventArgs>? _hostExitedHandler;
    private IEngineClient? _client;
    private EventHandler<EngineEvent>? _clientEventHandler;
    private SessionId? _sessionId;
    private ExecutionProfile? _executionProfile;
    private string? _engineProviderIdentity;
    private SessionMode? _confirmedSessionMode;
    private CancellationTokenSource? _promptCancellation;
    private int _engineGeneration;
    private long _engineEventEpoch;
    private long _nextRuntimeOperationId;
    private long? _worktreeOperationId;
    private long? _activeMutationOperationId;
    private int _activePromptGeneration;
    private int _crashedGeneration;
    private CrashRecoveryTarget? _crashRecoveryTarget;
    private string _status = "idle";
    private string? _statusMessage;
    private string? _statusSessionId;
    private bool _promptInProgress;
    private long _maintenanceSequence;
    private long? _activeMaintenanceLeaseId;
    private long _cloudSequence;
    private long? _activeCloudLeaseId;
    private bool _restartEngineBeforeNextPrompt;
    private ProviderProfile? _providerProfile;
    private bool _providerSettingsLoaded;
    private UiPreferences _uiPreferences = UiPreferences.Default;
    private bool _uiPreferencesLoaded;
    private bool _crashRecoveryLoaded;
    private TaskCompletionSource? _disposeCompletion;
    private volatile bool _disposing;
    private volatile bool _disposed;

    public AgentDeskHostController(
        AgentDeskHostOptions options,
        IUserNotificationService? notificationService = null,
        ExtensionApprovalHandler? extensionApprovalHandler = null)
        : this(
            options,
            new WindowsCredentialStore(),
            new AgentDeskSidecarHostFactory(),
            new JsonProviderSettingsStore(),
            new SqliteSessionIndexStore(),
            new JsonUiPreferencesStore(),
            notificationService,
            extensionApprovalHandler,
            new JsonCrashRecoveryStore())
    {
    }

    public AgentDeskHostController(
        AgentDeskHostOptions options,
        ICredentialStore credentialStore,
        IAgentDeskSidecarHostFactory sidecarHostFactory)
        : this(
            options,
            credentialStore,
            sidecarHostFactory,
            new EmptyProviderSettingsStore(),
            new EmptySessionIndexStore(),
            new InMemoryUiPreferencesStore())
    {
    }

    public AgentDeskHostController(
        AgentDeskHostOptions options,
        ICredentialStore credentialStore,
        IAgentDeskSidecarHostFactory sidecarHostFactory,
        IProviderSettingsStore providerSettingsStore)
        : this(
            options,
            credentialStore,
            sidecarHostFactory,
            providerSettingsStore,
            new SqliteSessionIndexStore(),
            new InMemoryUiPreferencesStore())
    {
    }

    public AgentDeskHostController(
        AgentDeskHostOptions options,
        ICredentialStore credentialStore,
        IAgentDeskSidecarHostFactory sidecarHostFactory,
        IProviderSettingsStore providerSettingsStore,
        ISessionIndexStore sessionIndexStore)
        : this(
            options,
            credentialStore,
            sidecarHostFactory,
            providerSettingsStore,
            sessionIndexStore,
            new InMemoryUiPreferencesStore())
    {
    }

    public AgentDeskHostController(
        AgentDeskHostOptions options,
        ICredentialStore credentialStore,
        IAgentDeskSidecarHostFactory sidecarHostFactory,
        IProviderSettingsStore providerSettingsStore,
        ISessionIndexStore sessionIndexStore,
        IUiPreferencesStore uiPreferencesStore,
        IUserNotificationService? notificationService = null,
        ExtensionApprovalHandler? extensionApprovalHandler = null,
        ICrashRecoveryStore? crashRecoveryStore = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(sidecarHostFactory);
        ArgumentNullException.ThrowIfNull(providerSettingsStore);
        ArgumentNullException.ThrowIfNull(sessionIndexStore);
        ArgumentNullException.ThrowIfNull(uiPreferencesStore);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CredentialName);
        if (options.AcpHandshakeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.AcpHandshakeTimeout,
                "The ACP handshake timeout must be positive.");
        }
        if (options.RuntimeOperationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.RuntimeOperationTimeout,
                "The runtime operation timeout must be positive.");
        }

        _options = options;
        _workspaceContextService = options.WorkspaceContextService ?? new WorkspaceContextService();
        _credentialStore = credentialStore;
        _sidecarHostFactory = sidecarHostFactory;
        _providerSettingsStore = providerSettingsStore;
        _sessionIndexStore = sessionIndexStore;
        _uiPreferencesStore = uiPreferencesStore;
        _crashRecoveryStore = crashRecoveryStore ?? new EmptyCrashRecoveryStore();
        _notificationService = notificationService ?? new NullUserNotificationService();
        _extensionApprovalHandler = extensionApprovalHandler;
        _cloudPolicyGate = options.CloudPolicyGate ?? new AgentDeskCloudPolicyGate();
        _workspacePath = options.WorkspacePath;
        _workspaceGeneration = _workspacePath is null ? 0 : 1;
        _isCurrentWorkspaceTrusted = _workspacePath is not null && options.IsTrustedWorkspace;
        _statusMessage = _workspacePath is null ? WorkspaceRequiredMessage : null;
    }

    public event EventHandler<WebEvent>? EventProduced;

    public async Task<bool> IsWindowsAutomationEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureUiPreferencesLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var teamPolicyActive = _cloudPolicyGate.Mode is not AgentDeskCloudPolicyMode.LocalOnly;
            return WindowsAutomationPolicy.Evaluate(
                _uiPreferences.WindowsAutomationEnabled,
                teamPolicyActive,
                _cloudPolicyGate.AllowsWindowsAutomation(localEnabled: true)).IsEnabled;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<bool> OpenIndexedSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!AgentDeskUserNotification.IsValidSessionId(sessionId))
        {
            throw new ArgumentException("The notification session ID is invalid.", nameof(sessionId));
        }

        var indexedSession = await _sessionIndexStore
            .FindByIdAsync(new SessionId(sessionId), cancellationToken)
            .ConfigureAwait(false);
        if (indexedSession is null)
        {
            return false;
        }

        ExecutionProfile executionProfile;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            try
            {
                await EnsureUiPreferencesLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                _uiPreferences = UiPreferences.Default;
                _uiPreferencesLoaded = true;
            }
            executionProfile = _executionProfile ?? _uiPreferences.ExecutionProfile;
        }
        finally
        {
            _stateGate.Release();
        }

        await HandleSessionOpenAsync(
                new SessionOpenWebCommand(
                    sessionId,
                    indexedSession.WorkspacePath,
                    executionProfile),
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdateWorkspaceAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        WorkspaceSelectedWebEvent? workspaceEvent = null;
        EngineStatusWebEvent? statusEvent = null;
        CancellationTokenSource? workspaceFileSearchCancellation = null;
        HashSet<CancellationTokenSource> runtimeOperationCancellations = [];
        var updated = false;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureCrashRecoveryLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (_promptInProgress ||
                _activeMaintenanceLeaseId is not null ||
                _activeCloudLeaseId is not null)
            {
                statusEvent = new EngineStatusWebEvent(
                    "running",
                    BusyMessage,
                    _sessionId?.Value,
                    EngineEpoch: Volatile.Read(ref _engineEventEpoch));
            }
            else if (string.Equals(_workspacePath, workspacePath, StringComparison.OrdinalIgnoreCase))
            {
                _isCurrentWorkspaceTrusted = false;
                _workspaceGeneration = checked(_workspaceGeneration + 1);
                InvalidateMemoryConfirmationsUnsafe();
                workspaceEvent = new WorkspaceSelectedWebEvent(
                    workspacePath,
                    _workspaceGeneration);
                workspaceFileSearchCancellation = DetachWorkspaceFileSearchUnsafe();
                updated = true;
            }
            else
            {
                var recoveryTarget = CreateCrashRecoveryTargetUnsafe() ?? _crashRecoveryTarget;
                var preserveRecovery = recoveryTarget is not null &&
                    string.Equals(
                        recoveryTarget.WorkspacePath,
                        workspacePath,
                        StringComparison.OrdinalIgnoreCase);
                if (_host is not null)
                {
                    var previousHost = _host;
                    DetachEngineUnsafe(runtimeOperationCancellations);
                    try
                    {
                        await StopAndDisposeHostAsync(previousHost).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        RestoreCleanupHostUnsafe(previousHost);
                        statusEvent = SetStatusUnsafe("error", EngineErrorMessage, null);
                    }
                }

                if (statusEvent is null)
                {
                    if (!preserveRecovery)
                    {
                        try
                        {
                            await _crashRecoveryStore
                                .ClearAsync(cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            _crashRecoveryTarget = recoveryTarget;
                            throw;
                        }
                    }
                    _crashRecoveryTarget = preserveRecovery ? recoveryTarget : null;
                    _workspacePath = workspacePath;
                    _isCurrentWorkspaceTrusted = false;
                    _workspaceGeneration = checked(_workspaceGeneration + 1);
                    InvalidateMemoryConfirmationsUnsafe();
                    workspaceEvent = new WorkspaceSelectedWebEvent(
                        workspacePath,
                        _workspaceGeneration);
                    workspaceFileSearchCancellation = DetachWorkspaceFileSearchUnsafe();
                    statusEvent = SetStatusUnsafe("idle", null, null);
                    updated = true;
                }
            }
        }
        finally
        {
            _stateGate.Release();
        }

        TryCancel(workspaceFileSearchCancellation);
        TryCancel(runtimeOperationCancellations);

        if (workspaceEvent is not null)
        {
            Publish(workspaceEvent);
        }

        if (statusEvent is not null)
        {
            Publish(statusEvent);
        }

        return updated;
    }

    public async Task<IAgentDeskMaintenanceLease> BeginMaintenanceAsync(
        CancellationToken cancellationToken = default)
    {
        HashSet<CancellationTokenSource> runtimeOperationCancellations = [];
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_promptInProgress ||
                _activeMutationOperationId is not null ||
                _activeMaintenanceLeaseId is not null ||
                _activeCloudLeaseId is not null)
            {
                throw new InvalidOperationException(BusyMessage);
            }

            var leaseId = checked(++_maintenanceSequence);
            _activeMaintenanceLeaseId = leaseId;
            CollectRuntimeOperationCancellationsUnsafe(runtimeOperationCancellations);
            return new MaintenanceLease(
                this,
                leaseId,
                _workspacePath ?? string.Empty);
        }
        finally
        {
            _stateGate.Release();
            TryCancel(runtimeOperationCancellations);
        }
    }

    public async Task<IAgentDeskCloudEngineLease> BeginCloudEngineOperationAsync(
        CancellationToken cancellationToken = default)
    {
        HashSet<CancellationTokenSource> runtimeOperationCancellations = [];
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_promptInProgress ||
                _activeMutationOperationId is not null ||
                _activeMaintenanceLeaseId is not null ||
                _activeCloudLeaseId is not null)
            {
                throw new InvalidOperationException(BusyMessage);
            }

            var workspacePath = _workspacePath ??
                throw new InvalidOperationException(WorkspaceRequiredMessage);
            await EnsureProviderSettingsLoadedUnsafeAsync(cancellationToken)
                .ConfigureAwait(false);
            await EnsureUiPreferencesLoadedUnsafeAsync(cancellationToken)
                .ConfigureAwait(false);
            if (_providerProfile is { CanSendCredentials: false })
            {
                throw new InvalidOperationException(InsecureProviderMessage);
            }

            var leaseId = checked(++_cloudSequence);
            _activeCloudLeaseId = leaseId;
            try
            {
                var profile = _executionProfile ?? _uiPreferences.ExecutionProfile;
                var context = await EnsureEngineAsync(
                        profile,
                        runtimeOperationCancellations,
                        cancellationToken)
                    .ConfigureAwait(false);
                CollectRuntimeOperationCancellationsUnsafe(runtimeOperationCancellations);
                return new CloudEngineLease(
                    this,
                    leaseId,
                    context.Client,
                    workspacePath,
                    string.IsNullOrWhiteSpace(_host?.EngineWorkspacePath)
                        ? workspacePath
                        : _host.EngineWorkspacePath!);
            }
            catch
            {
                _activeCloudLeaseId = null;
                throw;
            }
        }
        finally
        {
            _stateGate.Release();
            TryCancel(runtimeOperationCancellations);
        }
    }

    private async Task ActivateCloudSessionAsync(
        long leaseId,
        IEngineClient engine,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        SessionActiveChangedWebEvent? activeEvent = null;
        EngineStatusWebEvent? statusEvent = null;
        HashSet<CancellationTokenSource> runtimeOperationCancellations = [];
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_activeCloudLeaseId != leaseId || !ReferenceEquals(_client, engine))
            {
                throw new InvalidOperationException("The cloud engine lease is no longer active.");
            }

            var workspacePath = _workspacePath ??
                throw new InvalidOperationException(WorkspaceRequiredMessage);
            _ = await PrepareCrashRecoveryReplacementUnsafeAsync().ConfigureAwait(false);
            CollectRuntimeOperationCancellationsUnsafe(runtimeOperationCancellations);
            var engineEpoch = ActivateEngineSessionUnsafe(engine, sessionId);
            _confirmedSessionMode = SessionMode.Default;
            await TrySaveCrashRecoveryMarkerUnsafeAsync(
                    SessionMode.Default,
                    CrashRecoveryWriteKind.Replace)
                .ConfigureAwait(false);
            activeEvent = new SessionActiveChangedWebEvent(
                sessionId.Value,
                workspacePath,
                engineEpoch);
            statusEvent = SetStatusUnsafe("ready", null, sessionId.Value);
        }
        finally
        {
            _stateGate.Release();
            TryCancel(runtimeOperationCancellations);
        }

        Publish(activeEvent);
        Publish(statusEvent);
    }

    private async ValueTask ReleaseCloudEngineOperationAsync(long leaseId)
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeCloudLeaseId == leaseId)
            {
                _activeCloudLeaseId = null;
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task<EngineSessionDocument> ExportMaintenanceSessionAsync(
        long leaseId,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureMaintenanceLeaseUnsafe(leaseId);
            var client = _client ??
                throw new InvalidOperationException("The engine is not connected.");
            return await client
                .ExportSessionAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task<SessionId> ImportMaintenanceSessionAsync(
        long leaseId,
        EngineSessionDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        SessionActiveChangedWebEvent? activeEvent = null;
        EngineStatusWebEvent? statusEvent = null;
        SessionId imported;
        HashSet<CancellationTokenSource> runtimeOperationCancellations = [];

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureMaintenanceLeaseUnsafe(leaseId);
            var workspacePath = _workspacePath ??
                throw new InvalidOperationException(WorkspaceRequiredMessage);
            await EnsureProviderSettingsLoadedUnsafeAsync(cancellationToken)
                .ConfigureAwait(false);
            await EnsureUiPreferencesLoadedUnsafeAsync(cancellationToken)
                .ConfigureAwait(false);
            if (_providerProfile is { CanSendCredentials: false })
            {
                throw new InvalidOperationException(InsecureProviderMessage);
            }

            var profile = _executionProfile ?? _uiPreferences.ExecutionProfile;
            var client = (await EnsureEngineAsync(
                        profile,
                        runtimeOperationCancellations,
                        cancellationToken)
                    .ConfigureAwait(false))
                .Client;
            var engineWorkspacePath = string.IsNullOrWhiteSpace(_host?.EngineWorkspacePath)
                ? workspacePath
                : _host.EngineWorkspacePath;
            var previousRecovery = await PrepareCrashRecoveryReplacementUnsafeAsync()
                .ConfigureAwait(false);
            try
            {
                imported = await client
                    .ImportSessionAsync(document, engineWorkspacePath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                await RestoreCrashRecoveryReplacementUnsafeAsync(previousRecovery)
                    .ConfigureAwait(false);
                throw;
            }
            CollectRuntimeOperationCancellationsUnsafe(runtimeOperationCancellations);
            var engineEpoch = ActivateEngineSessionUnsafe(client, imported);
            _confirmedSessionMode = SessionMode.Default;
            await TrySaveCrashRecoveryMarkerUnsafeAsync(
                    SessionMode.Default,
                    CrashRecoveryWriteKind.Replace)
                .ConfigureAwait(false);
            activeEvent = new SessionActiveChangedWebEvent(
                imported.Value,
                workspacePath,
                engineEpoch);
            statusEvent = SetStatusUnsafe("ready", null, imported.Value);
        }
        finally
        {
            _stateGate.Release();
            TryCancel(runtimeOperationCancellations);
        }

        if (activeEvent is not null)
        {
            Publish(activeEvent);
        }
        if (statusEvent is not null)
        {
            Publish(statusEvent);
        }
        return imported;
    }

    private async Task StopMaintenanceEngineAsync(
        long leaseId,
        CancellationToken cancellationToken)
    {
        EngineStatusWebEvent? statusEvent = null;
        HashSet<CancellationTokenSource> runtimeOperationCancellations = [];
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureMaintenanceLeaseUnsafe(leaseId);
            if (_host is null)
            {
                return;
            }

            var host = _host;
            DetachEngineUnsafe(runtimeOperationCancellations);
            try
            {
                await StopAndDisposeHostAsync(host).ConfigureAwait(false);
                statusEvent = SetStatusUnsafe("idle", null, null);
            }
            catch
            {
                RestoreCleanupHostUnsafe(host);
                throw;
            }
        }
        finally
        {
            _stateGate.Release();
            TryCancel(runtimeOperationCancellations);
        }

        if (statusEvent is not null)
        {
            Publish(statusEvent);
        }
    }

    private async ValueTask ReleaseMaintenanceAsync(long leaseId)
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeMaintenanceLeaseId == leaseId)
            {
                _activeMaintenanceLeaseId = null;
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private void EnsureMaintenanceLeaseUnsafe(long leaseId)
    {
        if (_activeMaintenanceLeaseId != leaseId)
        {
            throw new InvalidOperationException("The maintenance lease is no longer active.");
        }
    }

    public Task HandleAsync(WebCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command switch
        {
            UiReadyWebCommand => HandleUiReadyAsync(cancellationToken),
            SaveUiPreferencesWebCommand value => HandleSaveUiPreferencesAsync(
                value,
                cancellationToken),
            SaveProviderWebCommand value => SaveProviderAsync(value, null, cancellationToken),
            SessionListWebCommand value => HandleSessionListAsync(value, cancellationToken),
            SessionOpenWebCommand value => HandleSessionOpenAsync(value, cancellationToken),
            SessionRenameWebCommand value => HandleSessionRenameAsync(value, cancellationToken),
            SessionArchiveWebCommand value => HandleSessionArchiveAsync(value, cancellationToken),
            SessionForkWebCommand value => HandleSessionForkAsync(value, cancellationToken),
            SessionCompactWebCommand value => HandleSessionCompactAsync(value, cancellationToken),
            SessionRewindPointsWebCommand value => HandleSessionRewindPointsAsync(
                value,
                cancellationToken),
            SessionRewindWebCommand value => HandleSessionRewindAsync(value, cancellationToken),
            RuntimeDashboardRefreshWebCommand value => HandleRuntimeDashboardRefreshAsync(
                value,
                cancellationToken),
            RuntimeTaskKillWebCommand value => HandleRuntimeTaskKillAsync(value, cancellationToken),
            RuntimeSubagentGetWebCommand value => HandleRuntimeSubagentGetAsync(
                value,
                cancellationToken),
            RuntimeSubagentCancelWebCommand value => HandleRuntimeSubagentCancelAsync(
                value,
                cancellationToken),
            WorkspaceInstructionsListWebCommand value =>
                HandleWorkspaceInstructionsListAsync(value, cancellationToken),
            WorkspaceFileReadWebCommand value =>
                HandleWorkspaceFileReadAsync(value, cancellationToken),
            WorkspaceInstructionsWriteWebCommand value =>
                HandleWorkspaceInstructionsWriteAsync(value, cancellationToken),
            WorkspaceFileSearchWebCommand value =>
                HandleWorkspaceFileSearchAsync(value, cancellationToken),
            RuntimeCommandsListWebCommand value => HandleRuntimeCommandsListAsync(
                value,
                cancellationToken),
            MemoryFlushWebCommand value => HandleMemoryFlushAsync(value, cancellationToken),
            MemoryListWebCommand value => HandleMemoryListAsync(value, cancellationToken),
            MemoryReadWebCommand value => HandleMemoryReadAsync(value, cancellationToken),
            MemoryWriteWebCommand value => HandleMemoryWriteAsync(value, cancellationToken),
            MemoryDeleteWebCommand value => HandleMemoryDeleteAsync(value, cancellationToken),
            ExtensionsListWebCommand value => HandleExtensionsListAsync(value, cancellationToken),
            ExtensionsActionWebCommand value => HandleExtensionsActionAsync(value, cancellationToken),
            WorktreeCreateWebCommand value => HandleWorktreeCreateAsync(value, cancellationToken),
            WorktreeListWebCommand value => HandleWorktreeListAsync(value, cancellationToken),
            WorktreeShowWebCommand value => HandleWorktreeShowAsync(value, cancellationToken),
            WorktreeApplyWebCommand value => HandleWorktreeApplyAsync(value, cancellationToken),
            WorktreeRemoveWebCommand value => HandleWorktreeRemoveAsync(value, cancellationToken),
            WorktreeGcWebCommand value => HandleWorktreeGcAsync(value, cancellationToken),
            PromptWebCommand value => HandlePromptAsync(value, cancellationToken),
            CancelWebCommand value => HandleCancelAsync(value, cancellationToken),
            PermissionRespondWebCommand value => HandlePermissionResponseAsync(
                value,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
    }

    public async ValueTask DisposeAsync()
    {
        IAgentDeskSidecarHost? host = null;
        IEngineClient? client = null;
        SessionId? sessionId = null;
        CancellationTokenSource? promptCancellation = null;
        CancellationTokenSource? workspaceFileSearchCancellation = null;
        CancellationTokenSource[] runtimeOperationCancellations = [];
        var promptInProgress = false;
        TaskCompletionSource? disposeCompletion = null;

        while (disposeCompletion is null)
        {
            Task? pendingDispose = null;
            await _stateGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }
                if (_disposing)
                {
                    pendingDispose = _disposeCompletion?.Task ?? Task.CompletedTask;
                }
                else
                {
                    _disposing = true;
                    disposeCompletion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _disposeCompletion = disposeCompletion;
                    _activeMaintenanceLeaseId = null;
                    _activeCloudLeaseId = null;
                    host = _host;
                    client = _client;
                    sessionId = _sessionId;
                    promptCancellation = _promptCancellation;
                    workspaceFileSearchCancellation = DetachWorkspaceFileSearchUnsafe();
                    runtimeOperationCancellations = SnapshotRuntimeOperationCancellationsUnsafe();
                    promptInProgress = _promptInProgress;
                    DetachEngineUnsafe(snapshotRuntimeOperations: false);
                }
            }
            finally
            {
                _stateGate.Release();
            }

            if (pendingDispose is not null)
            {
                await pendingDispose.ConfigureAwait(false);
            }
        }

        TryCancel(promptCancellation);
        TryCancel(workspaceFileSearchCancellation);
        foreach (var runtimeOperationCancellation in runtimeOperationCancellations)
        {
            TryCancel(runtimeOperationCancellation);
        }
        var hostCleanedUp = false;
        try
        {
            if (client is not null && sessionId is not null && promptInProgress)
            {
                using var cancelTimeout = new CancellationTokenSource(
                    _options.AcpHandshakeTimeout);
                try
                {
                    await client
                        .CancelAsync(sessionId, cancelTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            if (host is not null)
            {
                await StopAndDisposeHostAsync(host).ConfigureAwait(false);
            }
            hostCleanedUp = true;
            await _crashRecoveryStore.ClearAsync().ConfigureAwait(false);
        }
        catch
        {
            await _stateGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!hostCleanedUp && host is not null)
                {
                    RestoreCleanupHostUnsafe(host);
                }
                _disposing = false;
                _disposeCompletion = null;
            }
            finally
            {
                _stateGate.Release();
                disposeCompletion.TrySetResult();
            }
            throw;
        }

        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _disposing = false;
            _disposeCompletion = null;
        }
        finally
        {
            _stateGate.Release();
            disposeCompletion.TrySetResult();
        }
    }

    private async Task HandleUiReadyAsync(CancellationToken cancellationToken)
    {
        WorkspaceSelectedWebEvent? workspaceEvent;
        EngineStatusWebEvent statusEvent;
        ProviderStatusWebEvent? providerEvent = null;
        UiPreferencesChangedWebEvent preferencesEvent;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureCrashRecoveryLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureUiPreferencesLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _uiPreferences = UiPreferences.Default;
                _uiPreferencesLoaded = true;
            }
            _uiPreferences = ApplyPolicyToPreferences(
                _uiPreferences.ApplyHostCapabilities(_options.IsWslStrictAvailable));
            preferencesEvent = new UiPreferencesChangedWebEvent(_uiPreferences);
            try
            {
                await EnsureProviderSettingsLoadedUnsafeAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (_providerProfile is not null)
                {
                    providerEvent = new ProviderStatusWebEvent(
                        "loaded",
                        _providerProfile,
                        HasCredential: _credentialStore.Read(
                            _providerProfile.CredentialName) is not null);
                }
            }
            catch (Exception)
            {
                providerEvent = new ProviderStatusWebEvent(
                    "error",
                    string.Empty,
                    string.Empty,
                    "chat_completions",
                    AllowInsecureTransport: false,
                    HasCredential: false,
                    Message: ProviderSettingsErrorMessage);
            }
            workspaceEvent = _workspacePath is null
                ? null
                : new WorkspaceSelectedWebEvent(_workspacePath, _workspaceGeneration);
            ExecutionProfile[] executionProfiles = _options.IsWslStrictAvailable
                ? [ExecutionProfile.NativeProtected, ExecutionProfile.WslStrict]
                : [ExecutionProfile.NativeProtected];
            statusEvent = new EngineStatusWebEvent(
                _status,
                _statusMessage,
                _statusSessionId,
                executionProfiles,
                _options.IsWslStrictAvailable ? null : WslStrictUnavailableMessage,
                EngineEpoch: Volatile.Read(ref _engineEventEpoch));
        }
        finally
        {
            _stateGate.Release();
        }

        Publish(preferencesEvent);
        if (workspaceEvent is not null)
        {
            Publish(workspaceEvent);
        }

        Publish(statusEvent);
        if (providerEvent is not null)
        {
            Publish(providerEvent);
        }
    }

    private async Task HandleSaveUiPreferencesAsync(
        SaveUiPreferencesWebCommand command,
        CancellationToken cancellationToken)
    {
        UiPreferencesChangedWebEvent result;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureUiPreferencesLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var normalized = ApplyPolicyToPreferences(command.Preferences
                .Validate()
                .ApplyHostCapabilities(_options.IsWslStrictAvailable));
            var restartRequired = !string.Equals(
                _uiPreferences.Language,
                normalized.Language,
                StringComparison.Ordinal);
            await _uiPreferencesStore.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
            _uiPreferences = normalized;
            if (_workspacePath is null && _status == "idle")
            {
                _statusMessage = WorkspaceRequiredMessage;
            }
            result = new UiPreferencesChangedWebEvent(normalized, restartRequired);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            result = new UiPreferencesChangedWebEvent(_uiPreferences);
        }
        finally
        {
            _stateGate.Release();
        }
        Publish(result);
    }

    public async Task SaveProviderAsync(
        SaveProviderWebCommand command,
        string? replacementCredential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ProviderStatusWebEvent result;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureProviderSettingsLoadedUnsafeAsync(cancellationToken)
                .ConfigureAwait(false);
            var previousProfile = _providerProfile;
            var previousCredential = TryReadCredential(command.Profile.CredentialName);
            var credentialChanged = false;
            if (!previousCredential.Succeeded)
            {
                result = ProviderSaveError(
                    previousProfile,
                    command.Profile,
                    previousCredential);
            }
            else if (command.UseExistingCredential == command.ReplaceCredential ||
                (command.UseExistingCredential && replacementCredential is not null) ||
                (command.UseExistingCredential && previousCredential.Value is null) ||
                (command.ReplaceCredential && !IsValidProviderCredential(replacementCredential)))
            {
                result = ProviderSaveError(
                    previousProfile,
                    command.Profile,
                    previousCredential);
            }
            else
            {
                try
                {
                    if (command.ReplaceCredential)
                    {
                        credentialChanged = true;
                        _credentialStore.Save(
                            command.Profile.CredentialName,
                            replacementCredential!);
                    }

                    await _providerSettingsStore
                        .SaveAsync(command.Profile, cancellationToken)
                        .ConfigureAwait(false);
                    _providerProfile = command.Profile;
                    _providerSettingsLoaded = true;
                    _restartEngineBeforeNextPrompt = true;
                    result = new ProviderStatusWebEvent(
                        "saved",
                        command.Profile,
                        HasCredential: true);
                }
                catch (Exception)
                {
                    if (credentialChanged)
                    {
                        RestoreCredential(
                            command.Profile.CredentialName,
                            previousCredential.Value);
                    }

                    result = ProviderSaveError(
                        previousProfile,
                        command.Profile,
                        previousCredential);
                }
            }
        }
        finally
        {
            _stateGate.Release();
        }

        Publish(result);
    }

    private static bool IsValidProviderCredential(string? credential) =>
        credential is { Length: > 0 and <= 8 * 1024 } && !credential.Any(char.IsWhiteSpace);

    private ProviderStatusWebEvent ProviderSaveError(
        ProviderProfile? retainedProfile,
        ProviderProfile attemptedProfile,
        CredentialReadResult attemptedCredential)
    {
        var retainedCredential = retainedProfile is null
            ? CredentialReadResult.Missing
            : string.Equals(
                retainedProfile.CredentialName,
                attemptedProfile.CredentialName,
                StringComparison.Ordinal)
                ? attemptedCredential
                : TryReadCredential(retainedProfile.CredentialName);
        return new(
            "error",
            retainedProfile?.BaseUrl ?? string.Empty,
            retainedProfile?.Model ?? string.Empty,
            retainedProfile is null
                ? ProviderBackendName(attemptedProfile.Backend)
                : ProviderBackendName(retainedProfile.Backend),
            retainedProfile?.AllowInsecureTransport ?? false,
            HasCredential: retainedProfile is not null &&
                retainedCredential is { Succeeded: true, Value: not null },
            Message: ProviderSettingsErrorMessage);
    }

    private CredentialReadResult TryReadCredential(string name)
    {
        try
        {
            return new CredentialReadResult(true, _credentialStore.Read(name));
        }
        catch (Exception)
        {
            return CredentialReadResult.Failed;
        }
    }

    private void RestoreCredential(string name, string? previousCredential)
    {
        try
        {
            if (previousCredential is null)
            {
                _credentialStore.Delete(name);
            }
            else
            {
                _credentialStore.Save(name, previousCredential);
            }
        }
        catch (Exception)
        {
            // The provider error remains visible; never replace it with a secret-store exception.
        }
    }

    private readonly record struct CredentialReadResult(bool Succeeded, string? Value)
    {
        public static CredentialReadResult Failed { get; } = new(false, null);

        public static CredentialReadResult Missing { get; } = new(true, null);
    }

    private static string ProviderBackendName(ProviderBackend backend) => backend switch
    {
        ProviderBackend.ChatCompletions => "chat_completions",
        ProviderBackend.Responses => "responses",
        _ => "chat_completions",
    };

    private async Task HandleSessionListAsync(
        SessionListWebCommand command,
        CancellationToken cancellationToken)
    {
        SessionListChangedWebEvent? listEvent = null;
        SessionListErrorWebEvent? errorEvent = null;
        HashSet<CancellationTokenSource> runtimeOperationCancellations = [];

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            try
            {
                if (_workspacePath is null)
                {
                    listEvent = new SessionListChangedWebEvent(
                        [],
                        null,
                        command.RequestId);
                }
                else if (command.Archived)
                {
                    var offset = ParseLocalSessionCursor(command.Cursor);
                    var sessions = await _sessionIndexStore
                        .SearchAsync(
                            _workspacePath,
                            command.Query,
                            archived: true,
                            command.Limit,
                            offset,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var nextCursor = sessions.Count == command.Limit
                        ? $"local:{offset + sessions.Count}"
                        : null;
                    listEvent = new SessionListChangedWebEvent(
                        sessions,
                        nextCursor,
                        command.RequestId);
                }
                else
                {
                    await EnsureProviderSettingsLoadedUnsafeAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (_providerProfile is { CanSendCredentials: false })
                    {
                        throw new InvalidOperationException(InsecureProviderMessage);
                    }

                    var profile = _executionProfile ?? ExecutionProfile.NativeProtected;
                    var context = await EnsureEngineAsync(
                            profile,
                            runtimeOperationCancellations,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var page = await context.Client
                        .ListSessionsAsync(
                            _workspacePath,
                            command.Query,
                            command.Cursor,
                            command.Limit,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await _sessionIndexStore
                        .UpsertAsync(page.Sessions, cancellationToken)
                        .ConfigureAwait(false);
                    var archivedIds = await _sessionIndexStore
                        .GetArchivedIdsAsync(
                            page.Sessions.Select(session => session.SessionId).ToArray(),
                            cancellationToken)
                        .ConfigureAwait(false);
                    listEvent = new SessionListChangedWebEvent(
                        page.Sessions
                            .Where(session => !archivedIds.Contains(session.SessionId.Value))
                            .ToArray(),
                        page.NextCursor,
                        command.RequestId);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                errorEvent = new SessionListErrorWebEvent(
                    command.RequestId,
                    EngineErrorMessage);
            }
        }
        finally
        {
            _stateGate.Release();
            TryCancel(runtimeOperationCancellations);
        }

        if (listEvent is not null)
        {
            Publish(listEvent);
        }
        if (errorEvent is not null)
        {
            Publish(errorEvent);
        }
    }

    private async Task HandleSessionArchiveAsync(
        SessionArchiveWebCommand command,
        CancellationToken cancellationToken)
    {
        SessionArchiveChangedWebEvent? archiveEvent = null;
        SessionOperationErrorWebEvent? errorEvent = null;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            try
            {
                var updated = await _sessionIndexStore
                    .SetArchivedAsync(
                        new SessionId(command.SessionId),
                        command.Archived,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!updated)
                {
                    throw new InvalidOperationException("The session is not present in the local index.");
                }
                archiveEvent = new SessionArchiveChangedWebEvent(
                    command.RequestId,
                    command.SessionId,
                    command.Archived);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                errorEvent = new SessionOperationErrorWebEvent(
                    command.RequestId,
                    "archive",
                    command.SessionId,
                    EngineErrorMessage);
            }
        }
        finally
        {
            _stateGate.Release();
        }

        if (archiveEvent is not null)
        {
            Publish(archiveEvent);
        }
        if (errorEvent is not null)
        {
            Publish(errorEvent);
        }
    }

    private async Task HandleSessionForkAsync(
        SessionForkWebCommand command,
        CancellationToken cancellationToken)
    {
        SessionForkedWebEvent? forkedEvent = null;
        EngineStatusWebEvent? statusEvent = null;
        HashSet<CancellationTokenSource> runtimeOperationCancellations = [];

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            try
            {
                if (_promptInProgress ||
                    _activeMutationOperationId is not null ||
                    _activeMaintenanceLeaseId is not null ||
                    _activeCloudLeaseId is not null)
                {
                    throw new InvalidOperationException(BusyMessage);
                }
                await EnsureProviderSettingsLoadedUnsafeAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (_workspacePath is null)
                {
                    throw new InvalidOperationException(WorkspaceRequiredMessage);
                }
                if (_providerProfile is { CanSendCredentials: false })
                {
                    throw new InvalidOperationException(InsecureProviderMessage);
                }

                var profile = _executionProfile ?? ExecutionProfile.NativeProtected;
                var context = await EnsureEngineAsync(
                        profile,
                        runtimeOperationCancellations,
                        cancellationToken)
                    .ConfigureAwait(false);
                Publish(SetStatusUnsafe("running", null, _sessionId?.Value));
                var isWorktree = !string.Equals(
                    command.SourceWorkspacePath,
                    command.TargetWorkspacePath,
                    StringComparison.OrdinalIgnoreCase);
                var result = await context.Client
                    .ForkSessionAsync(
                        new SessionId(command.SessionId),
                        command.SourceWorkspacePath,
                        command.TargetWorkspacePath,
                        command.TargetPromptIndex,
                        sessionKind: isWorktree ? "worktree" : "fork",
                        sourceWorkspacePath: isWorktree ? command.SourceWorkspacePath : null,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                forkedEvent = new SessionForkedWebEvent(result);
                statusEvent = SetStatusUnsafe("ready", null, _sessionId?.Value);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                statusEvent = SetStatusUnsafe("error", EngineErrorMessage, _sessionId?.Value);
            }
        }
        finally
        {
            _stateGate.Release();
            TryCancel(runtimeOperationCancellations);
        }

        if (forkedEvent is not null)
        {
            Publish(forkedEvent);
        }
        if (statusEvent is not null)
        {
            Publish(statusEvent);
        }
    }

    private async Task HandleSessionCompactAsync(
        SessionCompactWebCommand command,
        CancellationToken cancellationToken)
    {
        SessionCompactedWebEvent? compactedEvent = null;
        EngineStatusWebEvent? statusEvent = null;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            try
            {
                var (client, sessionId) = RequireIdleActiveSessionUnsafe(command.SessionId);
                Publish(SetStatusUnsafe("running", null, sessionId.Value));
                await client
                    .CompactSessionAsync(sessionId, command.UserContext, cancellationToken)
                    .ConfigureAwait(false);
                compactedEvent = new SessionCompactedWebEvent(sessionId.Value);
                statusEvent = SetStatusUnsafe("ready", null, sessionId.Value);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                statusEvent = SetStatusUnsafe("error", EngineErrorMessage, _sessionId?.Value);
            }
        }
        finally
        {
            _stateGate.Release();
        }

        if (compactedEvent is not null)
        {
            Publish(compactedEvent);
        }
        if (statusEvent is not null)
        {
            Publish(statusEvent);
        }
    }

    private async Task HandleSessionRewindPointsAsync(
        SessionRewindPointsWebCommand command,
        CancellationToken cancellationToken)
    {
        SessionRewindPointsWebEvent? pointsEvent = null;
        EngineStatusWebEvent? errorEvent = null;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            try
            {
                var (client, sessionId) = RequireIdleActiveSessionUnsafe(command.SessionId);
                var points = await client
                    .GetRewindPointsAsync(sessionId, cancellationToken)
                    .ConfigureAwait(false);
                pointsEvent = new SessionRewindPointsWebEvent(sessionId.Value, points);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                errorEvent = SetStatusUnsafe("error", EngineErrorMessage, _sessionId?.Value);
            }
        }
        finally
        {
            _stateGate.Release();
        }

        if (pointsEvent is not null)
        {
            Publish(pointsEvent);
        }
        if (errorEvent is not null)
        {
            Publish(errorEvent);
        }
    }

    private async Task HandleSessionRewindAsync(
        SessionRewindWebCommand command,
        CancellationToken cancellationToken)
    {
        SessionRewoundWebEvent? rewoundEvent = null;
        EngineStatusWebEvent? statusEvent = null;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            try
            {
                var (client, sessionId) = RequireIdleActiveSessionUnsafe(command.SessionId);
                Publish(SetStatusUnsafe("running", null, sessionId.Value));
                var result = await client
                    .RewindSessionAsync(
                        sessionId,
                        command.TargetPromptIndex,
                        command.Mode,
                        command.Force,
                        cancellationToken)
                    .ConfigureAwait(false);
                rewoundEvent = new SessionRewoundWebEvent(sessionId.Value, result);
                statusEvent = SetStatusUnsafe(
                    result.Success ? "ready" : "error",
                    result.Success ? null : EngineErrorMessage,
                    sessionId.Value);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                statusEvent = SetStatusUnsafe("error", EngineErrorMessage, _sessionId?.Value);
            }
        }
        finally
        {
            _stateGate.Release();
        }

        if (rewoundEvent is not null)
        {
            Publish(rewoundEvent);
        }
        if (statusEvent is not null)
        {
            Publish(statusEvent);
        }
    }

    private (IEngineClient Client, SessionId SessionId) RequireIdleActiveSessionUnsafe(
        string requestedSessionId)
    {
        if (_promptInProgress || _activeMutationOperationId is not null)
        {
            throw new InvalidOperationException(BusyMessage);
        }
        if (_client is null ||
            _sessionId is null ||
            !string.Equals(_sessionId.Value, requestedSessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The requested session is not active.");
        }
        return (_client, _sessionId);
    }

    private async Task HandleRuntimeCommandsListAsync(
        RuntimeCommandsListWebCommand command,
        CancellationToken cancellationToken)
    {
        WorkspaceOperationContext? context = null;
        IReadOnlyList<RuntimeCommand>? commands = null;
        var failed = false;
        var callerCancelled = false;
        try
        {
            context = await BeginWorkspaceOperationAsync(
                command.WorkspaceGeneration,
                cancellationToken).ConfigureAwait(false);
            commands = await context.Client
                .ListRuntimeCommandsAsync(
                    context.WorkspacePath,
                    context.Cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            callerCancelled = true;
        }
        catch (Exception)
        {
            failed = true;
        }

        var isCurrent = context is not null
            ? await CompleteWorkspaceOperationAsync(context).ConfigureAwait(false)
            : await IsCurrentWorkspaceGenerationAsync(command.WorkspaceGeneration)
                .ConfigureAwait(false);
        if (callerCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (!isCurrent)
        {
            return;
        }
        Publish(failed
            ? new RuntimeCommandsErrorWebEvent(
                command.WorkspaceGeneration,
                RuntimeCommandsErrorMessage)
            : new RuntimeCommandsChangedWebEvent(command.WorkspaceGeneration, commands!));
    }

    private Task HandleWorktreeCreateAsync(
        WorktreeCreateWebCommand command,
        CancellationToken cancellationToken) => ExecuteWorktreeOperationAsync(
            command.WorkspaceGeneration,
            WorktreeOperation.Create,
            command.SessionId,
            requiresIdle: true,
            itemId: null,
            (context, operationCancellation) => context.Client.CreateWorktreeAsync(
                new WorktreeCreateRequest(
                    context.SessionId,
                    context.EngineWorkspacePath,
                    command.DestinationPath,
                    command.CopyMode,
                    command.GitReference,
                    command.CopyIgnoredInBackground,
                    command.IgnoredSkipPatterns,
                    command.CreationType,
                    command.Label),
                operationCancellation),
            result => new WorktreeCreatedWebEvent(command.WorkspaceGeneration, result),
            cancellationToken);

    private Task HandleWorktreeListAsync(
        WorktreeListWebCommand command,
        CancellationToken cancellationToken) => ExecuteWorktreeOperationAsync(
            command.WorkspaceGeneration,
            WorktreeOperation.List,
            requestedSessionId: null,
            requiresIdle: false,
            itemId: null,
            (context, operationCancellation) => context.Client.ListWorktreesAsync(
                new WorktreeListRequest(
                    context.EngineWorkspacePath,
                    command.Types,
                    command.IncludeAll),
                operationCancellation),
            result => new WorktreeListChangedWebEvent(command.WorkspaceGeneration, result),
            cancellationToken);

    private Task HandleWorktreeShowAsync(
        WorktreeShowWebCommand command,
        CancellationToken cancellationToken) => ExecuteWorktreeOperationAsync(
            command.WorkspaceGeneration,
            WorktreeOperation.Show,
            requestedSessionId: null,
            requiresIdle: false,
            command.IdOrPath,
            (context, operationCancellation) => context.Client.ShowWorktreeAsync(
                new WorktreeShowRequest(command.IdOrPath),
                operationCancellation),
            result => new WorktreeDetailWebEvent(command.WorkspaceGeneration, result),
            cancellationToken);

    private Task HandleWorktreeApplyAsync(
        WorktreeApplyWebCommand command,
        CancellationToken cancellationToken) => ExecuteWorktreeOperationAsync(
            command.WorkspaceGeneration,
            WorktreeOperation.Apply,
            command.SessionId,
            requiresIdle: true,
            command.WorktreePath,
            (context, operationCancellation) => context.Client.ApplyWorktreeAsync(
                new WorktreeApplyRequest(
                    context.SessionId,
                    command.WorktreePath,
                    command.Mode),
                operationCancellation),
            result => new WorktreeAppliedWebEvent(command.WorkspaceGeneration, result),
            cancellationToken);

    private Task HandleWorktreeRemoveAsync(
        WorktreeRemoveWebCommand command,
        CancellationToken cancellationToken) => ExecuteWorktreeOperationAsync(
            command.WorkspaceGeneration,
            WorktreeOperation.Remove,
            requestedSessionId: null,
            requiresIdle: true,
            command.IdOrPath,
            (context, operationCancellation) => context.Client.RemoveWorktreeAsync(
                new WorktreeRemoveRequest(command.IdOrPath, command.Force, command.DryRun),
                operationCancellation),
            result => new WorktreeRemovedWebEvent(
                command.WorkspaceGeneration,
                command.IdOrPath,
                result),
            cancellationToken);

    private Task HandleWorktreeGcAsync(
        WorktreeGcWebCommand command,
        CancellationToken cancellationToken) => ExecuteWorktreeOperationAsync(
            command.WorkspaceGeneration,
            WorktreeOperation.Gc,
            requestedSessionId: null,
            requiresIdle: true,
            itemId: null,
            (context, operationCancellation) => context.Client.GcWorktreesAsync(
                new WorktreeGcRequest(
                    command.DryRun,
                    WorktreeMaximumAge(command.MaximumAgeSeconds),
                    command.Force),
                operationCancellation),
            result => new WorktreeGcCompletedWebEvent(command.WorkspaceGeneration, result),
            cancellationToken);

    private async Task ExecuteWorktreeOperationAsync<TResult>(
        int workspaceGeneration,
        WorktreeOperation operation,
        string? requestedSessionId,
        bool requiresIdle,
        string? itemId,
        Func<WorkspaceOperationContext, CancellationToken, Task<TResult>> execute,
        Func<TResult, WebEvent> successEvent,
        CancellationToken cancellationToken)
    {
        WorkspaceOperationContext? context = null;
        TResult result = default!;
        var failed = false;
        var callerCancelled = false;
        try
        {
            context = await BeginWorkspaceOperationAsync(
                workspaceGeneration,
                cancellationToken,
                requestedSessionId,
                requiresIdle,
                exclusiveWorktree: true).ConfigureAwait(false);
            result = await execute(context, context.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            callerCancelled = true;
        }
        catch (Exception)
        {
            failed = true;
        }

        var isCurrent = context is not null
            ? await CompleteWorkspaceOperationAsync(context).ConfigureAwait(false)
            : await IsCurrentWorkspaceGenerationAsync(workspaceGeneration).ConfigureAwait(false);
        if (callerCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (!isCurrent)
        {
            return;
        }
        Publish(failed
            ? new WorktreeErrorWebEvent(
                workspaceGeneration,
                WorktreeErrorMessage,
                operation,
                itemId)
            : successEvent(result));
    }

    private static TimeSpan? WorktreeMaximumAge(long? maximumAgeSeconds)
    {
        const long maximumSeconds = 3_650L * 24L * 60L * 60L;
        if (maximumAgeSeconds is null)
        {
            return null;
        }
        if (maximumAgeSeconds is <= 0 or > maximumSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAgeSeconds));
        }
        return TimeSpan.FromSeconds(maximumAgeSeconds.Value);
    }

    private async Task<WorkspaceOperationContext> BeginWorkspaceOperationAsync(
        int requestedWorkspaceGeneration,
        CancellationToken cancellationToken,
        string? requestedSessionId = null,
        bool requiresIdle = false,
        bool exclusiveWorktree = false)
    {
        HashSet<CancellationTokenSource> runtimeOperationCancellations = [];
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_workspacePath is null || _workspaceGeneration != requestedWorkspaceGeneration)
            {
                throw new InvalidOperationException(WorkspaceChangedMessage);
            }
            await EnsureProviderSettingsLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            await EnsureUiPreferencesLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (_providerProfile is { CanSendCredentials: false })
            {
                throw new InvalidOperationException(InsecureProviderMessage);
            }
            if (requiresIdle &&
                (_promptInProgress ||
                 _activeMutationOperationId is not null ||
                 _activeMaintenanceLeaseId is not null ||
                 _activeCloudLeaseId is not null))
            {
                throw new InvalidOperationException(BusyMessage);
            }
            if (exclusiveWorktree && _worktreeOperationId is not null)
            {
                throw new InvalidOperationException(BusyMessage);
            }

            var profile = _executionProfile ?? _uiPreferences
                .ApplyHostCapabilities(_options.IsWslStrictAvailable)
                .ExecutionProfile;
            var promptContext = await EnsureEngineAsync(
                    profile,
                    runtimeOperationCancellations,
                    cancellationToken)
                .ConfigureAwait(false);
            if (requestedSessionId is not null &&
                !string.Equals(
                    promptContext.SessionId.Value,
                    requestedSessionId,
                    StringComparison.Ordinal))
            {
                throw new StaleWorkspaceOperationException();
            }
            var engineWorkspacePath = string.IsNullOrWhiteSpace(_host?.EngineWorkspacePath)
                ? _workspacePath
                : _host.EngineWorkspacePath;
            var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            operationCancellation.CancelAfter(_options.RuntimeOperationTimeout);
            var operationId = checked(++_nextRuntimeOperationId);
            _runtimeOperations.Add(operationId, operationCancellation);
            if (requiresIdle)
            {
                _activeMutationOperationId = operationId;
            }
            if (exclusiveWorktree)
            {
                _worktreeOperationId = operationId;
            }
            return new WorkspaceOperationContext(
                operationId,
                promptContext.Client,
                promptContext.SessionId,
                _workspacePath,
                engineWorkspacePath,
                _workspaceGeneration,
                promptContext.Generation,
                operationCancellation,
                requiresIdle,
                exclusiveWorktree);
        }
        finally
        {
            _stateGate.Release();
            TryCancel(runtimeOperationCancellations);
        }
    }

    private async Task<bool> CompleteWorkspaceOperationAsync(WorkspaceOperationContext context)
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _runtimeOperations.Remove(context.OperationId);
            if (context.ExclusiveMutation &&
                _activeMutationOperationId == context.OperationId)
            {
                _activeMutationOperationId = null;
            }
            if (context.ExclusiveWorktree && _worktreeOperationId == context.OperationId)
            {
                _worktreeOperationId = null;
            }
            return IsWorkspaceOperationCurrentUnsafe(context);
        }
        finally
        {
            _stateGate.Release();
            context.Cancellation.Dispose();
        }
    }

    private async Task<bool> IsCurrentWorkspaceGenerationAsync(int workspaceGeneration)
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return !_disposed &&
                _workspacePath is not null &&
                _workspaceGeneration == workspaceGeneration;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private bool IsWorkspaceOperationCurrentUnsafe(WorkspaceOperationContext context) =>
        !_disposed &&
        _workspaceGeneration == context.WorkspaceGeneration &&
        _engineGeneration == context.EngineGeneration &&
        ReferenceEquals(_client, context.Client) &&
        string.Equals(
            _sessionId?.Value,
            context.SessionId.Value,
            StringComparison.Ordinal) &&
        string.Equals(_workspacePath, context.WorkspacePath, StringComparison.OrdinalIgnoreCase);

    private async Task HandleMemoryFlushAsync(
        MemoryFlushWebCommand command,
        CancellationToken cancellationToken)
    {
        RuntimeOperationContext? context = null;
        var failed = false;
        var callerCancelled = false;
        try
        {
            context = await BeginRuntimeOperationAsync(
                command.SessionId,
                coalesceRefresh: false,
                cancellationToken,
                requiresIdle: true).ConfigureAwait(false);
            Publish(new MemoryFlushStatusWebEvent(command.SessionId, "running"));
            await context!.Client
                .FlushMemoryAsync(context.SessionId, context.Cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            callerCancelled = true;
        }
        catch (Exception)
        {
            failed = true;
        }

        var isCurrent = context is not null &&
            await CompleteRuntimeOperationAsync(context).ConfigureAwait(false);
        if (callerCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (!isCurrent)
        {
            return;
        }
        Publish(new MemoryFlushStatusWebEvent(
            command.SessionId,
            failed ? "error" : "succeeded",
            failed ? MemoryFlushErrorMessage : null));
    }

    private async Task HandleRuntimeDashboardRefreshAsync(
        RuntimeDashboardRefreshWebCommand command,
        CancellationToken cancellationToken)
    {
        RuntimeDashboardChangedWebEvent? dashboardEvent = null;
        var failed = false;
        var callerCancelled = false;
        RuntimeOperationContext? context;
        try
        {
            context = await BeginRuntimeOperationAsync(
                command.SessionId,
                coalesceRefresh: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            Publish(RuntimeDashboardError(
                command.SessionId,
                RuntimeDashboardOperation.Refresh));
            return;
        }
        if (context is null)
        {
            return;
        }

        try
        {
            dashboardEvent = await ReadRuntimeDashboardAsync(
                context.Client,
                context.SessionId,
                context.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            callerCancelled = true;
        }
        catch (Exception)
        {
            failed = true;
        }

        var isCurrent = await CompleteRuntimeOperationAsync(context).ConfigureAwait(false);
        if (callerCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (!isCurrent)
        {
            return;
        }
        Publish(failed
            ? RuntimeDashboardError(command.SessionId, RuntimeDashboardOperation.Refresh)
            : dashboardEvent!);
    }

    private async Task HandleRuntimeTaskKillAsync(
        RuntimeTaskKillWebCommand command,
        CancellationToken cancellationToken)
    {
        RuntimeTaskKilledWebEvent? killedEvent = null;
        RuntimeDashboardChangedWebEvent? dashboardEvent = null;
        var failed = false;
        var callerCancelled = false;
        RuntimeOperationContext context;
        try
        {
            context = (await BeginRuntimeOperationAsync(
                command.SessionId,
                coalesceRefresh: false,
                cancellationToken).ConfigureAwait(false))!;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            Publish(RuntimeDashboardError(
                command.SessionId,
                RuntimeDashboardOperation.TaskKill,
                command.TaskId));
            return;
        }

        try
        {
            var outcome = await context.Client
                .KillBackgroundTaskAsync(
                    context.SessionId,
                    command.TaskId,
                    context.Cancellation.Token)
                .ConfigureAwait(false);
            killedEvent = new RuntimeTaskKilledWebEvent(
                context.SessionId.Value,
                command.TaskId,
                outcome);
            dashboardEvent = await ReadRuntimeDashboardAsync(
                context.Client,
                context.SessionId,
                context.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            callerCancelled = true;
        }
        catch (Exception)
        {
            failed = true;
        }

        var isCurrent = await CompleteRuntimeOperationAsync(context).ConfigureAwait(false);
        if (callerCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (!isCurrent)
        {
            return;
        }
        if (killedEvent is not null)
        {
            Publish(killedEvent);
        }
        Publish(failed
            ? RuntimeDashboardError(
                command.SessionId,
                killedEvent is null
                    ? RuntimeDashboardOperation.TaskKill
                    : RuntimeDashboardOperation.Refresh,
                killedEvent is null ? command.TaskId : null)
            : dashboardEvent!);
    }

    private async Task HandleRuntimeSubagentGetAsync(
        RuntimeSubagentGetWebCommand command,
        CancellationToken cancellationToken)
    {
        RuntimeSubagentDetailWebEvent? detailEvent = null;
        var failed = false;
        var callerCancelled = false;
        RuntimeOperationContext context;
        try
        {
            context = (await BeginRuntimeOperationAsync(
                command.SessionId,
                coalesceRefresh: false,
                cancellationToken).ConfigureAwait(false))!;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            Publish(RuntimeDashboardError(
                command.SessionId,
                RuntimeDashboardOperation.SubagentGet,
                command.SubagentId));
            return;
        }

        try
        {
            var snapshot = await context.Client
                .GetSubagentAsync(
                    context.SessionId,
                    command.SubagentId,
                    cancellationToken: context.Cancellation.Token)
                .ConfigureAwait(false);
            detailEvent = new RuntimeSubagentDetailWebEvent(
                context.SessionId.Value,
                command.SubagentId,
                snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            callerCancelled = true;
        }
        catch (Exception)
        {
            failed = true;
        }

        var isCurrent = await CompleteRuntimeOperationAsync(context).ConfigureAwait(false);
        if (callerCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (!isCurrent)
        {
            return;
        }
        Publish(failed
            ? RuntimeDashboardError(
                command.SessionId,
                RuntimeDashboardOperation.SubagentGet,
                command.SubagentId)
            : detailEvent!);
    }

    private async Task HandleRuntimeSubagentCancelAsync(
        RuntimeSubagentCancelWebCommand command,
        CancellationToken cancellationToken)
    {
        RuntimeSubagentCancelledWebEvent? cancelledEvent = null;
        RuntimeDashboardChangedWebEvent? dashboardEvent = null;
        var failed = false;
        var callerCancelled = false;
        RuntimeOperationContext context;
        try
        {
            context = (await BeginRuntimeOperationAsync(
                command.SessionId,
                coalesceRefresh: false,
                cancellationToken).ConfigureAwait(false))!;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            Publish(RuntimeDashboardError(
                command.SessionId,
                RuntimeDashboardOperation.SubagentCancel,
                command.SubagentId));
            return;
        }

        try
        {
            var result = await context.Client
                .CancelSubagentAsync(
                    context.SessionId,
                    command.SubagentId,
                    context.Cancellation.Token)
                .ConfigureAwait(false);
            cancelledEvent = new RuntimeSubagentCancelledWebEvent(
                context.SessionId.Value,
                command.SubagentId,
                result);
            dashboardEvent = await ReadRuntimeDashboardAsync(
                context.Client,
                context.SessionId,
                context.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            callerCancelled = true;
        }
        catch (Exception)
        {
            failed = true;
        }

        var isCurrent = await CompleteRuntimeOperationAsync(context).ConfigureAwait(false);
        if (callerCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (!isCurrent)
        {
            return;
        }
        if (cancelledEvent is not null)
        {
            Publish(cancelledEvent);
        }
        Publish(failed
            ? RuntimeDashboardError(
                command.SessionId,
                cancelledEvent is null
                    ? RuntimeDashboardOperation.SubagentCancel
                    : RuntimeDashboardOperation.Refresh,
                cancelledEvent is null ? command.SubagentId : null)
            : dashboardEvent!);
    }

    private static async Task<RuntimeDashboardChangedWebEvent> ReadRuntimeDashboardAsync(
        IEngineClient client,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var tasksRequest = client.ListBackgroundTasksAsync(sessionId, cancellationToken);
        var subagentsRequest = client.ListRunningSubagentsAsync(sessionId, cancellationToken);
        await Task.WhenAll(tasksRequest, subagentsRequest).ConfigureAwait(false);
        var subagents = await subagentsRequest.ConfigureAwait(false);
        if (subagents.Any(subagent => !string.Equals(
                subagent.ParentSessionId,
                sessionId.Value,
                StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The runtime dashboard contained a subagent from another session.");
        }
        return new RuntimeDashboardChangedWebEvent(
            sessionId.Value,
            await tasksRequest.ConfigureAwait(false),
            subagents);
    }

    private async Task<RuntimeOperationContext?> BeginRuntimeOperationAsync(
        string requestedSessionId,
        bool coalesceRefresh,
        CancellationToken cancellationToken,
        bool requiresIdle = false)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (requiresIdle &&
                (_promptInProgress ||
                 _activeMutationOperationId is not null ||
                 _activeMaintenanceLeaseId is not null ||
                 _activeCloudLeaseId is not null))
            {
                throw new InvalidOperationException(BusyMessage);
            }
            var (client, sessionId) = RequireActiveSessionUnsafe(requestedSessionId);
            if (coalesceRefresh && !_runtimeRefreshSessions.Add(sessionId.Value))
            {
                return null;
            }

            var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            operationCancellation.CancelAfter(_options.RuntimeOperationTimeout);
            var operationId = checked(++_nextRuntimeOperationId);
            _runtimeOperations.Add(operationId, operationCancellation);
            if (requiresIdle)
            {
                _activeMutationOperationId = operationId;
            }
            return new RuntimeOperationContext(
                operationId,
                client,
                sessionId,
                _engineGeneration,
                operationCancellation,
                requiresIdle,
                coalesceRefresh);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task<bool> CompleteRuntimeOperationAsync(RuntimeOperationContext context)
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _runtimeOperations.Remove(context.OperationId);
            if (context.ExclusiveMutation &&
                _activeMutationOperationId == context.OperationId)
            {
                _activeMutationOperationId = null;
            }
            if (context.CoalescedRefresh)
            {
                _runtimeRefreshSessions.Remove(context.SessionId.Value);
            }
            return !_disposed &&
                _engineGeneration == context.EngineGeneration &&
                ReferenceEquals(_client, context.Client) &&
                string.Equals(_sessionId?.Value, context.SessionId.Value, StringComparison.Ordinal);
        }
        finally
        {
            _stateGate.Release();
            context.Cancellation.Dispose();
        }
    }

    private RuntimeDashboardErrorWebEvent RuntimeDashboardError(
        string sessionId,
        RuntimeDashboardOperation operation,
        string? itemId = null) =>
        new(sessionId, RuntimeDashboardErrorMessage, operation, itemId);

    private (IEngineClient Client, SessionId SessionId) RequireActiveSessionUnsafe(
        string requestedSessionId)
    {
        if (_client is null ||
            _sessionId is null ||
            !string.Equals(_sessionId.Value, requestedSessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The requested session is not active.");
        }
        return (_client, _sessionId);
    }

    private async Task HandleSessionOpenAsync(
        SessionOpenWebCommand command,
        CancellationToken cancellationToken)
    {
        if (!await UpdateWorkspaceAsync(command.WorkspacePath, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        EngineStatusWebEvent? errorEvent = null;
        HashSet<CancellationTokenSource> runtimeOperationCancellations = [];
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            try
            {
                await EnsureUiPreferencesLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _uiPreferences = UiPreferences.Default;
                _uiPreferencesLoaded = true;
            }
            try
            {
                await EnsureProviderSettingsLoadedUnsafeAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (_providerProfile is { CanSendCredentials: false })
                {
                    throw new InvalidOperationException(InsecureProviderMessage);
                }
                if (command.ExecutionProfile is ExecutionProfile.WslStrict &&
                    !_options.IsWslStrictAvailable)
                {
                    throw new NotSupportedException(WslStrictUnavailableMessage);
                }

                var targetSession = new SessionId(command.SessionId);
                var previousClient = _client;
                var previousSession = _sessionId;
                var previousMode = _confirmedSessionMode;
                var previousRecovery = await PrepareCrashRecoveryReplacementUnsafeAsync()
                    .ConfigureAwait(false);
                PromptContext context;
                try
                {
                    context = await EnsureEngineAsync(
                            command.ExecutionProfile,
                            runtimeOperationCancellations,
                            cancellationToken,
                            targetSession)
                        .ConfigureAwait(false);
                    if (!context.Client.Capabilities.LoadSession)
                    {
                        throw new NotSupportedException(
                            "The engine cannot restore saved sessions.");
                    }

                    CollectRuntimeOperationCancellationsUnsafe(runtimeOperationCancellations);
                    _confirmedSessionMode = null;
                    if (!string.Equals(
                            context.SessionId.Value,
                            targetSession.Value,
                            StringComparison.Ordinal))
                    {
                        var engineWorkspacePath = string.IsNullOrWhiteSpace(_host?.EngineWorkspacePath)
                            ? command.WorkspacePath
                            : _host.EngineWorkspacePath;
                        _ = await LoadAndActivateSessionAsync(
                                context.Client,
                                targetSession,
                                engineWorkspacePath,
                                engineEpoch => Publish(
                                    new SessionActiveChangedWebEvent(
                                        command.SessionId,
                                        command.WorkspacePath,
                                        engineEpoch)),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        _ = ActivateEngineSessionUnsafe(context.Client, targetSession);
                    }
                    _confirmedSessionMode ??= SessionMode.Default;
                }
                catch
                {
                    if (ReferenceEquals(_client, previousClient))
                    {
                        _confirmedSessionMode = previousMode;
                        if (previousSession is not null)
                        {
                            var engineEpoch = ActivateEngineSessionUnsafe(
                                previousClient!,
                                previousSession);
                            Publish(
                                new SessionActiveChangedWebEvent(
                                    previousSession.Value,
                                    command.WorkspacePath,
                                    engineEpoch));
                        }
                    }
                    await RestoreCrashRecoveryReplacementUnsafeAsync(previousRecovery)
                        .ConfigureAwait(false);
                    throw;
                }

                await TrySaveCrashRecoveryMarkerUnsafeAsync(
                        _confirmedSessionMode.Value,
                        CrashRecoveryWriteKind.Replace)
                    .ConfigureAwait(false);
                Publish(new EngineCapabilitiesChangedWebEvent(
                    command.SessionId,
                    context.Client.Capabilities.ImagePrompts,
                    context.Client.Capabilities.SessionModes));
                Publish(new MemoryCapabilitiesWebEvent(
                    command.SessionId,
                    context.Client.Capabilities.Memory));
                Publish(
                    new SessionModeChangedWebEvent(
                        command.SessionId,
                        _confirmedSessionMode.Value,
                        context.Client.Capabilities.Supports(SessionMode.Plan)));
                Publish(SetStatusUnsafe("ready", null, command.SessionId));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                errorEvent = SetStatusUnsafe("error", EngineErrorMessage, _sessionId?.Value);
            }
        }
        finally
        {
            _stateGate.Release();
            TryCancel(runtimeOperationCancellations);
        }

        if (errorEvent is not null)
        {
            Publish(errorEvent);
        }
    }

    private async Task HandleSessionRenameAsync(
        SessionRenameWebCommand command,
        CancellationToken cancellationToken)
    {
        SessionListChangedWebEvent? listEvent = null;
        SessionRenamedWebEvent? renamedEvent = null;
        SessionOperationErrorWebEvent? errorEvent = null;
        HashSet<CancellationTokenSource> runtimeOperationCancellations = [];

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            try
            {
                await EnsureProviderSettingsLoadedUnsafeAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (_workspacePath is null)
                {
                    throw new InvalidOperationException(WorkspaceRequiredMessage);
                }
                if (_providerProfile is { CanSendCredentials: false })
                {
                    throw new InvalidOperationException(InsecureProviderMessage);
                }

                var profile = _executionProfile ?? ExecutionProfile.NativeProtected;
                var context = await EnsureEngineAsync(
                        profile,
                        runtimeOperationCancellations,
                        cancellationToken)
                    .ConfigureAwait(false);
                await context.Client
                    .RenameSessionAsync(
                        new SessionId(command.SessionId),
                        command.Title,
                        command.WorkspacePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                var refreshed = await context.Client
                    .ListSessionsAsync(_workspacePath, null, null, 100, cancellationToken)
                    .ConfigureAwait(false);
                await _sessionIndexStore
                    .UpsertAsync(refreshed.Sessions, cancellationToken)
                    .ConfigureAwait(false);
                var archivedIds = await _sessionIndexStore
                    .GetArchivedIdsAsync(
                        refreshed.Sessions.Select(session => session.SessionId).ToArray(),
                        cancellationToken)
                    .ConfigureAwait(false);
                listEvent = new SessionListChangedWebEvent(
                    refreshed.Sessions
                        .Where(session => !archivedIds.Contains(session.SessionId.Value))
                        .ToArray(),
                    refreshed.NextCursor);
                renamedEvent = new SessionRenamedWebEvent(
                    command.RequestId,
                    command.SessionId,
                    command.Title);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                errorEvent = new SessionOperationErrorWebEvent(
                    command.RequestId,
                    "rename",
                    command.SessionId,
                    EngineErrorMessage);
            }
        }
        finally
        {
            _stateGate.Release();
            TryCancel(runtimeOperationCancellations);
        }

        if (renamedEvent is not null)
        {
            Publish(renamedEvent);
        }
        if (listEvent is not null)
        {
            Publish(listEvent);
        }
        if (errorEvent is not null)
        {
            Publish(errorEvent);
        }
    }

    private async Task HandlePromptAsync(
        PromptWebCommand command,
        CancellationToken cancellationToken)
    {
        PromptContext? context = null;
        CancellationTokenSource? promptCancellation = null;
        EngineStatusWebEvent? rejectedEvent = null;
        EngineStatusWebEvent? errorEvent = null;
        HashSet<CancellationTokenSource> runtimeOperationCancellations = [];

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            try
            {
                await EnsureUiPreferencesLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _uiPreferences = UiPreferences.Default;
                _uiPreferencesLoaded = true;
            }
            try
            {
                await EnsureProviderSettingsLoadedUnsafeAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                errorEvent = SetStatusUnsafe("error", ProviderSettingsErrorMessage, null);
            }

            if (errorEvent is null)
            {
                if (_promptInProgress ||
                    _activeMutationOperationId is not null ||
                    _activeMaintenanceLeaseId is not null ||
                    _activeCloudLeaseId is not null)
                {
                    rejectedEvent = new EngineStatusWebEvent(
                        "running",
                        BusyMessage,
                        _sessionId?.Value,
                        EngineEpoch: Volatile.Read(ref _engineEventEpoch));
                }
                else if (_workspacePath is null)
                {
                    errorEvent = SetStatusUnsafe("error", WorkspaceRequiredMessage, null);
                }
                else if (command.WorkspaceGeneration != _workspaceGeneration)
                {
                    errorEvent = SetStatusUnsafe("error", WorkspaceChangedMessage, null);
                }
                else if (_providerProfile is { CanSendCredentials: false })
                {
                    errorEvent = SetStatusUnsafe("error", InsecureProviderMessage, null);
                }
                else if (!_cloudPolicyGate.AllowsExecutionProfile(command.ExecutionProfile))
                {
                    errorEvent = SetStatusUnsafe(
                        "error",
                        CloudPolicyExecutionDeniedMessage,
                        null);
                }
                else if (!ExecutionProfilePolicy.CanExecute(
                             command.ExecutionProfile,
                             _isCurrentWorkspaceTrusted,
                             _options.IsWslStrictAvailable,
                             command.NativeRiskAcknowledged))
                {
                    var message = command.ExecutionProfile is ExecutionProfile.NativeProtected
                        ? NativeRiskAcknowledgementRequiredMessage
                        : WslStrictUnavailableMessage;
                    errorEvent = SetStatusUnsafe("error", message, null);
                }
                else
                {
                    _promptInProgress = true;
                    try
                    {
                        context = await EnsureEngineAsync(
                                command.ExecutionProfile,
                                runtimeOperationCancellations,
                                cancellationToken)
                            .ConfigureAwait(false);
                        await ConfirmSessionModeAsync(
                                context,
                                command.SessionMode,
                                cancellationToken)
                            .ConfigureAwait(false);
                        _ = PromptAttachmentPolicy.Validate(command.Attachments);
                        if (command.Attachments.Count > 0 &&
                            !context.Client.Capabilities.ImagePrompts)
                        {
                            throw new NotSupportedException(ImagePromptsUnavailableMessage);
                        }
                        promptCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken);
                        _promptCancellation = promptCancellation;
                        _activePromptGeneration = context.Generation;
                        errorEvent = SetStatusUnsafe("running", null, context.SessionId.Value);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        _promptInProgress = false;
                        _activePromptGeneration = 0;
                        _ = SetStatusUnsafe("idle", null, null);
                        throw;
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        _promptInProgress = false;
                        _activePromptGeneration = 0;
                        errorEvent = SetStatusUnsafe("error", EngineErrorMessage, null);
                    }
                }
            }
        }
        finally
        {
            _stateGate.Release();
            TryCancel(runtimeOperationCancellations);
        }

        if (rejectedEvent is not null)
        {
            Publish(rejectedEvent);
            return;
        }

        if (errorEvent is not null)
        {
            Publish(errorEvent);
        }

        if (context is null || promptCancellation is null)
        {
            return;
        }

        await RunPromptAsync(
            context,
            command.Text,
            command.Attachments,
            promptCancellation).ConfigureAwait(false);
    }

    private async Task<PromptContext> EnsureEngineAsync(
        ExecutionProfile executionProfile,
        ICollection<CancellationTokenSource> runtimeOperationCancellations,
        CancellationToken cancellationToken,
        SessionId? sessionToLoad = null)
    {
        await EnsureCrashRecoveryLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
        await EnsureProviderSettingsLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
        if (sessionToLoad is null && _crashRecoveryTarget is { } persistedRecovery)
        {
            var currentProviderIdentity = _providerProfile is null
                ? null
                : CrashRecoveryProviderIdentity.Create(_providerProfile);
            if (persistedRecovery.ExecutionProfile != executionProfile ||
                (persistedRecovery.IsPersistent &&
                    persistedRecovery.ProviderIdentity is null) ||
                (persistedRecovery.ProviderIdentity is not null &&
                    (currentProviderIdentity is null ||
                     !CrashRecoveryProviderIdentity.FixedTimeEquals(
                         currentProviderIdentity,
                         persistedRecovery.ProviderIdentity))))
            {
                await DiscardCrashRecoveryTargetUnsafeAsync().ConfigureAwait(false);
            }
        }
        if (!_cloudPolicyGate.AllowsExecutionProfile(executionProfile))
        {
            throw new InvalidOperationException(CloudPolicyExecutionDeniedMessage);
        }
        if (!_restartEngineBeforeNextPrompt &&
            _client is not null &&
            _sessionId is not null &&
            _executionProfile == executionProfile)
        {
            return new PromptContext(_client, _sessionId, _engineGeneration);
        }

        var ownsRecoveryReplacement = sessionToLoad is null && _crashRecoveryTarget is null;
        var previousRecovery = ownsRecoveryReplacement
            ? await PrepareCrashRecoveryReplacementUnsafeAsync().ConfigureAwait(false)
            : null;

        if (_host is not null)
        {
            var previousHost = _host;
            DetachEngineUnsafe(runtimeOperationCancellations);
            try
            {
                await StopAndDisposeHostAsync(previousHost).ConfigureAwait(false);
            }
            catch
            {
                RestoreCleanupHostUnsafe(previousHost);
                if (ownsRecoveryReplacement)
                {
                    await RestoreCrashRecoveryReplacementUnsafeAsync(previousRecovery)
                        .ConfigureAwait(false);
                }
                throw;
            }
        }

        IAgentDeskSidecarHost host;
        try
        {
            Publish(SetStatusUnsafe("starting", EngineStartingMessage, null));
            host = _sidecarHostFactory.Create();
        }
        catch
        {
            if (ownsRecoveryReplacement)
            {
                await RestoreCrashRecoveryReplacementUnsafeAsync(previousRecovery)
                    .ConfigureAwait(false);
            }
            throw;
        }
        var generation = ++_engineGeneration;
        _host = host;
        _executionProfile = executionProfile;
        _engineProviderIdentity = _providerProfile is null
            ? null
            : CrashRecoveryProviderIdentity.Create(_providerProfile);
        EventHandler<SidecarExitedEventArgs> exitedHandler = (_, args) =>
            OnSidecarExited(host, args);
        _hostExitedHandler = exitedHandler;
        host.Exited += exitedHandler;

        var replacementCommitted = false;
        try
        {
            var workspacePath = _workspacePath ?? throw new InvalidOperationException(
                WorkspaceRequiredMessage);
            var client = await StartHostAsync(
                    host,
                    workspacePath,
                    executionProfile,
                    cancellationToken)
                .ConfigureAwait(false);
            _client = client;
            client.PermissionRequested += OnPermissionRequested;
            client.Faulted += OnEngineFaulted;
            using var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            handshakeCancellation.CancelAfter(_options.AcpHandshakeTimeout);
            SessionId sessionId;
            var recoveryTarget = sessionToLoad is null
                ? _crashRecoveryTarget
                : new CrashRecoveryTarget(
                    sessionToLoad,
                    workspacePath,
                    executionProfile,
                    SessionMode.Default,
                    _providerProfile is null
                        ? null
                        : CrashRecoveryProviderIdentity.Create(_providerProfile),
                    IsPersistent: false);
            try
            {
                var capabilities = await client
                    .InitializeAsync(handshakeCancellation.Token)
                    .ConfigureAwait(false);
                if (executionProfile is ExecutionProfile.WslStrict &&
                    (!capabilities.AgentDeskHealth || !capabilities.StrictSandboxActive))
                {
                    throw new InvalidOperationException(
                        "The WSL engine did not attest an active strict sandbox.");
                }
                await client
                    .AuthenticateAsync(handshakeCancellation.Token)
                    .ConfigureAwait(false);
                var engineWorkspacePath = string.IsNullOrWhiteSpace(host.EngineWorkspacePath)
                    ? workspacePath
                    : host.EngineWorkspacePath;
                if (recoveryTarget is not null)
                {
                    if (!string.Equals(
                            recoveryTarget.WorkspacePath,
                            workspacePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "The crashed session belongs to a different workspace.");
                    }
                    if (!client.Capabilities.LoadSession)
                    {
                        throw new NotSupportedException(
                            "The engine cannot restore the session that was active before the crash.");
                    }
                    try
                    {
                        _ = await LoadAndActivateSessionAsync(
                                client,
                                recoveryTarget.SessionId,
                                engineWorkspacePath,
                                engineEpoch => Publish(
                                    new SessionActiveChangedWebEvent(
                                        recoveryTarget.SessionId.Value,
                                        workspacePath,
                                        engineEpoch)),
                                handshakeCancellation.Token)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        await DiscardCrashRecoveryTargetUnsafeAsync().ConfigureAwait(false);
                        throw;
                    }
                    sessionId = recoveryTarget.SessionId;
                }
                else
                {
                    sessionId = await client
                        .NewSessionAsync(engineWorkspacePath, handshakeCancellation.Token)
                        .ConfigureAwait(false);
                    replacementCommitted = true;
                }
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested &&
                      handshakeCancellation.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The ACP startup handshake did not complete within {_options.AcpHandshakeTimeout}.",
                    exception);
            }
            if (recoveryTarget is null)
            {
                _ = ActivateEngineSessionUnsafe(client, sessionId);
            }
            _confirmedSessionMode = recoveryTarget is null ? SessionMode.Default : null;
            _crashRecoveryTarget = null;
            await TrySaveCrashRecoveryMarkerUnsafeAsync(
                    recoveryTarget?.SessionMode ?? SessionMode.Default,
                    recoveryTarget is null
                        ? CrashRecoveryWriteKind.Replace
                        : CrashRecoveryWriteKind.Update)
                .ConfigureAwait(false);
            _restartEngineBeforeNextPrompt = false;
            _crashedGeneration = 0;
            Publish(new EngineCapabilitiesChangedWebEvent(
                sessionId.Value,
                client.Capabilities.ImagePrompts,
                client.Capabilities.SessionModes));
            Publish(new MemoryCapabilitiesWebEvent(
                sessionId.Value,
                client.Capabilities.Memory));
            Publish(
                new SessionModeChangedWebEvent(
                    sessionId.Value,
                    SessionMode.Default,
                    client.Capabilities.Supports(SessionMode.Plan)));
            Publish(SetStatusUnsafe("ready", null, sessionId.Value));
            return new PromptContext(client, sessionId, generation);
        }
        catch
        {
            host.Exited -= exitedHandler;
            if (_client is not null)
            {
                if (_clientEventHandler is not null)
                {
                    _client.EventReceived -= _clientEventHandler;
                    _clientEventHandler = null;
                }
                _client.PermissionRequested -= OnPermissionRequested;
                _client.Faulted -= OnEngineFaulted;
            }

            if (ReferenceEquals(_host, host))
            {
                _host = null;
                _client = null;
                _sessionId = null;
                _executionProfile = null;
                _engineProviderIdentity = null;
                _confirmedSessionMode = null;
                _hostExitedHandler = null;
            }

            try
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                RestoreCleanupHostUnsafe(host);
            }
            if (ownsRecoveryReplacement && !replacementCommitted)
            {
                await RestoreCrashRecoveryReplacementUnsafeAsync(previousRecovery)
                    .ConfigureAwait(false);
            }
            throw;
        }
    }

    private UiPreferences ApplyPolicyToPreferences(UiPreferences preferences) =>
        _cloudPolicyGate.AllowsWindowsAutomation(preferences.WindowsAutomationEnabled)
            ? preferences
            : preferences with { WindowsAutomationEnabled = false };

    private async Task ConfirmSessionModeAsync(
        PromptContext context,
        SessionMode requestedMode,
        CancellationToken cancellationToken)
    {
        if (!context.Client.Capabilities.Supports(requestedMode))
        {
            throw new NotSupportedException("The selected session mode is unavailable.");
        }

        if (_confirmedSessionMode == requestedMode)
        {
            return;
        }

        using var confirmationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        confirmationCancellation.CancelAfter(_options.AcpHandshakeTimeout);
        await context.Client
            .SetSessionModeAsync(
                context.SessionId,
                requestedMode,
                confirmationCancellation.Token)
            .ConfigureAwait(false);
        _confirmedSessionMode = requestedMode;
        await TrySaveCrashRecoveryMarkerUnsafeAsync(
                requestedMode,
                CrashRecoveryWriteKind.Update)
            .ConfigureAwait(false);
        Publish(
            new SessionModeChangedWebEvent(
                context.SessionId.Value,
                requestedMode,
                context.Client.Capabilities.Supports(SessionMode.Plan)));
    }

    private async Task RunPromptAsync(
        PromptContext context,
        string text,
        IReadOnlyList<PromptAttachment> attachments,
        CancellationTokenSource promptCancellation)
    {
        PromptCompletedWebEvent? completedEvent = null;
        EngineStatusWebEvent? statusEvent = null;
        AgentDeskNotificationKind? notificationKind = null;

        try
        {
            var result = attachments.Count == 0
                ? await context.Client
                    .PromptAsync(context.SessionId, text, promptCancellation.Token)
                    .ConfigureAwait(false)
                : await context.Client
                    .PromptWithAttachmentsAsync(
                        context.SessionId,
                        text,
                        attachments,
                        promptCancellation.Token)
                    .ConfigureAwait(false);
            completedEvent = new PromptCompletedWebEvent(
                context.SessionId.Value,
                result.RawStopReason);
            notificationKind = AgentDeskNotificationKind.TaskCompleted;
        }
        catch (OperationCanceledException) when (promptCancellation.IsCancellationRequested)
        {
            completedEvent = new PromptCompletedWebEvent(context.SessionId.Value, "cancelled");
        }
        catch (Exception)
        {
            statusEvent = new EngineStatusWebEvent(
                "error",
                EngineErrorMessage,
                context.SessionId.Value);
            notificationKind = AgentDeskNotificationKind.TaskFailed;
        }

        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var canPublish = !_disposed &&
                _crashedGeneration != context.Generation &&
                _engineGeneration == context.Generation &&
                ReferenceEquals(_client, context.Client) &&
                string.Equals(_sessionId?.Value, context.SessionId.Value, StringComparison.Ordinal);
            if (_activePromptGeneration == context.Generation &&
                ReferenceEquals(_promptCancellation, promptCancellation))
            {
                _promptInProgress = false;
                _activePromptGeneration = 0;
                _promptCancellation = null;
            }

            promptCancellation.Dispose();

            if (!canPublish)
            {
                completedEvent = null;
                statusEvent = null;
                notificationKind = null;
            }
            else if (statusEvent is null)
            {
                statusEvent = SetStatusUnsafe("ready", null, context.SessionId.Value);
            }
            else
            {
                statusEvent = SetStatusUnsafe(
                    statusEvent.Status,
                    statusEvent.Message,
                    statusEvent.SessionId);
            }
        }
        finally
        {
            _stateGate.Release();
        }

        if (completedEvent is not null)
        {
            Publish(completedEvent);
        }

        if (statusEvent is not null)
        {
            Publish(statusEvent);
        }

        if (notificationKind is not null)
        {
            await TryShowNotificationAsync(context.SessionId.Value, notificationKind.Value)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleCancelAsync(
        CancelWebCommand command,
        CancellationToken cancellationToken)
    {
        IEngineClient? client = null;
        SessionId? sessionId = null;
        CancellationTokenSource? promptCancellation = null;
        long engineEpoch = 0;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_promptInProgress &&
                _client is not null &&
                _sessionId is not null &&
                string.Equals(_sessionId.Value, command.SessionId, StringComparison.Ordinal))
            {
                client = _client;
                sessionId = _sessionId;
                promptCancellation = _promptCancellation;
                engineEpoch = Volatile.Read(ref _engineEventEpoch);
            }
        }
        finally
        {
            _stateGate.Release();
        }

        if (client is null || sessionId is null)
        {
            return;
        }

        TryCancel(promptCancellation);
        try
        {
            await client.CancelAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            Publish(new EngineStatusWebEvent(
                "error",
                EngineErrorMessage,
                sessionId.Value,
                EngineEpoch: engineEpoch));
        }
    }

    private async Task HandlePermissionResponseAsync(
        PermissionRespondWebCommand command,
        CancellationToken cancellationToken)
    {
        IEngineClient? client;
        string? sessionId;
        long engineEpoch;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            client = _client;
            sessionId = _sessionId?.Value;
            engineEpoch = Volatile.Read(ref _engineEventEpoch);
        }
        finally
        {
            _stateGate.Release();
        }

        if (client is null)
        {
            return;
        }

        try
        {
            _ = await client.RespondToPermissionAsync(
                    command.RequestId,
                    command.Decision,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            Publish(new EngineStatusWebEvent(
                "error",
                EngineErrorMessage,
                sessionId,
                EngineEpoch: engineEpoch));
        }
    }

    private long ActivateEngineSessionUnsafe(IEngineClient client, SessionId sessionId)
    {
        lock (_eventGate)
        {
            if (ReferenceEquals(_client, client) &&
                _clientEventHandler is not null &&
                string.Equals(_sessionId?.Value, sessionId.Value, StringComparison.Ordinal))
            {
                return Volatile.Read(ref _engineEventEpoch);
            }

            if (_clientEventHandler is not null && _client is not null)
            {
                _client.EventReceived -= _clientEventHandler;
            }

            _sessionId = sessionId;
            var engineEpoch = Interlocked.Increment(ref _engineEventEpoch);
            EventHandler<EngineEvent> eventHandler = (sender, engineEvent) =>
                OnEngineEventReceived(engineEpoch, sender, engineEvent);
            _clientEventHandler = eventHandler;
            client.EventReceived += eventHandler;
            return engineEpoch;
        }
    }

    private async Task<long> LoadAndActivateSessionAsync(
        IEngineClient client,
        SessionId sessionId,
        string workingDirectory,
        Action<long> onActivated,
        CancellationToken cancellationToken)
    {
        var replayGate = new object();
        var replay = new List<EngineEvent>();
        var active = false;
        var abandoned = false;
        long engineEpoch = 0;
        void BufferOrForward(object? sender, EngineEvent engineEvent)
        {
            lock (replayGate)
            {
                if (abandoned ||
                    !ReferenceEquals(sender, client) ||
                    !string.Equals(
                        engineEvent.SessionId.Value,
                        sessionId.Value,
                        StringComparison.Ordinal))
                {
                    return;
                }
                if (!active)
                {
                    replay.Add(engineEvent);
                    return;
                }
            }

            OnEngineEventReceived(engineEpoch, sender, engineEvent);
        }
        EventHandler<EngineEvent> eventHandler = BufferOrForward;
        client.EventReceived += eventHandler;

        try
        {
            await client
                .LoadSessionAsync(sessionId, workingDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lock (replayGate)
            {
                abandoned = true;
                replay.Clear();
            }
            client.EventReceived -= eventHandler;
            throw;
        }

        lock (replayGate)
        {
            lock (_eventGate)
            {
                if (_clientEventHandler is not null && _client is not null)
                {
                    _client.EventReceived -= _clientEventHandler;
                }

                _sessionId = sessionId;
                engineEpoch = Interlocked.Increment(ref _engineEventEpoch);
                _clientEventHandler = eventHandler;
            }

            onActivated(engineEpoch);
            foreach (var engineEvent in replay)
            {
                OnEngineEventReceived(engineEpoch, client, engineEvent);
            }
            replay.Clear();
            active = true;
        }
        return engineEpoch;
    }

    private void OnEngineEventReceived(
        long engineEpoch,
        object? sender,
        EngineEvent engineEvent)
    {
        if (_disposed ||
            engineEpoch != Volatile.Read(ref _engineEventEpoch) ||
            !ReferenceEquals(sender, _client) ||
            !string.Equals(engineEvent.SessionId.Value, _sessionId?.Value, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(
                engineEvent.UpdateKind,
                "current_mode_update",
                StringComparison.Ordinal))
        {
            if (TryReadSessionMode(engineEvent.Update, out var mode) &&
                sender is IEngineClient sourceClient)
            {
                _ = ApplyEngineModeUpdateAsync(
                    engineEpoch,
                    sourceClient,
                    engineEvent.SessionId,
                    mode);
            }
            return;
        }

        PublishEngineEvent(
            engineEpoch,
            new SessionUpdateWebEvent(
                engineEvent.SessionId.Value,
                engineEvent.UpdateKind,
                ExtractText(engineEvent.Update),
                engineEvent.Update,
                engineEpoch));
    }

    private async Task ApplyEngineModeUpdateAsync(
        long engineEpoch,
        IEngineClient sourceClient,
        SessionId sessionId,
        SessionMode mode)
    {
        SessionModeChangedWebEvent? modeEvent = null;
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed ||
                engineEpoch != Volatile.Read(ref _engineEventEpoch) ||
                !ReferenceEquals(sourceClient, _client) ||
                !string.Equals(sessionId.Value, _sessionId?.Value, StringComparison.Ordinal) ||
                _confirmedSessionMode == mode)
            {
                return;
            }

            _confirmedSessionMode = mode;
            await TrySaveCrashRecoveryMarkerUnsafeAsync(
                    mode,
                    CrashRecoveryWriteKind.Update)
                .ConfigureAwait(false);
            modeEvent = new SessionModeChangedWebEvent(
                sessionId.Value,
                mode,
                sourceClient.Capabilities.Supports(SessionMode.Plan));
        }
        finally
        {
            _stateGate.Release();
        }

        if (modeEvent is not null)
        {
            PublishEngineEvent(engineEpoch, modeEvent);
        }
    }

    private static bool TryReadSessionMode(JsonElement update, out SessionMode mode)
    {
        mode = SessionMode.Default;
        if (update.ValueKind != JsonValueKind.Object ||
            !update.TryGetProperty("currentModeId", out var modeId) ||
            modeId.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        switch (modeId.GetString())
        {
            case "default":
                return true;
            case "plan":
                mode = SessionMode.Plan;
                return true;
            default:
                return false;
        }
    }

    private void OnPermissionRequested(object? sender, PermissionRequest request)
    {
        if (sender is not IEngineClient sourceClient)
        {
            return;
        }

        if (_disposed ||
            !ReferenceEquals(sourceClient, _client) ||
            !string.Equals(request.SessionId.Value, _sessionId?.Value, StringComparison.Ordinal))
        {
            _ = CancelStalePermissionAsync(sourceClient, request.RequestId);
            return;
        }

        Publish(
            new PermissionRequestedWebEvent(
                request.RequestId,
                request.SessionId.Value,
                request.ToolCallId,
                request.Title,
                request.Options,
                request.Locations,
                request.ToolKind,
                request.RawInput));
        _ = TryShowNotificationAsync(
            request.SessionId.Value,
            AgentDeskNotificationKind.PermissionRequired);
    }

    private static async Task CancelStalePermissionAsync(
        IEngineClient client,
        string requestId)
    {
        try
        {
            _ = await client.RespondToPermissionAsync(
                    requestId,
                    PermissionDecision.Cancelled)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private void OnSidecarExited(IAgentDeskSidecarHost host, SidecarExitedEventArgs args)
    {
        if (args.WasExpected)
        {
            return;
        }

        _ = HandleSidecarExitedAsync(host, args.ExitCode);
    }

    private void OnEngineFaulted(object? sender, EngineFaultedEventArgs args)
    {
        if (sender is IEngineClient client)
        {
            _ = HandleEngineFaultedAsync(client);
        }
    }

    private async Task HandleEngineFaultedAsync(IEngineClient faultedClient)
    {
        EngineStatusWebEvent? statusEvent = null;
        CancellationTokenSource? promptCancellation = null;
        HashSet<CancellationTokenSource> runtimeOperationCancellations = [];

        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || !ReferenceEquals(faultedClient, _client))
            {
                return;
            }

            var host = _host;
            var recoveryTarget = CreateCrashRecoveryTargetUnsafe();
            var sessionId = recoveryTarget?.SessionId.Value;
            _crashedGeneration = _engineGeneration;
            promptCancellation = _promptCancellation;
            _promptInProgress = false;
            _activePromptGeneration = 0;
            _promptCancellation = null;
            DetachEngineUnsafe(runtimeOperationCancellations);
            _crashRecoveryTarget = recoveryTarget;

            var cleanupFailed = false;
            if (host is not null)
            {
                try
                {
                    await StopAndDisposeHostAsync(host).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    cleanupFailed = true;
                    RestoreCleanupHostUnsafe(host);
                }
            }

            statusEvent = SetStatusUnsafe(
                cleanupFailed ? "error" : "stopped",
                cleanupFailed ? EngineErrorMessage : EngineConnectionLostMessage,
                sessionId);
        }
        finally
        {
            _stateGate.Release();
        }

        TryCancel(promptCancellation);
        TryCancel(runtimeOperationCancellations);

        if (statusEvent is not null)
        {
            Publish(statusEvent);
            await TryShowNotificationAsync(
                    statusEvent.SessionId,
                    AgentDeskNotificationKind.TaskFailed)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleSidecarExitedAsync(IAgentDeskSidecarHost exitedHost, int exitCode)
    {
        IAgentDeskSidecarHost? host = null;
        EngineStatusWebEvent? statusEvent = null;
        CancellationTokenSource? promptCancellation = null;
        HashSet<CancellationTokenSource> runtimeOperationCancellations = [];

        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || !ReferenceEquals(exitedHost, _host))
            {
                return;
            }

            host = _host;
            var recoveryTarget = CreateCrashRecoveryTargetUnsafe();
            var sessionId = recoveryTarget?.SessionId.Value;
            _crashedGeneration = _engineGeneration;
            promptCancellation = _promptCancellation;
            _promptInProgress = false;
            _activePromptGeneration = 0;
            _promptCancellation = null;
            DetachEngineUnsafe(runtimeOperationCancellations);
            _crashRecoveryTarget = recoveryTarget;
            statusEvent = SetStatusUnsafe(
                "stopped",
                Message(
                    $"\u5f15\u64ce\u8fdb\u7a0b\u610f\u5916\u9000\u51fa\uff08\u4ee3\u7801 {exitCode}\uff09\u3002",
                    $"The engine process exited unexpectedly (code {exitCode})."),
                sessionId);
        }
        finally
        {
            _stateGate.Release();
        }

        TryCancel(promptCancellation);
        TryCancel(runtimeOperationCancellations);

        if (statusEvent is not null)
        {
            Publish(statusEvent);
        }

        if (host is not null)
        {
            try
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        if (statusEvent is not null)
        {
            await TryShowNotificationAsync(
                    statusEvent.SessionId,
                    AgentDeskNotificationKind.TaskFailed)
                .ConfigureAwait(false);
        }
    }

    private async Task TryShowNotificationAsync(
        string? sessionId,
        AgentDeskNotificationKind kind)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            string language;
            await _stateGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                await EnsureUiPreferencesLoadedUnsafeAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                if (!_uiPreferences.NotificationsEnabled)
                {
                    return;
                }
                language = _uiPreferences.Language;
            }
            finally
            {
                _stateGate.Release();
            }

            await _notificationService
                .ShowAsync(new AgentDeskUserNotification(sessionId, kind), language)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Notifications are best-effort and must never alter engine state.
        }
    }

    private void DetachEngineUnsafe(
        ICollection<CancellationTokenSource>? runtimeOperationCancellations = null,
        bool snapshotRuntimeOperations = true)
    {
        if (snapshotRuntimeOperations)
        {
            ArgumentNullException.ThrowIfNull(runtimeOperationCancellations);
            CollectRuntimeOperationCancellationsUnsafe(runtimeOperationCancellations);
        }
        if (_host is not null && _hostExitedHandler is not null)
        {
            _host.Exited -= _hostExitedHandler;
        }

        if (_client is not null)
        {
            if (_clientEventHandler is not null)
            {
                _client.EventReceived -= _clientEventHandler;
            }
            _client.PermissionRequested -= OnPermissionRequested;
            _client.Faulted -= OnEngineFaulted;
        }

        _clientEventHandler = null;
        lock (_eventGate)
        {
            Interlocked.Increment(ref _engineEventEpoch);
            _sessionId = null;
        }
        _host = null;
        _hostExitedHandler = null;
        _client = null;
        _executionProfile = null;
        _engineProviderIdentity = null;
        _confirmedSessionMode = null;
    }

    private CrashRecoveryTarget? CreateCrashRecoveryTargetUnsafe() =>
        _sessionId is null ||
        string.IsNullOrWhiteSpace(_workspacePath) ||
        _executionProfile is null
            ? null
            : new CrashRecoveryTarget(
                _sessionId,
                _workspacePath,
                _executionProfile.Value,
                _confirmedSessionMode,
                _engineProviderIdentity,
                IsPersistent: false);

    private async Task EnsureCrashRecoveryLoadedUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (_crashRecoveryLoaded)
        {
            return;
        }

        try
        {
            var marker = await _crashRecoveryStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (marker is not null &&
                (_workspacePath is null || string.Equals(
                    marker.WorkspacePath,
                    _workspacePath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                _crashRecoveryTarget = new CrashRecoveryTarget(
                    marker.SessionId,
                    marker.WorkspacePath,
                    marker.ExecutionProfile,
                    marker.SessionMode,
                    marker.ProviderIdentity,
                    IsPersistent: true);
            }
            _crashRecoveryLoaded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _crashRecoveryTarget = null;
            _crashRecoveryLoaded = true;
        }
    }

    private async Task<CrashRecoveryTarget?> PrepareCrashRecoveryReplacementUnsafeAsync()
    {
        var previousRecovery = CreateCrashRecoveryTargetUnsafe() ?? _crashRecoveryTarget;
        await _crashRecoveryStore.ClearAsync(CancellationToken.None)
            .ConfigureAwait(false);
        _crashRecoveryTarget = null;
        return previousRecovery;
    }

    private async Task RestoreCrashRecoveryReplacementUnsafeAsync(
        CrashRecoveryTarget? previousRecovery)
    {
        _crashRecoveryTarget = previousRecovery;
        if (previousRecovery is not
            {
                SessionMode: { } sessionMode,
                ProviderIdentity: { } providerIdentity,
            })
        {
            return;
        }

        try
        {
            await _crashRecoveryStore
                .SaveAsync(
                    new CrashRecoveryMarker(
                        previousRecovery.SessionId,
                        previousRecovery.WorkspacePath,
                        previousRecovery.ExecutionProfile,
                        sessionMode,
                        _options.TimeProvider.GetUtcNow(),
                        providerIdentity),
                    CancellationToken.None)
                .ConfigureAwait(false);
            _crashRecoveryTarget = previousRecovery with { IsPersistent = true };
        }
        catch (Exception)
        {
            // The previous target remains available in memory while the disk marker stays empty.
        }
    }

    private async Task DiscardCrashRecoveryTargetUnsafeAsync()
    {
        await _crashRecoveryStore.ClearAsync(CancellationToken.None)
            .ConfigureAwait(false);
        _crashRecoveryTarget = null;
    }

    private async Task TrySaveCrashRecoveryMarkerUnsafeAsync(
        SessionMode sessionMode,
        CrashRecoveryWriteKind writeKind)
    {
        var recoveryTarget = CreateCrashRecoveryTargetUnsafe();
        if (recoveryTarget is null || recoveryTarget.ProviderIdentity is null)
        {
            return;
        }

        try
        {
            await _crashRecoveryStore
                .SaveAsync(
                    new CrashRecoveryMarker(
                        recoveryTarget.SessionId,
                        recoveryTarget.WorkspacePath,
                        recoveryTarget.ExecutionProfile,
                        sessionMode,
                        _options.TimeProvider.GetUtcNow(),
                        recoveryTarget.ProviderIdentity),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (writeKind is CrashRecoveryWriteKind.Replace)
            {
                _crashRecoveryTarget = null;
            }
        }
    }

    private void RestoreCleanupHostUnsafe(IAgentDeskSidecarHost host)
    {
        if (_host is null)
        {
            _host = host;
        }
    }

    private static async Task StopAndDisposeHostAsync(IAgentDeskSidecarHost host)
    {
        Exception? stopError = null;
        try
        {
            await host.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopError = exception;
        }

        Exception? disposeError = null;
        try
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            disposeError = exception;
        }

        if (disposeError is not null)
        {
            throw stopError ?? disposeError;
        }
    }

    private async Task<IEngineClient> StartHostAsync(
        IAgentDeskSidecarHost host,
        string workspacePath,
        ExecutionProfile executionProfile,
        CancellationToken cancellationToken)
    {
        string? apiKey = null;
        try
        {
            var credentialName = _providerProfile?.CredentialName ?? _options.CredentialName;
            apiKey = _credentialStore.Read(credentialName);
            return await host
                .StartAsync(
                    new SidecarLaunchOptions(workspacePath, executionProfile)
                    {
                        EnginePath = _options.GetEnginePath(executionProfile),
                        ApiKey = apiKey,
                        ProviderProfile = _providerProfile,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            apiKey = null;
        }
    }

    private async Task EnsureProviderSettingsLoadedUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (_providerSettingsLoaded)
        {
            return;
        }

        _providerProfile = await _providerSettingsStore
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        _providerSettingsLoaded = true;
    }

    private async Task EnsureUiPreferencesLoadedUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (_uiPreferencesLoaded)
        {
            return;
        }

        _uiPreferences = (await _uiPreferencesStore
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false))
            .Validate()
            .ApplyHostCapabilities(_options.IsWslStrictAvailable);
        _uiPreferencesLoaded = true;
        if (_workspacePath is null && _status == "idle")
        {
            _statusMessage = WorkspaceRequiredMessage;
        }
    }

    private string Message(string chinese, string english) =>
        string.Equals(_uiPreferences.Language, "en-US", StringComparison.Ordinal)
            ? english
            : chinese;

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (Exception)
        {
            // Cancellation callbacks cannot be allowed to interrupt state cleanup.
        }
    }

    private static void TryCancel(IEnumerable<CancellationTokenSource> cancellations)
    {
        foreach (var cancellation in cancellations)
        {
            TryCancel(cancellation);
        }
    }

    private void CollectRuntimeOperationCancellationsUnsafe(
        ICollection<CancellationTokenSource> cancellations)
    {
        foreach (var cancellation in SnapshotRuntimeOperationCancellationsUnsafe())
        {
            cancellations.Add(cancellation);
        }
    }

    private CancellationTokenSource[] SnapshotRuntimeOperationCancellationsUnsafe()
    {
        _worktreeOperationId = null;
        InvalidateMemoryConfirmationsUnsafe();
        return [.. _runtimeOperations.Values];
    }

    private EngineStatusWebEvent SetStatusUnsafe(
        string status,
        string? message,
        string? sessionId)
    {
        _status = status;
        _statusMessage = message;
        _statusSessionId = sessionId;
        return new EngineStatusWebEvent(
            status,
            message,
            sessionId,
            EngineEpoch: Volatile.Read(ref _engineEventEpoch));
    }

    private void Publish(WebEvent webEvent)
    {
        if (_disposed || _disposing)
        {
            return;
        }

        lock (_eventGate)
        {
            if (_disposed || _disposing)
            {
                return;
            }

            PublishUnsafe(webEvent);
        }
    }

    private void PublishEngineEvent(long engineEpoch, WebEvent webEvent)
    {
        if (_disposed || _disposing)
        {
            return;
        }

        lock (_eventGate)
        {
            if (_disposed ||
                _disposing ||
                engineEpoch != Volatile.Read(ref _engineEventEpoch))
            {
                return;
            }

            PublishUnsafe(webEvent);
        }
    }

    private void PublishUnsafe(WebEvent webEvent)
    {
        var handlers = EventProduced;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<WebEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, webEvent);
            }
            catch (Exception)
            {
            }
        }
    }

    private static string? ExtractText(JsonElement update)
    {
        if (update.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (update.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String &&
            string.Equals(type.GetString(), "text", StringComparison.Ordinal) &&
            content.TryGetProperty("text", out var contentText) &&
            contentText.ValueKind == JsonValueKind.String)
        {
            return contentText.GetString();
        }

        return update.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;
    }

    private static int ParseLocalSessionCursor(string? cursor)
    {
        if (cursor is null)
        {
            return 0;
        }
        const string prefix = "local:";
        if (cursor.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(cursor.AsSpan(prefix.Length), out var offset) &&
            offset >= 0)
        {
            return offset;
        }
        throw new InvalidDataException("The archived-session cursor is invalid.");
    }

    private sealed class EmptyProviderSettingsStore : IProviderSettingsStore
    {
        public Task<ProviderProfile?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ProviderProfile?>(null);

        public Task SaveAsync(
            ProviderProfile profile,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptySessionIndexStore : ISessionIndexStore
    {
        public Task UpsertAsync(
            IReadOnlyCollection<SessionSummary> sessions,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SessionSummary>> SearchAsync(
            string? workspacePath,
            string? query,
            bool archived,
            int limit,
            int offset,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionSummary>>([]);

        public Task<bool> SetArchivedAsync(
            SessionId sessionId,
            bool archived,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<IReadOnlySet<string>> GetArchivedIdsAsync(
            IReadOnlyCollection<SessionId> sessionIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
    }

    private sealed class EmptyCrashRecoveryStore : ICrashRecoveryStore
    {
        public Task<CrashRecoveryMarker?> LoadAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CrashRecoveryMarker?>(null);

        public Task SaveAsync(
            CrashRecoveryMarker marker,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class InMemoryUiPreferencesStore : IUiPreferencesStore
    {
        private UiPreferences _preferences = UiPreferences.Default;

        public Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_preferences);

        public Task SaveAsync(
            UiPreferences preferences,
            CancellationToken cancellationToken = default)
        {
            _preferences = preferences;
            return Task.CompletedTask;
        }
    }

    private sealed class MaintenanceLease(
        AgentDeskHostController owner,
        long leaseId,
        string workspacePath) : IAgentDeskMaintenanceLease
    {
        private int _disposed;

        public string WorkspacePath { get; } = workspacePath;

        public Task<EngineSessionDocument> ExportSessionAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(sessionId);
            return owner.ExportMaintenanceSessionAsync(leaseId, sessionId, cancellationToken);
        }

        public Task<SessionId> ImportSessionAsync(
            EngineSessionDocument document,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return owner.ImportMaintenanceSessionAsync(leaseId, document, cancellationToken);
        }

        public Task StopEngineAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return owner.StopMaintenanceEngineAsync(leaseId, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }
            return owner.ReleaseMaintenanceAsync(leaseId);
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed class CloudEngineLease(
        AgentDeskHostController owner,
        long leaseId,
        IEngineClient engine,
        string workspacePath,
        string engineWorkspacePath) : IAgentDeskCloudEngineLease
    {
        private int _disposed;

        public IEngineClient Engine { get; } = engine;

        public string WorkspacePath { get; } = workspacePath;

        public string EngineWorkspacePath { get; } = engineWorkspacePath;

        public Task ActivateSessionAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return owner.ActivateCloudSessionAsync(
                leaseId,
                Engine,
                sessionId,
                cancellationToken);
        }

        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0
            ? owner.ReleaseCloudEngineOperationAsync(leaseId)
            : ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed || _disposing, this);

    private sealed record PromptContext(
        IEngineClient Client,
        SessionId SessionId,
        int Generation);

    private sealed record CrashRecoveryTarget(
        SessionId SessionId,
        string WorkspacePath,
        ExecutionProfile ExecutionProfile,
        SessionMode? SessionMode,
        string? ProviderIdentity,
        bool IsPersistent);

    private enum CrashRecoveryWriteKind
    {
        Replace,
        Update,
    }

    private sealed record RuntimeOperationContext(
        long OperationId,
        IEngineClient Client,
        SessionId SessionId,
        int EngineGeneration,
        CancellationTokenSource Cancellation,
        bool ExclusiveMutation,
        bool CoalescedRefresh);

    private sealed record WorkspaceOperationContext(
        long OperationId,
        IEngineClient Client,
        SessionId SessionId,
        string WorkspacePath,
        string EngineWorkspacePath,
        int WorkspaceGeneration,
        int EngineGeneration,
        CancellationTokenSource Cancellation,
        bool ExclusiveMutation,
        bool ExclusiveWorktree);

    private sealed class StaleWorkspaceOperationException : InvalidOperationException;
}
