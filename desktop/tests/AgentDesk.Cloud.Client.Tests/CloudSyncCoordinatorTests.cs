using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentDesk.Cloud.Client;

namespace AgentDesk.Cloud.Client.Tests;

public sealed class CloudSyncCoordinatorTests
{
    private static readonly CloudConnectionProfile Profile = new(
        new Uri("https://cloud.agentdesk.example/"),
        "team-1",
        "device-1");

    [Fact]
    public async Task UploadEncryptsExplicitDocumentAndAdvancesRevisionMonotonically()
    {
        const string marker = "explicit-session-content";
        var key = RandomNumberGenerator.GetBytes(32);
        var client = new RecordingCloudClient();
        var metadata = new RecordingCloudSyncMetadataStore();
        var keys = new RecordingRecoveryKeyStore(key);
        var coordinator = new CloudSyncCoordinator(Profile, client, keys, metadata);
        var document = SessionSyncDocument.FromJson($"{{\"custom\":\"{marker}\"}}");

        var first = await coordinator.UploadAsync("session-1", document);
        var second = await coordinator.UploadAsync("session-1", document);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal([1, 2], client.Uploads.Select(item => item.Revision));
        Assert.DoesNotContain(
            marker,
            client.Uploads[0].Envelope.Ciphertext,
            StringComparison.Ordinal);
        var decrypted = new AesGcmEnvelopeCodec().Decrypt(
            client.Uploads[0].Envelope,
            key,
            new EnvelopeBinding("team-1", "session-sync", "session-1", 1));
        Assert.Equal(document.ExportUtf8Json(), decrypted);
        Assert.Equal(2, metadata.Revisions["session-1"]);
        Assert.Equal(1, metadata.SaveProfileCount);
        Assert.DoesNotContain(marker, JsonSerializer.Serialize(metadata), StringComparison.Ordinal);
        Assert.Equal(1, keys.GetOrCreateCount);
    }

