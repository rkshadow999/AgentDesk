using System.Security.Cryptography;
using AgentDesk.Cloud.Client;

namespace AgentDesk.Cloud.Client.Tests;

internal sealed class RecordingCloudClient : IAgentDeskCloudClient
{
    public List<(string SessionId, int Revision, EncryptedEnvelope Envelope)> Uploads { get; } = [];

    public List<(string SessionId, int KnownRevision)> DeletedSessions { get; } = [];

    public CloudSyncedSession? SessionToDownload { get; set; }

    public Exception? GetSessionException { get; set; }

    public List<CloudHandoff> Handoffs { get; } = [];

    public string SourceDeviceId { get; set; } = "device-1";

    public Func<string, int, EncryptedEnvelope, CloudSessionWriteReceipt>? OnUpload { get; set; }

    public Func<string, int, CloudSessionDeleteReceipt>? OnDelete { get; set; }

    public Task<CloudSessionWriteReceipt> PutSessionAsync(
        string sessionId,
        int revision,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Uploads.Add((sessionId, revision, envelope));
        return Task.FromResult(
            OnUpload?.Invoke(sessionId, revision, envelope) ??
            new CloudSessionWriteReceipt(sessionId, revision));
    }

    public Task<CloudSyncedSession?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (GetSessionException is not null)
        {
            return Task.FromException<CloudSyncedSession?>(GetSessionException);
        }
        return Task.FromResult(SessionToDownload);
    }

    public Task<CloudSessionDeleteReceipt> DeleteSessionAsync(
        string sessionId,
        int knownRevision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeletedSessions.Add((sessionId, knownRevision));
        return Task.FromResult(
            OnDelete?.Invoke(sessionId, knownRevision) ??
            new CloudSessionDeleteReceipt(sessionId, checked(knownRevision + 1)));
    }

    public Task<CloudHandoff> CreateHandoffAsync(
        string handoffId,
        string targetDeviceId,
        string sessionId,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handoff = new CloudHandoff(
            handoffId,
            SourceDeviceId,
            targetDeviceId,
            sessionId,
            envelope,
            DateTimeOffset.UtcNow);
        Handoffs.Add(handoff);
        return Task.FromResult(handoff);
    }

    public Task<IReadOnlyList<CloudHandoff>> ListHandoffsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<CloudHandoff>>(Handoffs.Take(limit).ToArray());
    }

    public Task<bool> AcknowledgeHandoffAsync(
        string handoffId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var removed = Handoffs.RemoveAll(item => item.HandoffId == handoffId) > 0;
        return Task.FromResult(removed);
    }

    public Task<CloudIssuedToken> CreateTokenAsync(
        string subjectId,
        CloudTokenRole role,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task RevokeTokenAsync(
        string subjectId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CloudTeamPolicy> GetPolicyAsync(
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CloudTeamPolicy> UpdatePolicyAsync(
        CloudTeamPolicyUpdate update,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task RegisterRunnerAsync(
        string runnerId,
        IReadOnlyList<string> capabilities,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CloudJobReceipt> QueueJobAsync(
        CloudRunnerJobIdentity identity,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CloudRunnerJob?> ClaimJobAsync(
        string runnerId,
        int leaseSeconds = 60,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task CompleteJobAsync(
        CloudRunnerJobIdentity identity,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CloudAutomation> CreateAutomationAsync(
        string automationId,
        string name,
        int intervalSeconds,
        string requiredCapability,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<CloudAutomation>> ListAutomationsAsync(
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<bool> DisableAutomationAsync(
        string automationId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class RecordingCloudSyncMetadataStore : ICloudSyncMetadataStore
{
    private readonly Dictionary<string, int> _revisions = new(StringComparer.Ordinal);

    public CloudConnectionProfile? Profile { get; private set; }

    public int SaveProfileCount { get; private set; }

    public int RemainingSaveRevisionFailures { get; set; }

    public int RemainingDeleteRevisionFailures { get; set; }

    public IReadOnlyDictionary<string, int> Revisions => _revisions;

    public ValueTask<CloudConnectionProfile?> ReadProfileAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Profile);
    }

    public ValueTask SaveProfileAsync(
        CloudConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Profile = profile;
        SaveProfileCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask<int?> ReadRevisionAsync(
        CloudSyncMetadataScope scope,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _revisions.TryGetValue(sessionId, out var revision) ? (int?)revision : null);
    }

    public ValueTask SaveRevisionAsync(
        CloudSyncMetadataScope scope,
        string sessionId,
        int revision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RemainingSaveRevisionFailures > 0)
        {
            RemainingSaveRevisionFailures--;
            throw new InvalidOperationException("The simulated revision save failed.");
        }
        _revisions[sessionId] = revision;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteRevisionAsync(
        CloudSyncMetadataScope scope,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RemainingDeleteRevisionFailures > 0)
        {
            RemainingDeleteRevisionFailures--;
            throw new InvalidOperationException("The simulated revision delete failed.");
        }
        _revisions.Remove(sessionId);
        return ValueTask.CompletedTask;
    }

    public void SeedRevision(string sessionId, int revision) => _revisions[sessionId] = revision;
}

internal sealed class RecordingRecoveryKeyStore(byte[] key) : IRecoveryKeyStore
{
    private readonly Dictionary<RecoveryKeyReference, byte[]> _keys = [];
    private readonly byte[] _defaultKey = key.ToArray();

    public int GetOrCreateCount { get; private set; }

    public int ReadCount { get; private set; }

    public void Save(RecoveryKeyReference reference, ReadOnlySpan<byte> value) =>
        _keys[reference] = value.ToArray();

    public byte[]? Read(RecoveryKeyReference reference)
    {
        ReadCount++;
        return _keys.TryGetValue(reference, out var value) ? value.ToArray() : null;
    }

    public byte[] GetOrCreate(RecoveryKeyReference reference)
    {
        GetOrCreateCount++;
        if (!_keys.TryGetValue(reference, out var value))
        {
            value = _defaultKey.ToArray();
            _keys[reference] = value;
        }
        return value.ToArray();
    }

    public bool Delete(RecoveryKeyReference reference)
    {
        if (_keys.Remove(reference, out var value))
        {
            CryptographicOperations.ZeroMemory(value);
            return true;
        }
        return false;
    }
}
