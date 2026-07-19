using System.Security.Cryptography;

namespace AgentDesk.Cloud.Client;

public sealed class EncryptedHandoffCoordinator
{
    private const int HandoffEnvelopeRevision = 1;

    private readonly CloudConnectionProfile _profile;
    private readonly IAgentDeskCloudClient? _cloudClient;
    private readonly IRecoveryKeyStore _recoveryKeyStore;
    private readonly EncryptedHandoffCoordinatorOptions _options;
    private readonly AesGcmEnvelopeCodec _codec;

    public EncryptedHandoffCoordinator(
        CloudConnectionProfile profile,
        IAgentDeskCloudClient? cloudClient,
        IRecoveryKeyStore recoveryKeyStore,
        EncryptedHandoffCoordinatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(recoveryKeyStore);
        if (!profile.IsLocalOnly && cloudClient is null)
        {
            throw new ArgumentNullException(nameof(cloudClient));
        }

        _profile = profile;
        _cloudClient = cloudClient;
        _recoveryKeyStore = recoveryKeyStore;
        _options = options ?? new EncryptedHandoffCoordinatorOptions();
        _codec = new AesGcmEnvelopeCodec(_options.MaximumDocumentBytes);
    }

    public async Task<CloudHandoff> CreateAsync(
        string targetDeviceId,
        string sessionId,
        SessionSyncDocument document,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(document);
        targetDeviceId = CloudRequestGuard.Identifier(
            targetDeviceId,
            128,
            nameof(targetDeviceId));
        sessionId = CloudRequestGuard.Identifier(sessionId, 128, nameof(sessionId));
        EnsureRemoteAvailable();
        if (document.ByteLength > _options.MaximumDocumentBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(document),
                "The handoff document exceeds the configured size limit.");
        }

        var key = TeamRecoveryKeyResolver.GetOrCreate(_recoveryKeyStore, _profile);
        var plaintext = document.ExportUtf8Json();
        var handoffId = Guid.CreateVersion7().ToString();
        try
        {
            var envelope = _codec.Encrypt(
                plaintext,
                key,
                new HandoffEnvelopeBinding(
                    _profile.TeamId!,
                    _profile.DeviceId!,
                    targetDeviceId,
                    sessionId,
                    handoffId,
                    HandoffEnvelopeRevision));
            var handoff = await _cloudClient!
                .CreateHandoffAsync(
                    handoffId,
                    targetDeviceId,
                    sessionId,
                    envelope,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(handoff.HandoffId, handoffId, StringComparison.Ordinal) ||
                !string.Equals(handoff.SourceDeviceId, _profile.DeviceId, StringComparison.Ordinal) ||
                !string.Equals(handoff.TargetDeviceId, targetDeviceId, StringComparison.Ordinal) ||
                !string.Equals(handoff.SessionId, sessionId, StringComparison.Ordinal))
            {
                throw new CloudClientException(
                    CloudClientErrorKind.InvalidResponse,
                    "The cloud returned a handoff that did not match the request.");
            }
            return handoff;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<IReadOnlyList<CloudHandoffDownloadResult>> ListAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureRemoteAvailable();
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var handoffs = await _cloudClient!
            .ListHandoffsAsync(limit, cancellationToken)
            .ConfigureAwait(false);
        if (handoffs.Count == 0)
        {
            return [];
        }

        var key = TeamRecoveryKeyResolver.Read(_recoveryKeyStore, _profile) ??
            throw new CloudRecoveryKeyUnavailableException();
        try
        {
            var results = new List<CloudHandoffDownloadResult>(handoffs.Count);
            foreach (var handoff in handoffs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(
                        handoff.TargetDeviceId,
                        _profile.DeviceId,
                        StringComparison.Ordinal))
                {
                    throw new CloudClientException(
                        CloudClientErrorKind.InvalidResponse,
                        "The cloud returned a handoff for a different target device.");
                }

                var plaintext = _codec.Decrypt(
                    handoff.Envelope,
                    key,
                    new HandoffEnvelopeBinding(
                        _profile.TeamId!,
                        handoff.SourceDeviceId,
                        handoff.TargetDeviceId,
                        handoff.SessionId,
                        handoff.HandoffId,
                        HandoffEnvelopeRevision));
                try
                {
                    var document = SessionSyncDocument.FromUtf8Json(plaintext);
                    results.Add(new CloudHandoffDownloadResult(
                        handoff.HandoffId,
                        handoff.SourceDeviceId,
                        handoff.TargetDeviceId,
                        handoff.SessionId,
                        handoff.CreatedAt,
                        document));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            return results;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public Task<bool> AcknowledgeAsync(
        string handoffId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        handoffId = CloudRequestGuard.Identifier(handoffId, 128, nameof(handoffId));
        EnsureRemoteAvailable();
        return _cloudClient!.AcknowledgeHandoffAsync(handoffId, cancellationToken);
    }

    private void EnsureRemoteAvailable()
    {
        if (_profile.IsLocalOnly)
        {
            throw new CloudSyncUnavailableException();
        }
    }
}
