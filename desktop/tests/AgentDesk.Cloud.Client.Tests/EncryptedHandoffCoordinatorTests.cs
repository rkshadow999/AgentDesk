using System.Security.Cryptography;
using System.Text;
using AgentDesk.Cloud.Client;

namespace AgentDesk.Cloud.Client.Tests;

public sealed class EncryptedHandoffCoordinatorTests
{
    private static readonly CloudConnectionProfile SourceProfile = new(
        new Uri("https://cloud.agentdesk.example/"),
        "team-1",
        "device-1");

    private static readonly CloudConnectionProfile TargetProfile = new(
        new Uri("https://cloud.agentdesk.example/"),
        "team-1",
        "device-2");

    [Fact]
    public async Task CreateEncryptsExplicitDocumentForTargetDeviceBinding()
    {
        const string marker = "explicit-handoff-content";
        var key = RandomNumberGenerator.GetBytes(32);
        var client = new RecordingCloudClient();
        var keys = new RecordingRecoveryKeyStore(key);
        var coordinator = new EncryptedHandoffCoordinator(SourceProfile, client, keys);
        var document = SessionSyncDocument.FromJson($"{{\"custom\":\"{marker}\"}}");

        var created = await coordinator.CreateAsync("device-2", "session-1", document);

        Assert.Equal("device-2", created.TargetDeviceId);
        Assert.Equal("session-1", created.SessionId);
        Assert.DoesNotContain(marker, created.Envelope.Ciphertext, StringComparison.Ordinal);
        var decrypted = new AesGcmEnvelopeCodec().Decrypt(
            created.Envelope,
            key,
            new HandoffEnvelopeBinding(
                "team-1",
                "device-1",
                "device-2",
                "session-1",
                created.HandoffId,
                1));
        Assert.Equal(document.ExportUtf8Json(), decrypted);
        Assert.Equal(1, keys.GetOrCreateCount);
    }

    [Fact]
    public async Task ListDecryptsIncomingHandoffWithExistingSharedRecoveryKey()
    {
        const string json = "{\"explicit\":\"handoff content\"}";
        var key = RandomNumberGenerator.GetBytes(32);
        var envelope = new AesGcmEnvelopeCodec().Encrypt(
            Encoding.UTF8.GetBytes(json),
            key,
            new HandoffEnvelopeBinding(
                "team-1",
                "device-1",
                "device-2",
                "session-1",
                "handoff-1",
                1));
        var client = new RecordingCloudClient();
        client.Handoffs.Add(new CloudHandoff(
            "handoff-1",
            "device-1",
            "device-2",
            "session-1",
            envelope,
            DateTimeOffset.UtcNow));
        var keys = new RecordingRecoveryKeyStore(RandomNumberGenerator.GetBytes(32));
        keys.Save(RecoveryKeyReference.ForTeam("team-1"), key);
        var coordinator = new EncryptedHandoffCoordinator(TargetProfile, client, keys);

        var incoming = await coordinator.ListAsync();

        var handoff = Assert.Single(incoming);
        Assert.Equal("handoff-1", handoff.HandoffId);
        Assert.Equal("device-1", handoff.SourceDeviceId);
        Assert.Equal(json, Encoding.UTF8.GetString(handoff.Document.ExportUtf8Json()));
        Assert.DoesNotContain("handoff content", handoff.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, keys.ReadCount);
        Assert.Equal(0, keys.GetOrCreateCount);
    }

    [Fact]
    public async Task ListFailsWithoutGeneratingAReplacementRecoveryKey()
    {
        var client = new RecordingCloudClient();
        client.Handoffs.Add(CreateIncomingHandoff(RandomNumberGenerator.GetBytes(32)));
        var keys = new RecordingRecoveryKeyStore(RandomNumberGenerator.GetBytes(32));
        var coordinator = new EncryptedHandoffCoordinator(TargetProfile, client, keys);

        await Assert.ThrowsAsync<CloudRecoveryKeyUnavailableException>(
            () => coordinator.ListAsync());

        Assert.Equal(2, keys.ReadCount);
        Assert.Equal(0, keys.GetOrCreateCount);
    }

