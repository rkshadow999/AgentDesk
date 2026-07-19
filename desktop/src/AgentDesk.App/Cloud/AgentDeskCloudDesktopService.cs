using AgentDesk.Cloud.Client;
using AgentDesk.Core.Engine;
using AgentDesk.Core.Security;
using AgentDesk.Platform.Windows.Cloud;
using AgentDesk.Platform.Windows.Credentials;

namespace AgentDesk.App.Cloud;

public sealed class AgentDeskCloudDesktopService : IAgentDeskCloudDesktopService, IDisposable
{
    private const int MaximumActiveRunnerClaims = 1024;

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly ICloudConnectionProfileStore _profileStore;
    private readonly ICredentialStore _credentialStore;
    private readonly ICloudSyncMetadataStore _metadataStore;
    private readonly IAgentDeskCloudClient? _cloudClientOverride;
    private readonly Func<CloudConnectionProfile, ICloudAccessTokenProvider, ICloudNotificationClient>
        _notificationClientFactory;
    private readonly AgentDeskCloudTaskPayloadCodec _taskPayloadCodec = new();
    private readonly SemaphoreSlim _profileGate = new(1, 1);
    private readonly SemaphoreSlim _notificationGate = new(1, 1);
    private readonly Dictionary<string, RememberedRunnerClaim>
        _claimedRunnerJobs = new(StringComparer.Ordinal);
    private readonly object _claimedRunnerJobsGate = new();
    private int _pendingRunnerClaims;
    private long _profileGeneration;
    private RemoteRuntime? _remoteRuntime;
    private NotificationRuntime? _notificationRuntime;
    private int _disposed;

    public AgentDeskCloudDesktopService()
        : this(
            new JsonCloudConnectionProfileStore(),
            new WindowsCredentialStore(),
            new SqliteCloudSyncMetadataStore(),
            cloudClientOverride: null,
            useDefaultCloudClient: true,
            notificationClientFactory: null)
    {
    }

    public AgentDeskCloudDesktopService(
        ICloudConnectionProfileStore profileStore,
        ICredentialStore credentialStore,
        ICloudSyncMetadataStore metadataStore,
        IAgentDeskCloudClient cloudClient)
        : this(
            profileStore,
            credentialStore,
            metadataStore,
            cloudClientOverride: cloudClient,
            useDefaultCloudClient: false,
            notificationClientFactory: null)
    {
        ArgumentNullException.ThrowIfNull(cloudClient);
    }

    internal AgentDeskCloudDesktopService(
        ICloudConnectionProfileStore profileStore,
        ICredentialStore credentialStore,
        ICloudSyncMetadataStore metadataStore,
        IAgentDeskCloudClient cloudClient,
        Func<CloudConnectionProfile, ICloudAccessTokenProvider, ICloudNotificationClient>?
            notificationClientFactory)
        : this(
            profileStore,
            credentialStore,
            metadataStore,
            cloudClientOverride: cloudClient,
            useDefaultCloudClient: false,
            notificationClientFactory: notificationClientFactory)
    {
        ArgumentNullException.ThrowIfNull(cloudClient);
    }

    private AgentDeskCloudDesktopService(
        ICloudConnectionProfileStore profileStore,
        ICredentialStore credentialStore,
        ICloudSyncMetadataStore metadataStore,
        IAgentDeskCloudClient? cloudClientOverride,
        bool useDefaultCloudClient,
        Func<CloudConnectionProfile, ICloudAccessTokenProvider, ICloudNotificationClient>?
            notificationClientFactory)
    {
        ArgumentNullException.ThrowIfNull(profileStore);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(metadataStore);
        if (!useDefaultCloudClient)
        {
            ArgumentNullException.ThrowIfNull(cloudClientOverride);
        }

        _profileStore = profileStore;
        _credentialStore = credentialStore;
        _metadataStore = metadataStore;
        _cloudClientOverride = cloudClientOverride;
        _notificationClientFactory = notificationClientFactory ?? CreateNotificationClient;
    }

