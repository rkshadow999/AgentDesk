using System.Security.Cryptography;
using AgentDesk.App.Bridge;
using AgentDesk.App.Maintenance;
using AgentDesk.Cloud.Client;
using AgentDesk.Core.Engine;

namespace AgentDesk.App.Cloud;

public sealed class AgentDeskCloudCoordinator : IDisposable
{
    private readonly IAgentDeskCloudDesktopService _service;
    private readonly IAgentDeskCloudEngineHost _engineHost;
    private readonly Func<WebEvent, Task> _publish;
    private readonly AgentDeskCloudPolicyGate _policyGate;
    private readonly PairingPackageFileStore _pairingFiles;
    private readonly SessionDocumentFileStore _sessionDocuments;
    private readonly bool _ownsService;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _notificationShutdown = new();
    private readonly object _requestSync = new();
    private readonly HashSet<string> _usedRequestIds = new(StringComparer.Ordinal);
    private bool _disposed;

    public AgentDeskCloudCoordinator(
        IAgentDeskCloudDesktopService service,
        IAgentDeskCloudEngineHost engineHost,
        Func<WebEvent, Task> publish,
        AgentDeskCloudPolicyGate? policyGate = null,
        bool ownsService = false,
        PairingPackageFileStore? pairingFiles = null,
        SessionDocumentFileStore? sessionDocuments = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _engineHost = engineHost ?? throw new ArgumentNullException(nameof(engineHost));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _policyGate = policyGate ?? new AgentDeskCloudPolicyGate();
        _pairingFiles = pairingFiles ?? new PairingPackageFileStore();
        _sessionDocuments = sessionDocuments ?? new SessionDocumentFileStore();
        _ownsService = ownsService;
    }