    [Fact]
    public async Task DownloadRejectsServerRevisionOlderThanPersistedRevision()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var envelope = new AesGcmEnvelopeCodec().Encrypt(
            "{}"u8,
            key,
            new EnvelopeBinding("team-1", "session-sync", "session-1", 2));
        var client = new RecordingCloudClient
        {
            SessionToDownload = new CloudSyncedSession(
                "session-1",
                2,
                envelope,
                DateTimeOffset.UtcNow),
        };
        var metadata = new RecordingCloudSyncMetadataStore();
        metadata.SeedRevision("session-1", 3);
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            new RecordingRecoveryKeyStore(key),
            metadata);

        var exception = await Assert.ThrowsAsync<CloudRollbackDetectedException>(
            () => coordinator.DownloadAsync("session-1"));

        Assert.Equal(3, exception.KnownRevision);
        Assert.Equal(2, exception.ServerRevision);
        Assert.Equal(3, metadata.Revisions["session-1"]);
    }

    [Fact]
    public async Task DownloadRejectsTamperedEnvelopeWithoutAdvancingMetadata()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var original = new AesGcmEnvelopeCodec().Encrypt(
            "{}"u8,
            key,
            new EnvelopeBinding("team-1", "session-sync", "session-1", 1));
        var bytes = Convert.FromBase64String(original.Ciphertext);
        bytes[0] ^= 0x01;
        var client = new RecordingCloudClient
        {
            SessionToDownload = new CloudSyncedSession(
                "session-1",
                1,
                new EncryptedEnvelope(
                    original.Algorithm,
                    original.Nonce,
                    Convert.ToBase64String(bytes)),
                DateTimeOffset.UtcNow),
        };
        var metadata = new RecordingCloudSyncMetadataStore();
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            new RecordingRecoveryKeyStore(key),
            metadata);

        await Assert.ThrowsAsync<EnvelopeAuthenticationException>(
            () => coordinator.DownloadAsync("session-1"));

        Assert.Empty(metadata.Revisions);
    }

    [Fact]
    public async Task DownloadConvergesADeletedSessionTombstoneFromAnotherDevice()
    {
        var metadata = new RecordingCloudSyncMetadataStore();
        metadata.SeedRevision("session-1", 1);
        var client = new RecordingCloudClient
        {
            GetSessionException = new CloudSessionDeletedException("session-1", 2),
        };
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            new RecordingRecoveryKeyStore(new byte[32]),
            metadata);

        var downloaded = await coordinator.DownloadAsync("session-1");

        Assert.Null(downloaded);
        Assert.Equal(2, metadata.Revisions["session-1"]);
    }

    [Fact]
    public async Task DeletePreservesRemoteTombstoneRevisionLocally()
    {
        var metadata = new RecordingCloudSyncMetadataStore();
        metadata.SeedRevision("session-1", 4);
        var client = new RecordingCloudClient();
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            new RecordingRecoveryKeyStore(new byte[32]),
            metadata);

        var tombstoneRevision = await coordinator.DeleteAsync("session-1");

        Assert.Equal([("session-1", 4)], client.DeletedSessions);
        Assert.Equal(5, tombstoneRevision);
        Assert.Equal(5, metadata.Revisions["session-1"]);
    }

    [Fact]
    public async Task DeletePersistsAnObservedRemoteRevisionBeforeAConflict()
    {
        var client = new RecordingCloudClient
        {
            SessionToDownload = new CloudSyncedSession(
                "session-1",
                4,
                new EncryptedEnvelope(
                    EncryptedEnvelope.Aes256GcmAlgorithm,
                    Convert.ToBase64String(new byte[12]),
                    Convert.ToBase64String(new byte[16])),
                DateTimeOffset.UtcNow),
            OnDelete = (_, _) =>
                throw CreateCloudClientException(CloudClientErrorKind.Conflict),
        };
        var metadata = new RecordingCloudSyncMetadataStore();
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            new RecordingRecoveryKeyStore(new byte[32]),
            metadata);

        await Assert.ThrowsAsync<CloudClientException>(
            () => coordinator.DeleteAsync("session-1"));

        Assert.Equal(4, metadata.Revisions["session-1"]);
        Assert.Equal([("session-1", 4)], client.DeletedSessions);
    }

    [Fact]
    public async Task ExportWritesDecryptedDocumentWithoutReturningSecretObjectState()
    {
        const string json = "{\"explicit\":\"exported content\"}";
        var key = RandomNumberGenerator.GetBytes(32);
        var envelope = new AesGcmEnvelopeCodec().Encrypt(
            Encoding.UTF8.GetBytes(json),
            key,
            new EnvelopeBinding("team-1", "session-sync", "session-1", 1));
        var client = new RecordingCloudClient
        {
            SessionToDownload = new CloudSyncedSession(
                "session-1",
                1,
                envelope,
                DateTimeOffset.UtcNow),
        };
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            new RecordingRecoveryKeyStore(key),
            new RecordingCloudSyncMetadataStore());
        await using var destination = new MemoryStream();

        var exported = await coordinator.ExportAsync("session-1", destination);

        Assert.True(exported);
        Assert.Equal(json, Encoding.UTF8.GetString(destination.ToArray()));
    }

    [Fact]
    public async Task CancellationStopsBeforeKeyOrNetworkAccess()
    {
        var client = new RecordingCloudClient();
        var keys = new RecordingRecoveryKeyStore(new byte[32]);
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            keys,
            new RecordingCloudSyncMetadataStore());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.UploadAsync(
                "session-1",
                SessionSyncDocument.FromJson("{}"),
                cancellation.Token));

        Assert.Equal(0, keys.GetOrCreateCount);
        Assert.Empty(client.Uploads);
    }

    [Fact]
    public async Task UploadCommitsRevisionAfterRemoteReceiptEvenWhenCallerCancels()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new RecordingCloudClient
        {
            OnUpload = (sessionId, revision, _) =>
            {
                cancellation.Cancel();
                return new CloudSessionWriteReceipt(sessionId, revision);
            },
        };
        var metadata = new RecordingCloudSyncMetadataStore();
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            new RecordingRecoveryKeyStore(new byte[32]),
            metadata);

        var revision = await coordinator.UploadAsync(
            "session-1",
            SessionSyncDocument.FromJson("{}"),
            cancellation.Token);

        Assert.Equal(1, revision);
        Assert.Equal(1, metadata.Revisions["session-1"]);
    }

    [Fact]
    public async Task UploadRecoversRemoteCommitAfterLocalRevisionSaveFailure()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var conflict = CreateCloudClientException(CloudClientErrorKind.Conflict);
        var client = new RecordingCloudClient();
        CloudSyncedSession? remote = null;
        client.OnUpload = (sessionId, revision, envelope) =>
        {
            if (remote is not null && revision != remote.Revision + 1)
            {
                throw conflict;
            }

            remote = new CloudSyncedSession(
                sessionId,
                revision,
                envelope,
                DateTimeOffset.UtcNow);
            client.SessionToDownload = remote;
            return new CloudSessionWriteReceipt(sessionId, revision);
        };
        var metadata = new RecordingCloudSyncMetadataStore
        {
            RemainingSaveRevisionFailures = 1,
        };
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            new RecordingRecoveryKeyStore(key),
            metadata);
        var document = SessionSyncDocument.FromJson("{\"state\":\"latest\"}");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.UploadAsync("session-1", document));
        var revision = await coordinator.UploadAsync("session-1", document);

        Assert.Equal(1, revision);
        Assert.Equal([1, 1], client.Uploads.Select(item => item.Revision));
        Assert.Equal(1, metadata.Revisions["session-1"]);
        var decrypted = new AesGcmEnvelopeCodec().Decrypt(
            remote!.Envelope,
            key,
            new EnvelopeBinding("team-1", "session-sync", "session-1", 1));
        Assert.Equal(document.ExportUtf8Json(), decrypted);
    }

    [Fact]
    public async Task UploadConflictRejectsUnauthenticatedRemoteRevisionWithoutRetrying()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var original = new AesGcmEnvelopeCodec().Encrypt(
            "{}"u8,
            key,
            new EnvelopeBinding("team-1", "session-sync", "session-1", 1));
        var ciphertext = Convert.FromBase64String(original.Ciphertext);
        ciphertext[0] ^= 0x01;
        var client = new RecordingCloudClient
        {
            SessionToDownload = new CloudSyncedSession(
                "session-1",
                1,
                new EncryptedEnvelope(
                    original.Algorithm,
                    original.Nonce,
                    Convert.ToBase64String(ciphertext)),
                DateTimeOffset.UtcNow),
            OnUpload = (_, _, _) =>
                throw CreateCloudClientException(CloudClientErrorKind.Conflict),
        };
        var metadata = new RecordingCloudSyncMetadataStore();
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            new RecordingRecoveryKeyStore(key),
            metadata);

        await Assert.ThrowsAsync<EnvelopeAuthenticationException>(
            () => coordinator.UploadAsync(
                "session-1",
                SessionSyncDocument.FromJson("{\"state\":\"latest\"}")));

        Assert.Single(client.Uploads);
        Assert.Empty(metadata.Revisions);
    }

    [Fact]
    public async Task UploadConflictPreservesRollbackProtection()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var remoteEnvelope = new AesGcmEnvelopeCodec().Encrypt(
            "{}"u8,
            key,
            new EnvelopeBinding("team-1", "session-sync", "session-1", 2));
        var client = new RecordingCloudClient
        {
            SessionToDownload = new CloudSyncedSession(
                "session-1",
                2,
                remoteEnvelope,
                DateTimeOffset.UtcNow),
            OnUpload = (_, _, _) =>
                throw CreateCloudClientException(CloudClientErrorKind.Conflict),
        };
        var metadata = new RecordingCloudSyncMetadataStore();
        metadata.SeedRevision("session-1", 3);
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            new RecordingRecoveryKeyStore(key),
            metadata);

        var exception = await Assert.ThrowsAsync<CloudRollbackDetectedException>(
            () => coordinator.UploadAsync(
                "session-1",
                SessionSyncDocument.FromJson("{\"state\":\"latest\"}")));

        Assert.Equal(3, exception.KnownRevision);
        Assert.Equal(2, exception.ServerRevision);
        Assert.Single(client.Uploads);
        Assert.Equal(3, metadata.Revisions["session-1"]);
    }

    [Fact]
    public async Task UploadConflictPreservesRevisionWhenRemoteSessionIsMissing()
    {
        var client = new RecordingCloudClient
        {
            OnUpload = (sessionId, revision, _) => revision == 1
                ? new CloudSessionWriteReceipt(sessionId, revision)
                : throw CreateCloudClientException(CloudClientErrorKind.Conflict),
        };
        var metadata = new RecordingCloudSyncMetadataStore();
        metadata.SeedRevision("session-1", 4);
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            new RecordingRecoveryKeyStore(new byte[32]),
            metadata);

        var exception = await Assert.ThrowsAsync<CloudSyncConflictException>(
            () => coordinator.UploadAsync(
            "session-1",
            SessionSyncDocument.FromJson("{}")));

        Assert.Equal(CloudSyncConflictKind.RemoteMissing, exception.Kind);
        Assert.Equal("session-1", exception.SessionId);
        Assert.Equal(4, exception.KnownRevision);
        Assert.Null(exception.RemoteRevision);
        Assert.Equal([5], client.Uploads.Select(item => item.Revision));
        Assert.Equal(4, metadata.Revisions["session-1"]);
    }

    [Fact]
    public async Task DownloadMissingSessionPreservesRollbackHighWaterMark()
    {
        var metadata = new RecordingCloudSyncMetadataStore();
        metadata.SeedRevision("session-1", 4);
        var coordinator = new CloudSyncCoordinator(
            Profile,
            new RecordingCloudClient(),
            new RecordingRecoveryKeyStore(new byte[32]),
            metadata);

        var downloaded = await coordinator.DownloadAsync("session-1");

        Assert.Null(downloaded);
        Assert.Equal(4, metadata.Revisions["session-1"]);
    }

    [Fact]
    public async Task UploadConflictRejectsDifferentAuthenticatedRemoteDocumentWithoutRetrying()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var remoteEnvelope = new AesGcmEnvelopeCodec().Encrypt(
            "{\"state\":\"remote\"}"u8,
            key,
            new EnvelopeBinding("team-1", "session-sync", "session-1", 2));
        var client = new RecordingCloudClient
        {
            SessionToDownload = new CloudSyncedSession(
                "session-1",
                2,
                remoteEnvelope,
                DateTimeOffset.UtcNow),
            OnUpload = (_, _, _) =>
                throw CreateCloudClientException(CloudClientErrorKind.Conflict),
        };
        var metadata = new RecordingCloudSyncMetadataStore();
        metadata.SeedRevision("session-1", 1);
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            new RecordingRecoveryKeyStore(key),
            metadata);

        var exception = await Assert.ThrowsAsync<CloudSyncConflictException>(
            () => coordinator.UploadAsync(
            "session-1",
            SessionSyncDocument.FromJson("{\"state\":\"local\"}")));

        Assert.Equal(CloudSyncConflictKind.RemoteDocumentChanged, exception.Kind);
        Assert.Equal(1, exception.KnownRevision);
        Assert.Equal(2, exception.RemoteRevision);
        Assert.Single(client.Uploads);
        Assert.Equal(1, metadata.Revisions["session-1"]);
    }

    [Fact]
    public async Task DeleteClearsRevisionAfterRemoteDeleteEvenWhenCallerCancels()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new RecordingCloudClient
        {
            OnDelete = (sessionId, knownRevision) =>
            {
                cancellation.Cancel();
                return new CloudSessionDeleteReceipt(sessionId, knownRevision + 1);
            },
        };
        var metadata = new RecordingCloudSyncMetadataStore();
        metadata.SeedRevision("session-1", 4);
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            new RecordingRecoveryKeyStore(new byte[32]),
            metadata);

        await coordinator.DeleteAsync("session-1", cancellation.Token);

        Assert.Equal(5, metadata.Revisions["session-1"]);
    }

    [Fact]
    public async Task DeleteRetryCommitsTombstoneAfterPreviousLocalSaveFailure()
    {
        var client = new RecordingCloudClient();
        var metadata = new RecordingCloudSyncMetadataStore
        {
            RemainingSaveRevisionFailures = 1,
        };
        metadata.SeedRevision("session-1", 4);
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            new RecordingRecoveryKeyStore(new byte[32]),
            metadata);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.DeleteAsync("session-1"));
        await coordinator.DeleteAsync("session-1");

        Assert.Equal([("session-1", 4), ("session-1", 4)], client.DeletedSessions);
        Assert.Equal(5, metadata.Revisions["session-1"]);
    }

    [Fact]
    public async Task DeleteRemoteNotFoundPreservesPersistedRevision()
    {
        var client = new RecordingCloudClient
        {
            OnDelete = (_, _) =>
                throw CreateCloudClientException(CloudClientErrorKind.NotFound),
        };
        var metadata = new RecordingCloudSyncMetadataStore();
        metadata.SeedRevision("session-1", 4);
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            new RecordingRecoveryKeyStore(new byte[32]),
            metadata);

        var revision = await coordinator.DeleteAsync("session-1");

        Assert.Null(revision);
        Assert.Equal(4, metadata.Revisions["session-1"]);
    }

    [Fact]
    public async Task ConfiguredDocumentLimitRejectsUploadBeforeEncryption()
    {
        var client = new RecordingCloudClient();
        var keys = new RecordingRecoveryKeyStore(new byte[32]);
        var coordinator = new CloudSyncCoordinator(
            Profile,
            client,
            keys,
            new RecordingCloudSyncMetadataStore(),
            new CloudSyncCoordinatorOptions(maximumDocumentBytes: 16));
        var document = SessionSyncDocument.FromJson(
            "{\"explicit\":\"content larger than sixteen bytes\"}");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => coordinator.UploadAsync("session-1", document));

        Assert.Equal(0, keys.GetOrCreateCount);
        Assert.Empty(client.Uploads);
    }

    [Fact]
    public async Task LocalOnlyDefaultRejectsRemoteOperations()
    {
        var coordinator = new CloudSyncCoordinator(
            new CloudConnectionProfile(),
            cloudClient: null,
            new RecordingRecoveryKeyStore(new byte[32]),
            new RecordingCloudSyncMetadataStore());

        await Assert.ThrowsAsync<CloudSyncUnavailableException>(
            () => coordinator.DownloadAsync("session-1"));
    }

    private static CloudClientException CreateCloudClientException(CloudClientErrorKind kind)
    {
        var constructor = typeof(CloudClientException)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        return (CloudClientException)constructor.Invoke(
            [kind, "The simulated cloud request failed.", null, null]);
    }
}
