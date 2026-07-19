using System.Security.Cryptography;

namespace AgentDesk.Cloud.Client;

public sealed class CloudSyncCoordinator
{
    private const string SessionSyncBindingScope = "session-sync";

    private readonly CloudConnectionProfile _profile;
    private readonly IAgentDeskCloudClient? _cloudClient;
    private readonly IRecoveryKeyStore _recoveryKeyStore;
    private readonly ICloudSyncMetadataStore _metadataStore;
    private readonly CloudSyncMetadataScope _metadataScope;
    private readonly CloudSyncCoordinatorOptions _options;
    private readonly AesGcmEnvelopeCodec _codec;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _profileInitialized;

    public CloudSyncCoordinator(
        CloudConnectionProfile profile,
        IAgentDeskCloudClient? cloudClient,
        IRecoveryKeyStore recoveryKeyStore,
        ICloudSyncMetadataStore metadataStore,
        CloudSyncCoordinatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(recoveryKeyStore);
        ArgumentNullException.ThrowIfNull(metadataStore);
        if (!profile.IsLocalOnly && cloudClient is null)
        {
            throw new ArgumentNullException(nameof(cloudClient));
        }

        _profile = profile;
        _cloudClient = cloudClient;
        _recoveryKeyStore = recoveryKeyStore;
        _metadataStore = metadataStore;
        _metadataScope = profile.IsLocalOnly
            ? null!
            : CloudSyncMetadataScope.FromProfile(profile);
        _options = options ?? new CloudSyncCoordinatorOptions();
        _codec = new AesGcmEnvelopeCodec(_options.MaximumDocumentBytes);
    }