    public async Task InitializePolicyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var profile = await _service.LoadProfileAsync(cancellationToken).ConfigureAwait(false);
        await ApplyProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!profile.Profile.IsLocalOnly)
        {
            var policy = await _service.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
            await _policyGate.ApplyPolicyAsync(policy, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task HandleAsync(
        CloudWebCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteAsync(
            command,
            token => HandleCoreAsync(command, token),
            cancellationToken);
    }

    public Task SaveRemoteProfileAsync(
        CloudProfileSaveRemoteWebCommand command,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteAsync(
            command,
            async token => await ProfileEventAsync(
                    command.RequestId,
                    await _service.SaveRemoteProfileAsync(
                            command.BaseUri,
                            command.TeamId,
                            command.DeviceId,
                            accessToken,
                            token)
                        .ConfigureAwait(false),
                    token)
                .ConfigureAwait(false),
            cancellationToken);
    }

    public Task ExportPairingAsync(
        CloudPairingExportWebCommand command,
        string nativePath,
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativePath);
        return ExecuteAsync(
            command,
            async token =>
            {
                var package = await _service
                    .ExportRecoveryKeyPairingPackageAsync(passphrase, token)
                    .ConfigureAwait(false);
                var bytes = package.ExportBytes();
                try
                {
                    await _pairingFiles.WriteAsync(nativePath, bytes, token).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
                return new CloudPairingCompletedWebEvent(command.RequestId, "export");
            },
            cancellationToken);
    }

    public Task ImportPairingAsync(
        CloudPairingImportWebCommand command,
        string nativePath,
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativePath);
        return ExecuteAsync(
            command,
            async token =>
            {
                var bytes = await _pairingFiles.ReadAsync(nativePath, token).ConfigureAwait(false);
                try
                {
                    var package = RecoveryKeyPairingPackage.FromBytes(bytes);
                    await _service
                        .ImportRecoveryKeyPairingPackageAsync(package, passphrase, token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
                return new CloudPairingCompletedWebEvent(command.RequestId, "import");
            },
            cancellationToken);
    }

    public Task ExportSessionAsync(
        CloudSessionExportWebCommand command,
        string nativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativePath);
        return ExecuteAsync(
            command,
            token => ExportSessionCoreAsync(command, nativePath, token),
            cancellationToken);
    }

    public Task CancelAsync(
        CloudWebCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteAsync(
            command,
            _ => Task.FromResult<WebEvent>(
                new CloudCancelledWebEvent(command.RequestId, OperationName(command))),
            cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _notificationShutdown.Cancel();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _service.StopNotificationsAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
        }
        _notificationShutdown.Dispose();
        _operationGate.Dispose();
        if (_ownsService && _service is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private async Task ExecuteAsync(
        CloudWebCommand command,
        Func<CancellationToken, Task<WebEvent>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool duplicate;
        lock (_requestSync)
        {
            duplicate = !_usedRequestIds.Add(command.RequestId);
        }
        if (duplicate)
        {
            await _publish(new CloudErrorWebEvent(command.RequestId, OperationName(command)))
                .ConfigureAwait(false);
            return;
        }
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            await _publish(new CloudErrorWebEvent(command.RequestId, OperationName(command)))
                .ConfigureAwait(false);
            return;
        }

        WebEvent result;
        try
        {
            result = await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            result = new CloudErrorWebEvent(command.RequestId, OperationName(command));
        }
        finally
        {
            _operationGate.Release();
        }
        await _publish(result).ConfigureAwait(false);
    }

    private async Task<WebEvent> HandleCoreAsync(
        CloudWebCommand command,
        CancellationToken cancellationToken) => command switch
        {
            CloudProfileGetWebCommand value => await ProfileEventAsync(
                    value.RequestId,
                    await _service.LoadProfileAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false),
            CloudProfileSaveLocalWebCommand value => await ProfileEventAsync(
                    value.RequestId,
                    await _service.SaveLocalOnlyProfileAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false),
            CloudSessionUploadWebCommand value => await UploadSessionAsync(value, cancellationToken)
                .ConfigureAwait(false),
            CloudSessionDownloadWebCommand value => await DownloadSessionAsync(value, cancellationToken)
                .ConfigureAwait(false),
            CloudSessionDeleteWebCommand value => await DeleteSessionAsync(value, cancellationToken)
                .ConfigureAwait(false),
            CloudHandoffCreateWebCommand value => await CreateHandoffAsync(value, cancellationToken)
                .ConfigureAwait(false),
            CloudHandoffReceiveWebCommand value => await ReceiveHandoffsAsync(value, cancellationToken)
                .ConfigureAwait(false),
            CloudPolicyGetWebCommand value => await PolicyEventAsync(
                    value.RequestId,
                    await _service.GetPolicyAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false),
            CloudPolicyUpdateWebCommand value => await PolicyEventAsync(
                    value.RequestId,
                    await _service.UpdatePolicyAsync(
                            new CloudTeamPolicyUpdate(
                                value.AllowedExecutionProfiles,
                                value.RemoteRunnerEnabled,
                                value.UiAutomationEnabled,
                                value.MaximumConcurrentJobs,
                                value.AllowedPluginPublishers),
                            cancellationToken)
                        .ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false),
            CloudRunnerRegisterWebCommand value => await RegisterRunnerAsync(value, cancellationToken)
                .ConfigureAwait(false),
            CloudRunnerQueueWebCommand value => await QueueRunnerTaskAsync(value, cancellationToken)
                .ConfigureAwait(false),
            CloudRunnerClaimWebCommand value => await ClaimRunnerTaskAsync(value, cancellationToken)
                .ConfigureAwait(false),
            CloudRunnerCompleteWebCommand value => await CompleteRunnerTaskAsync(value, cancellationToken)
                .ConfigureAwait(false),
            CloudAutomationListWebCommand value => await ListAutomationsAsync(
                    value,
                    cancellationToken)
                .ConfigureAwait(false),
            CloudAutomationDisableWebCommand value => await DisableAutomationAsync(
                    value,
                    cancellationToken)
                .ConfigureAwait(false),
            CloudAutomationCreateWebCommand value => await CreateAutomationTaskAsync(
                    value,
                    cancellationToken)
                .ConfigureAwait(false),
            CloudProfileSaveRemoteWebCommand or CloudPairingExportWebCommand or
                CloudPairingImportWebCommand or CloudSessionExportWebCommand =>
                throw new InvalidOperationException(
                    "The cloud operation requires a native secret flow."),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    private async Task<WebEvent> UploadSessionAsync(
        CloudSessionUploadWebCommand command,
        CancellationToken cancellationToken)
    {
        await using var lease = await _engineHost
            .BeginCloudEngineOperationAsync(cancellationToken)
            .ConfigureAwait(false);
        var revision = await _service
            .UploadSessionAsync(lease.Engine, new SessionId(command.SessionId), cancellationToken)
            .ConfigureAwait(false);
        return new CloudSessionUploadedWebEvent(
            command.RequestId,
            command.SessionId,
            revision);
    }

    private async Task<WebEvent> DownloadSessionAsync(
        CloudSessionDownloadWebCommand command,
        CancellationToken cancellationToken)
    {
        await using var lease = await _engineHost
            .BeginCloudEngineOperationAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = await _service
            .DownloadAndImportSessionAsync(
                lease.Engine,
                command.RemoteSessionId,
                lease.EngineWorkspacePath,
                cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
        {
            return new CloudSessionImportedWebEvent(
                command.RequestId,
                command.RemoteSessionId,
                Found: false);
        }
        await lease.ActivateSessionAsync(result.ImportedSessionId, cancellationToken)
            .ConfigureAwait(false);
        return new CloudSessionImportedWebEvent(
            command.RequestId,
            result.RemoteSessionId,
            Found: true,
            result.Revision,
            result.ImportedSessionId.Value);
    }

    private async Task<WebEvent> DeleteSessionAsync(
        CloudSessionDeleteWebCommand command,
        CancellationToken cancellationToken)
    {
        var revision = await _service
            .DeleteSessionAsync(command.RemoteSessionId, cancellationToken)
            .ConfigureAwait(false);
        return new CloudSessionDeletedWebEvent(
            command.RequestId,
            command.RemoteSessionId,
            Found: revision is not null,
            Revision: revision);
    }

    private async Task<WebEvent> ExportSessionCoreAsync(
        CloudSessionExportWebCommand command,
        string nativePath,
        CancellationToken cancellationToken)
    {
        await using var lease = await _engineHost
            .BeginCloudEngineOperationAsync(cancellationToken)
            .ConfigureAwait(false);
        var document = await _service
            .ExportSessionAsync(
                lease.Engine,
                new SessionId(command.SessionId),
                cancellationToken)
            .ConfigureAwait(false);
        await _sessionDocuments
            .SaveAsync(nativePath, document, cancellationToken)
            .ConfigureAwait(false);
        return new CloudSessionExportedWebEvent(
            command.RequestId,
            command.SessionId,
            Path.GetFileName(Path.GetFullPath(nativePath)));
    }

    private async Task<WebEvent> CreateHandoffAsync(
        CloudHandoffCreateWebCommand command,
        CancellationToken cancellationToken)
    {
        await using var lease = await _engineHost
            .BeginCloudEngineOperationAsync(cancellationToken)
            .ConfigureAwait(false);
        var handoff = await _service
            .CreateHandoffAsync(
                lease.Engine,
                new SessionId(command.SessionId),
                command.TargetDeviceId,
                cancellationToken)
            .ConfigureAwait(false);
        return new CloudHandoffCreatedWebEvent(
            command.RequestId,
            handoff.HandoffId,
            handoff.SessionId,
            handoff.TargetDeviceId);
    }

    private async Task<WebEvent> ReceiveHandoffsAsync(
        CloudHandoffReceiveWebCommand command,
        CancellationToken cancellationToken)
    {
        await using var lease = await _engineHost
            .BeginCloudEngineOperationAsync(cancellationToken)
            .ConfigureAwait(false);
        var imports = await _service
            .ReceiveHandoffsAsync(
                lease.Engine,
                lease.EngineWorkspacePath,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (imports.Count > 0)
        {
            await lease.ActivateSessionAsync(imports[^1].ImportedSessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        return new CloudHandoffsReceivedWebEvent(
            command.RequestId,
            imports.Select(item => new CloudHandoffImportWebSummary(
                item.HandoffId,
                item.SourceDeviceId,
                item.RemoteSessionId,
                item.ImportedSessionId.Value)).ToArray());
    }

    private async Task<WebEvent> RegisterRunnerAsync(
        CloudRunnerRegisterWebCommand command,
        CancellationToken cancellationToken)
    {
        EnsureRemoteRunnerAllowed();
        await _service
            .RegisterRunnerAsync(command.RunnerId, command.Capabilities, cancellationToken)
            .ConfigureAwait(false);
        return new CloudRunnerRegisteredWebEvent(
            command.RequestId,
            command.RunnerId,
            command.Capabilities);
    }

    private async Task<WebEvent> QueueRunnerTaskAsync(
        CloudRunnerQueueWebCommand command,
        CancellationToken cancellationToken)
    {
        EnsureRemoteRunnerAllowed();
        var receipt = await _service
            .QueueRunnerTaskAsync(
                command.RequiredCapability,
                command.Task,
                cancellationToken)
            .ConfigureAwait(false);
        return new CloudRunnerQueuedWebEvent(command.RequestId, receipt.JobId);
    }

    private async Task<WebEvent> ClaimRunnerTaskAsync(
        CloudRunnerClaimWebCommand command,
        CancellationToken cancellationToken)
    {
        EnsureRemoteRunnerAllowed();
        var task = await _service
            .ClaimRunnerTaskAsync(command.RunnerId, command.LeaseSeconds, cancellationToken)
            .ConfigureAwait(false);
        return task is null
            ? new CloudRunnerClaimedWebEvent(command.RequestId, Found: false)
            : new CloudRunnerClaimedWebEvent(
                command.RequestId,
                Found: true,
                task.JobId,
                task.RequiredCapability,
                task.Task,
                task.LeaseExpiresAt,
                task.ClaimHandle);
    }

    private async Task<WebEvent> CompleteRunnerTaskAsync(
        CloudRunnerCompleteWebCommand command,
        CancellationToken cancellationToken)
    {
        EnsureRemoteRunnerAllowed();
        await _service
            .CompleteRunnerTaskAsync(
                command.ClaimHandle,
                command.JobId,
                command.Result,
                cancellationToken)
            .ConfigureAwait(false);
        return new CloudRunnerCompletedWebEvent(
            command.RequestId,
            command.ClaimHandle,
            command.JobId);
    }

    private async Task<WebEvent> ListAutomationsAsync(
        CloudAutomationListWebCommand command,
        CancellationToken cancellationToken)
    {
        return new CloudAutomationsWebEvent(
            command.RequestId,
            (await _service.ListAutomationsAsync(cancellationToken).ConfigureAwait(false))
                .Select(AutomationSummary)
                .ToArray());
    }

    private async Task<WebEvent> DisableAutomationAsync(
        CloudAutomationDisableWebCommand command,
        CancellationToken cancellationToken)
    {
        return new CloudAutomationDisabledWebEvent(
            command.RequestId,
            command.AutomationId,
            await _service.DisableAutomationAsync(command.AutomationId, cancellationToken)
            .ConfigureAwait(false));
    }

    private async Task<WebEvent> CreateAutomationTaskAsync(
        CloudAutomationCreateWebCommand command,
        CancellationToken cancellationToken)
    {
        EnsureRemoteRunnerAllowed();
        var automation = await _service
            .CreateAutomationTaskAsync(
                command.Name,
                command.IntervalSeconds,
                command.RequiredCapability,
                command.Task,
                cancellationToken)
            .ConfigureAwait(false);
        return new CloudAutomationCreatedWebEvent(
            command.RequestId,
            AutomationSummary(automation));
    }

    private void EnsureRemoteRunnerAllowed()
    {
        if (!_policyGate.AllowsRemoteRunner)
        {
            throw new InvalidOperationException("Remote runner operations are disabled by policy.");
        }
    }

    private async Task<CloudProfileWebEvent> ProfileEventAsync(
        string requestId,
        AgentDeskCloudProfileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await ApplyProfileAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return new CloudProfileWebEvent(
            requestId,
            snapshot.Profile.IsLocalOnly,
            snapshot.Profile.BaseUri?.AbsoluteUri,
            snapshot.Profile.TeamId,
            snapshot.Profile.DeviceId,
            snapshot.HasAccessToken);
    }

    private Task ApplyProfileAsync(
        AgentDeskCloudProfileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        return snapshot.Profile.IsLocalOnly
            ? ApplyLocalProfileAsync(cancellationToken)
            : ApplyRemoteProfileAsync(cancellationToken);
    }

    private async Task ApplyLocalProfileAsync(CancellationToken cancellationToken)
    {
        await _service.StopNotificationsAsync(cancellationToken).ConfigureAwait(false);
        await _policyGate.ApplyLocalOnlyProfileAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyRemoteProfileAsync(CancellationToken cancellationToken)
    {
        await _policyGate.ApplyRemoteProfileAsync(cancellationToken).ConfigureAwait(false);
        await _service
            .StartNotificationsAsync(HandleNotificationAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleNotificationAsync(
        CloudNotification notification,
        CancellationToken generationCancellation)
    {
        if (_disposed)
        {
            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _notificationShutdown.Token,
            generationCancellation);
        var cancellationToken = linkedCancellation.Token;
        try
        {
            if (notification.Kind is CloudNotificationKind.PolicyChanged)
            {
                try
                {
                    await RefreshPolicyAfterNotificationAsync(notification, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (
                    exception is not OutOfMemoryException and not StackOverflowException)
                {
                    // The gate remains remote-unverified; the bounded change signal is still useful.
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _publish(NotificationEvent(notification)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Push failures remain fail-closed and never project transport details.
        }
    }

    private async Task RefreshPolicyAfterNotificationAsync(
        CloudNotification notification,
        CancellationToken cancellationToken)
    {
        await _policyGate.ApplyRemoteProfileAsync(cancellationToken).ConfigureAwait(false);
        var policy = await _service.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        if (policy.Version < notification.PolicyVersion)
        {
            return;
        }
        await _policyGate.ApplyPolicyAsync(policy, cancellationToken).ConfigureAwait(false);
    }

    private static CloudNotificationWebEvent NotificationEvent(
        CloudNotification notification) => notification.Kind switch
        {
            CloudNotificationKind.HandoffChanged => new CloudNotificationWebEvent(
                "handoff-changed",
                notification.ResourceId),
            CloudNotificationKind.JobChanged => new CloudNotificationWebEvent(
                "job-changed",
                notification.ResourceId),
            CloudNotificationKind.PolicyChanged => new CloudNotificationWebEvent(
                "policy-changed",
                PolicyVersion: notification.PolicyVersion),
            _ => throw new ArgumentOutOfRangeException(nameof(notification)),
        };

    private async Task<CloudPolicyWebEvent> PolicyEventAsync(
        string requestId,
        CloudTeamPolicy policy,
        CancellationToken cancellationToken)
    {
        await _policyGate.ApplyPolicyAsync(policy, cancellationToken).ConfigureAwait(false);
        return new CloudPolicyWebEvent(
            requestId,
            policy.Version,
            policy.AllowedExecutionProfiles,
            policy.RemoteRunnerEnabled,
            policy.UiAutomationEnabled,
            policy.MaximumConcurrentJobs,
            policy.AllowedPluginPublishers);
    }

    private static CloudAutomationWebSummary AutomationSummary(CloudAutomation automation) => new(
        automation.AutomationId,
        automation.Name,
        automation.IntervalSeconds,
        automation.Enabled,
        automation.NextRunAt);

    public static string OperationName(CloudWebCommand command) => command switch
    {
        CloudProfileGetWebCommand => "profile-get",
        CloudProfileSaveLocalWebCommand => "profile-save-local",
        CloudProfileSaveRemoteWebCommand => "profile-save-remote",
        CloudPairingExportWebCommand => "pairing-export",
        CloudPairingImportWebCommand => "pairing-import",
        CloudSessionUploadWebCommand => "session-upload",
        CloudSessionDownloadWebCommand => "session-download",
        CloudSessionDeleteWebCommand => "session-delete",
        CloudSessionExportWebCommand => "session-export",
        CloudHandoffCreateWebCommand => "handoff-create",
        CloudHandoffReceiveWebCommand => "handoff-receive",
        CloudPolicyGetWebCommand => "policy-get",
        CloudPolicyUpdateWebCommand => "policy-update",
        CloudRunnerRegisterWebCommand => "runner-register",
        CloudRunnerQueueWebCommand => "runner-queue",
        CloudRunnerClaimWebCommand => "runner-claim",
        CloudRunnerCompleteWebCommand => "runner-complete",
        CloudAutomationListWebCommand => "automation-list",
        CloudAutomationDisableWebCommand => "automation-disable",
        CloudAutomationCreateWebCommand => "automation-create",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };
}