    [Fact]
    public async Task ListRejectsTamperedHandoff()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var original = CreateIncomingHandoff(key);
        var ciphertext = Convert.FromBase64String(original.Envelope.Ciphertext);
        ciphertext[0] ^= 0x01;
        var client = new RecordingCloudClient();
        client.Handoffs.Add(new CloudHandoff(
            original.HandoffId,
            original.SourceDeviceId,
            original.TargetDeviceId,
            original.SessionId,
            new EncryptedEnvelope(
                original.Envelope.Algorithm,
                original.Envelope.Nonce,
                Convert.ToBase64String(ciphertext)),
            original.CreatedAt));
        var keys = new RecordingRecoveryKeyStore(RandomNumberGenerator.GetBytes(32));
        keys.Save(RecoveryKeyReference.ForTeam("team-1"), key);
        var coordinator = new EncryptedHandoffCoordinator(TargetProfile, client, keys);

        await Assert.ThrowsAsync<EnvelopeAuthenticationException>(
            () => coordinator.ListAsync());
    }

    [Theory]
    [InlineData("handoff-swapped", "device-1")]
    [InlineData("handoff-1", "device-attacker")]
    public async Task ListRejectsHandoffWhoseIdentityMetadataWasSwapped(
        string handoffId,
        string sourceDeviceId)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var sourceClient = new RecordingCloudClient();
        var sourceKeys = new RecordingRecoveryKeyStore(key);
        var source = new EncryptedHandoffCoordinator(SourceProfile, sourceClient, sourceKeys);
        var original = await source.CreateAsync(
            "device-2",
            "session-1",
            SessionSyncDocument.FromJson("{\"message\":\"bound handoff\"}"));
        var targetClient = new RecordingCloudClient();
        targetClient.Handoffs.Add(new CloudHandoff(
            handoffId,
            sourceDeviceId,
            original.TargetDeviceId,
            original.SessionId,
            original.Envelope,
            original.CreatedAt));
        var targetKeys = new RecordingRecoveryKeyStore(RandomNumberGenerator.GetBytes(32));
        targetKeys.Save(RecoveryKeyReference.ForTeam("team-1"), key);
        var target = new EncryptedHandoffCoordinator(TargetProfile, targetClient, targetKeys);

        await Assert.ThrowsAsync<EnvelopeAuthenticationException>(() => target.ListAsync());
    }

    [Fact]
    public async Task CreateRejectsConfiguredDocumentLimitBeforeKeyOrNetworkAccess()
    {
        var client = new RecordingCloudClient();
        var keys = new RecordingRecoveryKeyStore(new byte[32]);
        var coordinator = new EncryptedHandoffCoordinator(
            SourceProfile,
            client,
            keys,
            new EncryptedHandoffCoordinatorOptions(maximumDocumentBytes: 16));
        var document = SessionSyncDocument.FromJson(
            "{\"explicit\":\"content larger than sixteen bytes\"}");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => coordinator.CreateAsync("device-2", "session-1", document));

        Assert.Equal(0, keys.GetOrCreateCount);
        Assert.Empty(client.Handoffs);
    }

    [Fact]
    public async Task CancellationStopsBeforeKeyOrNetworkAccess()
    {
        var client = new RecordingCloudClient();
        var keys = new RecordingRecoveryKeyStore(new byte[32]);
        var coordinator = new EncryptedHandoffCoordinator(SourceProfile, client, keys);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.CreateAsync(
                "device-2",
                "session-1",
                SessionSyncDocument.FromJson("{}"),
                cancellation.Token));

        Assert.Equal(0, keys.GetOrCreateCount);
        Assert.Empty(client.Handoffs);
    }

    [Fact]
    public async Task AcknowledgeRemovesProcessedHandoff()
    {
        var client = new RecordingCloudClient();
        client.Handoffs.Add(CreateIncomingHandoff(RandomNumberGenerator.GetBytes(32)));
        var coordinator = new EncryptedHandoffCoordinator(
            TargetProfile,
            client,
            new RecordingRecoveryKeyStore(new byte[32]));

        var acknowledged = await coordinator.AcknowledgeAsync("handoff-1");

        Assert.True(acknowledged);
        Assert.Empty(client.Handoffs);
    }

    private static CloudHandoff CreateIncomingHandoff(byte[] key)
    {
        var envelope = new AesGcmEnvelopeCodec().Encrypt(
            "{}"u8,
            key,
            new HandoffEnvelopeBinding(
                "team-1",
                "device-1",
                "device-2",
                "session-1",
                "handoff-1",
                1));
        return new CloudHandoff(
            "handoff-1",
            "device-1",
            "device-2",
            "session-1",
            envelope,
            DateTimeOffset.UtcNow);
    }
}