    public async Task<int> UploadAsync(
        string sessionId,
        SessionSyncDocument document,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(document);
        sessionId = ValidateSessionId(sessionId);
        EnsureRemoteAvailable();
        if (document.ByteLength > _options.MaximumDocumentBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(document),
                "The sync document exceeds the configured size limit.");
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureProfileAsync(cancellationToken).ConfigureAwait(false);
            var knownRevision = await _metadataStore
                .ReadRevisionAsync(_metadataScope, sessionId, cancellationToken)
                .ConfigureAwait(false);
            var key = GetRecoveryKey();
            var plaintext = document.ExportUtf8Json();
            try
            {
                var revision = checked(knownRevision.GetValueOrDefault() + 1);
                var envelope = _codec.Encrypt(
                    plaintext,
                    key,
                    CreateBinding(sessionId, revision));
                CloudSessionWriteReceipt receipt;
                try
                {
                    receipt = await _cloudClient!
                        .PutSessionAsync(sessionId, revision, envelope, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (CloudClientException exception)
                    when (exception.Kind == CloudClientErrorKind.Conflict)
                {
                    return await ReconcileRemoteCommitAsync(
                            sessionId,
                            knownRevision,
                            plaintext,
                            key,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                if (!string.Equals(receipt.SessionId, sessionId, StringComparison.Ordinal) ||
                    receipt.Revision != revision)
                {
                    throw new CloudClientException(
                        CloudClientErrorKind.InvalidResponse,
                        "The cloud returned a session write receipt that did not match the request.");
                }

                // The remote write is committed; finish the local revision update atomically.
                await _metadataStore
                    .SaveRevisionAsync(_metadataScope, sessionId, revision, CancellationToken.None)
                    .ConfigureAwait(false);
                return revision;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<CloudSyncDownloadResult?> DownloadAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sessionId = ValidateSessionId(sessionId);
        EnsureRemoteAvailable();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureProfileAsync(cancellationToken).ConfigureAwait(false);
            CloudSyncedSession? syncedSession;
            try
            {
                syncedSession = await _cloudClient!
                    .GetSessionAsync(sessionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CloudSessionDeletedException tombstone)
            {
                await ObserveTombstoneAsync(sessionId, tombstone, cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }
            if (syncedSession is null)
            {
                return null;
            }
            if (!string.Equals(syncedSession.SessionId, sessionId, StringComparison.Ordinal))
            {
                throw new CloudClientException(
                    CloudClientErrorKind.InvalidResponse,
                    "The cloud returned a different session than the one requested.");
            }

            var knownRevision = await _metadataStore
                .ReadRevisionAsync(_metadataScope, sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (knownRevision is int persisted && syncedSession.Revision < persisted)
            {
                throw new CloudRollbackDetectedException(persisted, syncedSession.Revision);
            }

            var key = GetRecoveryKey();
            byte[]? plaintext = null;
            try
            {
                plaintext = _codec.Decrypt(
                    syncedSession.Envelope,
                    key,
                    CreateBinding(sessionId, syncedSession.Revision));
                var document = SessionSyncDocument.FromUtf8Json(plaintext);
                await _metadataStore
                    .SaveRevisionAsync(
                        _metadataScope,
                        sessionId,
                        syncedSession.Revision,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new CloudSyncDownloadResult(
                    sessionId,
                    syncedSession.Revision,
                    document);
            }
            finally
            {
                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<int?> DeleteAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sessionId = ValidateSessionId(sessionId);
        EnsureRemoteAvailable();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureProfileAsync(cancellationToken).ConfigureAwait(false);
            var knownRevision = await _metadataStore
                .ReadRevisionAsync(_metadataScope, sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (knownRevision is null)
            {
                CloudSyncedSession? remote;
                try
                {
                    remote = await _cloudClient!
                        .GetSessionAsync(sessionId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (CloudSessionDeletedException tombstone)
                {
                    await ObserveTombstoneAsync(sessionId, tombstone, cancellationToken)
                        .ConfigureAwait(false);
                    return tombstone.Revision;
                }
                if (remote is null)
                {
                    return null;
                }
                if (!string.Equals(remote.SessionId, sessionId, StringComparison.Ordinal))
                {
                    throw new CloudClientException(
                        CloudClientErrorKind.InvalidResponse,
                        "The cloud returned a different session than the one requested.");
                }
                knownRevision = remote.Revision;
                // Persist the observed high water before a delete attempt can conflict or cancel.
                await _metadataStore
                    .SaveRevisionAsync(
                        _metadataScope,
                        sessionId,
                        knownRevision.Value,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            CloudSessionDeleteReceipt receipt;
            try
            {
                receipt = await _cloudClient!
                    .DeleteSessionAsync(sessionId, knownRevision.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CloudClientException exception)
                when (exception.Kind == CloudClientErrorKind.NotFound)
            {
                return null;
            }
            if (!string.Equals(receipt.SessionId, sessionId, StringComparison.Ordinal) ||
                (receipt.Revision != knownRevision.Value &&
                    receipt.Revision != (long)knownRevision.Value + 1))
            {
                throw new CloudClientException(
                    CloudClientErrorKind.InvalidResponse,
                    "The cloud returned a session delete receipt that did not match the request.");
            }

            // The tombstone is a committed revision and remains the local anti-rollback high water.
            await _metadataStore
                .SaveRevisionAsync(
                    _metadataScope,
                    sessionId,
                    receipt.Revision,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return receipt.Revision;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<bool> ExportAsync(
        string sessionId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The export destination must be writable.", nameof(destination));
        }

        var downloaded = await DownloadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (downloaded is null)
        {
            return false;
        }

        var plaintext = downloaded.Document.ExportUtf8Json();
        try
        {
            await destination.WriteAsync(plaintext, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async ValueTask EnsureProfileAsync(CancellationToken cancellationToken)
    {
        if (_profileInitialized)
        {
            return;
        }

        var stored = await _metadataStore
            .ReadProfileAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!ProfilesMatch(stored, _profile))
        {
            await _metadataStore
                .SaveProfileAsync(_profile, cancellationToken)
                .ConfigureAwait(false);
        }

        _profileInitialized = true;
    }

    private async Task<int> ReconcileRemoteCommitAsync(
        string sessionId,
        int? knownRevision,
        byte[] localPlaintext,
        byte[] key,
        CancellationToken cancellationToken)
    {
        CloudSyncedSession? syncedSession;
        try
        {
            syncedSession = await _cloudClient!
                .GetSessionAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CloudSessionDeletedException tombstone)
        {
            await ObserveTombstoneAsync(sessionId, tombstone, cancellationToken)
                .ConfigureAwait(false);
            throw new CloudSyncConflictException(
                CloudSyncConflictKind.RemoteDeleted,
                sessionId,
                knownRevision,
                tombstone.Revision);
        }
        if (syncedSession is null)
        {
            throw new CloudSyncConflictException(
                CloudSyncConflictKind.RemoteMissing,
                sessionId,
                knownRevision,
                remoteRevision: null);
        }
        if (!string.Equals(syncedSession.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new CloudClientException(
                CloudClientErrorKind.InvalidResponse,
                "The cloud returned a different session than the one requested.");
        }
        if (knownRevision is int persisted && syncedSession.Revision < persisted)
        {
            throw new CloudRollbackDetectedException(persisted, syncedSession.Revision);
        }

        byte[]? plaintext = null;
        try
        {
            plaintext = _codec.Decrypt(
                syncedSession.Envelope,
                key,
                CreateBinding(sessionId, syncedSession.Revision));
            if (!CryptographicOperations.FixedTimeEquals(plaintext, localPlaintext))
            {
                throw new CloudSyncConflictException(
                    CloudSyncConflictKind.RemoteDocumentChanged,
                    sessionId,
                    knownRevision,
                    syncedSession.Revision);
            }
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        await _metadataStore
            .SaveRevisionAsync(
                _metadataScope,
                sessionId,
                syncedSession.Revision,
                CancellationToken.None)
            .ConfigureAwait(false);
        return syncedSession.Revision;
    }

    private async Task ObserveTombstoneAsync(
        string sessionId,
        CloudSessionDeletedException tombstone,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(tombstone.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new CloudClientException(
                CloudClientErrorKind.InvalidResponse,
                "The cloud returned a tombstone for a different session.");
        }
        var knownRevision = await _metadataStore
            .ReadRevisionAsync(_metadataScope, sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (knownRevision is int persisted && tombstone.Revision < persisted)
        {
            throw new CloudRollbackDetectedException(persisted, tombstone.Revision);
        }
        await _metadataStore
            .SaveRevisionAsync(
                _metadataScope,
                sessionId,
                tombstone.Revision,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private byte[] GetRecoveryKey() =>
        TeamRecoveryKeyResolver.GetOrCreate(_recoveryKeyStore, _profile);

    private EnvelopeBinding CreateBinding(string sessionId, int revision) => new(
        _profile.TeamId!,
        SessionSyncBindingScope,
        sessionId,
        revision);

    private void EnsureRemoteAvailable()
    {
        if (_profile.IsLocalOnly)
        {
            throw new CloudSyncUnavailableException();
        }
    }

    private static string ValidateSessionId(string sessionId) =>
        CloudRequestGuard.Identifier(sessionId, 128, nameof(sessionId));

    private static bool ProfilesMatch(
        CloudConnectionProfile? left,
        CloudConnectionProfile right) =>
        left is not null &&
        left.IsLocalOnly == right.IsLocalOnly &&
        Equals(left.BaseUri, right.BaseUri) &&
        string.Equals(left.TeamId, right.TeamId, StringComparison.Ordinal) &&
        string.Equals(left.DeviceId, right.DeviceId, StringComparison.Ordinal);
}