    public async Task StartNotificationsAsync(
        Func<CloudNotification, CancellationToken, Task> notificationHandler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notificationHandler);
        ThrowIfDisposed();
        await _notificationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CloudConnectionProfile profile;
            long profileGeneration;
            ICloudAccessTokenProvider tokenProvider;
            await _profileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                profile = await _profileStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (profile.IsLocalOnly)
                {
                    throw new AgentDeskCloudUnavailableException();
                }
                profileGeneration = _profileGeneration;
                tokenProvider = new CredentialCloudAccessTokenProvider(
                    _credentialStore,
                    profile);
                _ = await tokenProvider
                    .GetAccessTokenAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _profileGate.Release();
            }

            if (_notificationRuntime is { } current &&
                current.ProfileGeneration == profileGeneration &&
                ProfilesMatch(current.Profile, profile))
            {
                return;
            }

            await StopNotificationRuntimeUnsafeAsync(CancellationToken.None)
                .ConfigureAwait(false);
            var client = _notificationClientFactory(profile, tokenProvider);
            var generationCancellation = new CancellationTokenSource();
            try
            {
                await client
                    .StartAsync(
                        notification => DispatchNotificationAsync(
                            profileGeneration,
                            generationCancellation.Token,
                            notificationHandler,
                            notification),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (Volatile.Read(ref _profileGeneration) != profileGeneration)
                {
                    generationCancellation.Cancel();
                }
                _notificationRuntime = new NotificationRuntime(
                    profileGeneration,
                    profile,
                    client,
                    generationCancellation);
            }
            catch
            {
                generationCancellation.Cancel();
                generationCancellation.Dispose();
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _notificationGate.Release();
        }
    }

    public async Task StopNotificationsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _notificationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopNotificationRuntimeUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _notificationGate.Release();
        }
    }

    public async Task<AgentDeskCloudProfileSnapshot> LoadProfileAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _profileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = await _profileStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            return await CreateSnapshotAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _profileGate.Release();
        }
    }

    public async Task<AgentDeskCloudProfileSnapshot> SaveRemoteProfileAsync(
        Uri baseUri,
        string teamId,
        string deviceId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var profile = new CloudConnectionProfile(baseUri, teamId, deviceId);
        var tokenVault = new CredentialCloudAccessTokenProvider(_credentialStore, profile);

        AgentDeskCloudProfileSnapshot snapshot;
        await _profileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousProfile = await _profileStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            tokenVault.SaveAccessToken(accessToken);
            try
            {
                await _profileStore
                    .SaveAsync(profile, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                if (!CredentialNamesMatch(previousProfile, profile))
                {
                    _ = tokenVault.DeleteAccessToken();
                }
                throw;
            }
            DeleteSupersededToken(previousProfile, profile);
            InvalidateRemoteRuntime();
            snapshot = new AgentDeskCloudProfileSnapshot(profile, hasAccessToken: true);
        }
        finally
        {
            _profileGate.Release();
        }
        await StopNotificationsAfterProfileChangeAsync().ConfigureAwait(false);
        return snapshot;
    }

    public async Task<AgentDeskCloudProfileSnapshot> SaveLocalOnlyProfileAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        AgentDeskCloudProfileSnapshot snapshot;
        await _profileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousProfile = await _profileStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            var localOnlyProfile = new CloudConnectionProfile();
            cancellationToken.ThrowIfCancellationRequested();
            await _profileStore
                .SaveAsync(localOnlyProfile, CancellationToken.None)
                .ConfigureAwait(false);
            InvalidateRemoteRuntime();
            if (!previousProfile.IsLocalOnly)
            {
                TryDeleteToken(previousProfile);
            }
            snapshot = new AgentDeskCloudProfileSnapshot(
                localOnlyProfile,
                hasAccessToken: false);
        }
        finally
        {
            _profileGate.Release();
        }
        await StopNotificationsAfterProfileChangeAsync().ConfigureAwait(false);
        return snapshot;
    }

    public async Task<RecoveryKeyPairingPackage> ExportRecoveryKeyPairingPackageAsync(
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return runtime.RecoveryKeyStore.ExportPairingPackage(
            RecoveryKeyReference.ForTeam(runtime.Profile.TeamId!),
            passphrase.Span);
    }

    public async Task ImportRecoveryKeyPairingPackageAsync(
        RecoveryKeyPairingPackage package,
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        runtime.RecoveryKeyStore.ImportPairingPackage(
            RecoveryKeyReference.ForTeam(runtime.Profile.TeamId!),
            package,
            passphrase.Span);
    }

    public async Task<int> UploadSessionAsync(
        IEngineClient engine,
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        return await runtime.Workflow
            .UploadAsync(engine, sessionId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int?> DeleteSessionAsync(
        string remoteSessionId,
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        return await runtime.Workflow
            .DeleteAsync(remoteSessionId, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<EngineSessionDocument> ExportSessionAsync(
        IEngineClient engine,
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(sessionId);
        return engine.ExportSessionAsync(sessionId, cancellationToken);
    }

    public async Task<EngineCloudImportResult?> DownloadAndImportSessionAsync(
        IEngineClient engine,
        string remoteSessionId,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        return await runtime.Workflow
            .DownloadAndImportAsync(
                engine,
                remoteSessionId,
                workingDirectory,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CloudHandoff> CreateHandoffAsync(
        IEngineClient engine,
        SessionId sessionId,
        string targetDeviceId,
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        return await runtime.Workflow
            .CreateHandoffAsync(engine, sessionId, targetDeviceId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EngineCloudHandoffImportResult>> ReceiveHandoffsAsync(
        IEngineClient engine,
        string workingDirectory,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        return await runtime.Workflow
            .ReceiveHandoffsAsync(engine, workingDirectory, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CloudTeamPolicy> GetPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        return await runtime.Client.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudTeamPolicy> UpdatePolicyAsync(
        CloudTeamPolicyUpdate update,
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        return await runtime.Client
            .UpdatePolicyAsync(update, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RegisterRunnerAsync(
        string runnerId,
        IReadOnlyList<string> capabilities,
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        await runtime.Client
            .RegisterRunnerAsync(runnerId, capabilities, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CloudJobReceipt> QueueRunnerJobAsync(
        string requiredCapability,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        var identity = DirectTaskIdentity(requiredCapability);
        return await runtime.Client
            .QueueJobAsync(identity, envelope, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CloudJobReceipt> QueueRunnerTaskAsync(
        string requiredCapability,
        string task,
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        var identity = DirectTaskIdentity(requiredCapability);
        var envelope = _taskPayloadCodec.ProtectTask(
            runtime.Profile,
            runtime.RecoveryKeyStore,
            identity,
            task);
        return await runtime.Client
            .QueueJobAsync(identity, envelope, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AgentDeskCloudRunnerJobClaim?> ClaimRunnerJobAsync(
        string runnerId,
        int leaseSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        ReserveRunnerClaimSlot();
        var reservationActive = true;
        try
        {
            var job = await runtime.Client
                .ClaimJobAsync(runnerId, leaseSeconds, cancellationToken)
                .ConfigureAwait(false);
            if (job is null)
            {
                return null;
            }

            var claimHandle = RememberClaim(job.Identity, job.LeaseExpiresAt);
            reservationActive = false;
            return new AgentDeskCloudRunnerJobClaim(claimHandle, job);
        }
        finally
        {
            if (reservationActive)
            {
                ReleaseRunnerClaimSlot();
            }
        }
    }

    public async Task<AgentDeskCloudRunnerTask?> ClaimRunnerTaskAsync(
        string runnerId,
        int leaseSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        ReserveRunnerClaimSlot();
        var reservationActive = true;
        try
        {
            var job = await runtime.Client
                .ClaimJobAsync(runnerId, leaseSeconds, cancellationToken)
                .ConfigureAwait(false);
            if (job is null)
            {
                return null;
            }
            var task = _taskPayloadCodec.UnprotectTask(
                runtime.Profile,
                runtime.RecoveryKeyStore,
                job);
            var claimHandle = RememberClaim(job.Identity, job.LeaseExpiresAt);
            reservationActive = false;
            return new AgentDeskCloudRunnerTask(
                claimHandle,
                job.Identity,
                task,
                job.LeaseExpiresAt);
        }
        finally
        {
            if (reservationActive)
            {
                ReleaseRunnerClaimSlot();
            }
        }
    }

    public async Task CompleteRunnerJobAsync(
        string claimHandle,
        string jobId,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var claim = GetClaimedRunnerJob(claimHandle, jobId);
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        await runtime.Client
            .CompleteJobAsync(claim.Identity, envelope, cancellationToken)
            .ConfigureAwait(false);
        ForgetClaimedRunnerJob(claimHandle, claim);
    }

    public async Task CompleteRunnerTaskAsync(
        string claimHandle,
        string jobId,
        string result,
        CancellationToken cancellationToken = default)
    {
        var claim = GetClaimedRunnerJob(claimHandle, jobId);
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        var envelope = _taskPayloadCodec.ProtectResult(
            runtime.Profile,
            runtime.RecoveryKeyStore,
            claim.Identity,
            result);
        await runtime.Client
            .CompleteJobAsync(claim.Identity, envelope, cancellationToken)
            .ConfigureAwait(false);
        ForgetClaimedRunnerJob(claimHandle, claim);
    }

    public async Task<CloudAutomation> CreateAutomationAsync(
        string name,
        int intervalSeconds,
        string requiredCapability,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        var automationId = Guid.CreateVersion7().ToString();
        return await runtime.Client
            .CreateAutomationAsync(
                automationId,
                name,
                intervalSeconds,
                requiredCapability,
                envelope,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CloudAutomation> CreateAutomationTaskAsync(
        string name,
        int intervalSeconds,
        string requiredCapability,
        string task,
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        var automationId = Guid.CreateVersion7().ToString();
        var envelope = _taskPayloadCodec.ProtectAutomationTask(
            runtime.Profile,
            runtime.RecoveryKeyStore,
            automationId,
            requiredCapability,
            task);
        return await runtime.Client
            .CreateAutomationAsync(
                automationId,
                name,
                intervalSeconds,
                requiredCapability,
                envelope,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CloudAutomation>> ListAutomationsAsync(
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        return await runtime.Client
            .ListAutomationsAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> DisableAutomationAsync(
        string automationId,
        CancellationToken cancellationToken = default)
    {
        var runtime = await CreateRemoteRuntimeAsync(cancellationToken).ConfigureAwait(false);
        return await runtime.Client
            .DisableAutomationAsync(automationId, cancellationToken)
            .ConfigureAwait(false);
    }

    public override string ToString() => "AgentDeskCloudDesktopService";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _notificationGate.Wait();
        try
        {
            try
            {
                StopNotificationRuntimeUnsafeAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException and not StackOverflowException)
            {
                // Profile and credential cleanup must continue if the transport is already broken.
            }
        }
        finally
        {
            _notificationGate.Release();
        }
        InvalidateRemoteRuntime();
        _notificationGate.Dispose();
        _profileGate.Dispose();
    }

    private async Task StopNotificationsAfterProfileChangeAsync()
    {
        await _notificationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            try
            {
                await StopNotificationRuntimeUnsafeAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException and not StackOverflowException)
            {
                // A persisted profile switch succeeds even if the old connection is already broken.
            }
        }
        finally
        {
            _notificationGate.Release();
        }
    }

    private async Task StopNotificationRuntimeUnsafeAsync(CancellationToken cancellationToken)
    {
        var runtime = _notificationRuntime;
        if (runtime is null)
        {
            return;
        }

        _notificationRuntime = null;
        runtime.Cancel();
        try
        {
            await runtime.Client.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await runtime.Client.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                runtime.GenerationCancellation.Dispose();
            }
        }
    }

    private async Task DispatchNotificationAsync(
        long profileGeneration,
        CancellationToken generationCancellation,
        Func<CloudNotification, CancellationToken, Task> notificationHandler,
        CloudNotification notification)
    {
        if (generationCancellation.IsCancellationRequested ||
            Volatile.Read(ref _profileGeneration) != profileGeneration)
        {
            return;
        }

        try
        {
            await notificationHandler(notification, generationCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (generationCancellation.IsCancellationRequested)
        {
        }
    }

    private async Task<RemoteRuntime> CreateRemoteRuntimeAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        CloudConnectionProfile profile;
        await _profileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            profile = await _profileStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (profile.IsLocalOnly)
            {
                throw new AgentDeskCloudUnavailableException();
            }

            var tokenProvider = new CredentialCloudAccessTokenProvider(
                _credentialStore,
                profile);
            _ = await tokenProvider
                .GetAccessTokenAsync(cancellationToken)
                .ConfigureAwait(false);

            if (_remoteRuntime is { } cached &&
                cached.ProfileGeneration == _profileGeneration &&
                ProfilesMatch(cached.Profile, profile))
            {
                return cached;
            }

            var client = _cloudClientOverride ?? new AgentDeskCloudClient(
                SharedHttpClient,
                profile.CreateConnectionOptions(),
                tokenProvider);
            var recoveryKeyStore = new CredentialRecoveryKeyStore(_credentialStore);
            var workflow = new EngineCloudSessionWorkflow(
                new CloudSyncCoordinator(
                    profile,
                    client,
                    recoveryKeyStore,
                    _metadataStore),
                new EncryptedHandoffCoordinator(
                    profile,
                    client,
                    recoveryKeyStore));
            var runtime = new RemoteRuntime(
                _profileGeneration,
                profile,
                client,
                workflow,
                recoveryKeyStore);
            _remoteRuntime = runtime;
            return runtime;
        }
        finally
        {
            _profileGate.Release();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private async Task<AgentDeskCloudProfileSnapshot> CreateSnapshotAsync(
        CloudConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile.IsLocalOnly)
        {
            return new AgentDeskCloudProfileSnapshot(profile, hasAccessToken: false);
        }

        try
        {
            var tokenProvider = new CredentialCloudAccessTokenProvider(
                _credentialStore,
                profile);
            _ = await tokenProvider
                .GetAccessTokenAsync(cancellationToken)
                .ConfigureAwait(false);
            return new AgentDeskCloudProfileSnapshot(profile, hasAccessToken: true);
        }
        catch (CloudAccessTokenStoreException)
        {
            return new AgentDeskCloudProfileSnapshot(profile, hasAccessToken: false);
        }
    }

    private void DeleteSupersededToken(
        CloudConnectionProfile previousProfile,
        CloudConnectionProfile currentProfile)
    {
        if (previousProfile.IsLocalOnly || CredentialNamesMatch(previousProfile, currentProfile))
        {
            return;
        }

        TryDeleteToken(previousProfile);
    }

    private void TryDeleteToken(CloudConnectionProfile profile)
    {
        try
        {
            _ = new CredentialCloudAccessTokenProvider(_credentialStore, profile)
                .DeleteAccessToken();
        }
        catch (CloudAccessTokenStoreException)
        {
            // The selected profile is already durable; orphan cleanup must not mask success.
        }
    }

    private void InvalidateRemoteRuntime()
    {
        _profileGeneration = checked(_profileGeneration + 1);
        _remoteRuntime = null;
        Volatile.Read(ref _notificationRuntime)?.Cancel();
        lock (_claimedRunnerJobsGate)
        {
            _claimedRunnerJobs.Clear();
        }
    }

    private static CloudRunnerJobIdentity DirectTaskIdentity(string requiredCapability) =>
        new(
            Guid.CreateVersion7().ToString(),
            CloudRunnerPayloadKinds.Task,
            requiredCapability);

    private static ICloudNotificationClient CreateNotificationClient(
        CloudConnectionProfile profile,
        ICloudAccessTokenProvider tokenProvider) =>
        new SignalRCloudNotificationClient(profile, tokenProvider);

    private string RememberClaim(
        CloudRunnerJobIdentity identity,
        DateTimeOffset leaseExpiresAt)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var now = DateTimeOffset.UtcNow;
        lock (_claimedRunnerJobsGate)
        {
            RemoveExpiredRunnerClaims(now);
            if (_pendingRunnerClaims <= 0)
            {
                throw new InvalidOperationException("The cloud runner claim reservation is not active.");
            }
            if (leaseExpiresAt <= now)
            {
                throw new InvalidOperationException("The cloud runner claim has expired.");
            }

            while (true)
            {
                var claimHandle = Guid.CreateVersion7().ToString();
                if (_claimedRunnerJobs.TryAdd(
                    claimHandle,
                    new RememberedRunnerClaim(identity, leaseExpiresAt)))
                {
                    _pendingRunnerClaims--;
                    return claimHandle;
                }
            }
        }
    }

    private void ReserveRunnerClaimSlot()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_claimedRunnerJobsGate)
        {
            RemoveExpiredRunnerClaims(now);
            if (_claimedRunnerJobs.Count + _pendingRunnerClaims >= MaximumActiveRunnerClaims)
            {
                throw new InvalidOperationException("The active cloud runner claim limit was reached.");
            }
            _pendingRunnerClaims++;
        }
    }

    private void ReleaseRunnerClaimSlot()
    {
        lock (_claimedRunnerJobsGate)
        {
            if (_pendingRunnerClaims <= 0)
            {
                throw new InvalidOperationException("The cloud runner claim reservation is not active.");
            }
            _pendingRunnerClaims--;
        }
    }

    private RememberedRunnerClaim GetClaimedRunnerJob(string claimHandle, string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        lock (_claimedRunnerJobsGate)
        {
            RemoveExpiredRunnerClaims(DateTimeOffset.UtcNow);
            if (!_claimedRunnerJobs.TryGetValue(claimHandle, out var claim))
            {
                throw new InvalidOperationException("The cloud runner claim is not active.");
            }
            if (!string.Equals(claim.Identity.JobId, jobId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The cloud runner claim does not match the job.");
            }
            return claim;
        }
    }

    private void ForgetClaimedRunnerJob(
        string claimHandle,
        RememberedRunnerClaim completedClaim)
    {
        lock (_claimedRunnerJobsGate)
        {
            if (_claimedRunnerJobs.TryGetValue(claimHandle, out var currentClaim) &&
                ReferenceEquals(currentClaim, completedClaim))
            {
                _claimedRunnerJobs.Remove(claimHandle);
            }
        }
    }

    private void RemoveExpiredRunnerClaims(DateTimeOffset now)
    {
        var expiredHandles = _claimedRunnerJobs
            .Where(pair => pair.Value.LeaseExpiresAt <= now)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var claimHandle in expiredHandles)
        {
            _claimedRunnerJobs.Remove(claimHandle);
        }
    }

    private static bool CredentialNamesMatch(
        CloudConnectionProfile left,
        CloudConnectionProfile right) =>
        !left.IsLocalOnly &&
        !right.IsLocalOnly &&
        string.Equals(
            left.AccessTokenCredentialName,
            right.AccessTokenCredentialName,
            StringComparison.Ordinal);

    private static bool ProfilesMatch(
        CloudConnectionProfile left,
        CloudConnectionProfile right) =>
        left.IsLocalOnly == right.IsLocalOnly &&
        Equals(left.BaseUri, right.BaseUri) &&
        string.Equals(left.TeamId, right.TeamId, StringComparison.Ordinal) &&
        string.Equals(left.DeviceId, right.DeviceId, StringComparison.Ordinal);

    private sealed record RemoteRuntime(
        long ProfileGeneration,
        CloudConnectionProfile Profile,
        IAgentDeskCloudClient Client,
        EngineCloudSessionWorkflow Workflow,
        CredentialRecoveryKeyStore RecoveryKeyStore);

    private sealed record NotificationRuntime(
        long ProfileGeneration,
        CloudConnectionProfile Profile,
        ICloudNotificationClient Client,
        CancellationTokenSource GenerationCancellation)
    {
        public void Cancel()
        {
            try
            {
                GenerationCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private sealed record RememberedRunnerClaim(
        CloudRunnerJobIdentity Identity,
        DateTimeOffset LeaseExpiresAt);
}
