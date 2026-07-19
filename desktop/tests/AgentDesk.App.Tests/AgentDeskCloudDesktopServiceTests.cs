using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AgentDesk.App.Cloud;
using AgentDesk.Cloud.Client;
using AgentDesk.Core.Engine;
using AgentDesk.Core.Security;

namespace AgentDesk.App.Tests;

public sealed class AgentDeskCloudDesktopServiceTests
{
    private static readonly Uri RemoteEndpoint = new("https://cloud.example.test");

    [Fact]
    public async Task LoadProfileDefaultsToLocalOnlyWithoutReadingCredentials()
    {
        var fixture = new Fixture();

        var snapshot = await fixture.Service.LoadProfileAsync();

        Assert.True(snapshot.Profile.IsLocalOnly);
        Assert.False(snapshot.HasAccessToken);
        Assert.Equal(0, fixture.Credentials.ReadCount);
    }

    [Fact]
    public async Task SaveRemoteProfileRejectsNonLoopbackHttpBeforePersistingSecrets()
    {
        var fixture = new Fixture();
        const string token = "test-token-must-not-leak";

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.SaveRemoteProfileAsync(
                new Uri("http://cloud.example.test"),
                "team-1",
                "device-1",
                token));

        Assert.True(fixture.ProfileStore.Profile.IsLocalOnly);
        Assert.Empty(fixture.Credentials.Values);
        Assert.DoesNotContain(token, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAndLoadRemoteProfileKeepsTokenOutOfSnapshotsAndToString()
    {
        var fixture = new Fixture();
        const string token = "test-token-must-not-leak";

        var saved = await fixture.Service.SaveRemoteProfileAsync(
            RemoteEndpoint,
            "team-1",
            "device-1",
            token);
        var loaded = await fixture.Service.LoadProfileAsync();

        Assert.False(saved.Profile.IsLocalOnly);
        Assert.True(saved.HasAccessToken);
        Assert.True(loaded.HasAccessToken);
        Assert.Equal(RemoteEndpoint.AbsoluteUri, loaded.Profile.BaseUri!.AbsoluteUri);
        Assert.Contains(token, fixture.Credentials.Values);
        Assert.DoesNotContain(token, saved.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(token, loaded.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(token, fixture.Service.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveLocalOnlyDeletesThePreviouslyConfiguredAccessToken()
    {
        var fixture = new Fixture();
        await fixture.Service.SaveRemoteProfileAsync(
            RemoteEndpoint,
            "team-1",
            "device-1",
            "test-token");

        var snapshot = await fixture.Service.SaveLocalOnlyProfileAsync();

        Assert.True(snapshot.Profile.IsLocalOnly);
        Assert.False(snapshot.HasAccessToken);
        Assert.Empty(fixture.Credentials.Values);
    }

    [Fact]
    public async Task SaveLocalOnlyKeepsRemoteProfileAndTokenWhenProfilePersistenceFails()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        fixture.ProfileStore.SaveException = new IOException("profile write failed");

        await Assert.ThrowsAsync<IOException>(
            () => fixture.Service.SaveLocalOnlyProfileAsync());

        Assert.False(fixture.ProfileStore.Profile.IsLocalOnly);
        Assert.Contains("test-token", fixture.Credentials.Values);
    }

    [Fact]
    public async Task SaveLocalOnlySucceedsWhenSupersededTokenCleanupFails()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        fixture.Credentials.DeleteException = new IOException("credential cleanup failed");

        var snapshot = await fixture.Service.SaveLocalOnlyProfileAsync();

        Assert.True(snapshot.Profile.IsLocalOnly);
        Assert.True(fixture.ProfileStore.Profile.IsLocalOnly);
        Assert.Contains("test-token", fixture.Credentials.Values);
        Assert.DoesNotContain(
            "credential cleanup failed",
            snapshot.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveRemoteProfileRollsBackANewTokenWhenProfilePersistenceFails()
    {
        var fixture = new Fixture();
        fixture.ProfileStore.SaveException = new IOException("profile write failed");
        const string token = "test-token-must-not-leak";

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            fixture.Service.SaveRemoteProfileAsync(
                RemoteEndpoint,
                "team-1",
                "device-1",
                token));

        Assert.Empty(fixture.Credentials.Values);
        Assert.DoesNotContain(token, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalOnlyModeRejectsCloudCallsBeforeInvokingTheClient()
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<AgentDeskCloudUnavailableException>(
            () => fixture.Service.GetPolicyAsync());

        Assert.Equal(0, fixture.CloudClient.GetPolicyCount);
    }

    [Fact]
    public async Task RemoteOperationsFailClosedWhenTheStoredTokenIsMissing()
    {
        var fixture = new Fixture();
        fixture.ProfileStore.Profile = new CloudConnectionProfile(
            RemoteEndpoint,
            "team-1",
            "device-1");

        var exception = await Assert.ThrowsAsync<CloudAccessTokenStoreException>(
            () => fixture.Service.GetPolicyAsync());

        Assert.Equal(0, fixture.CloudClient.GetPolicyCount);
        Assert.DoesNotContain(
            "test-token-must-not-leak",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionUploadAndDownloadUseTheEngineWorkflow()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        var engine = new RecordingEngineClient
        {
            ExportedDocument = EngineSessionDocument.FromJson("{\"session\":\"local\"}"),
            ImportedSessionId = new SessionId("session-imported"),
        };

        var revision = await fixture.Service.UploadSessionAsync(
            engine,
            new SessionId("session-remote"));
        fixture.CloudClient.SessionToDownload = new CloudSyncedSession(
            fixture.CloudClient.Uploads[0].SessionId,
            fixture.CloudClient.Uploads[0].Revision,
            fixture.CloudClient.Uploads[0].Envelope,
            DateTimeOffset.UtcNow);
        var imported = await fixture.Service.DownloadAndImportSessionAsync(
            engine,
            "session-remote",
            "C:\\work\\project");

        Assert.Equal(1, revision);
        Assert.NotNull(imported);
        Assert.Equal("session-imported", imported.ImportedSessionId.Value);
        Assert.Equal("C:\\work\\project", engine.ImportWorkingDirectory);
        Assert.Equal(["session-remote"], engine.ExportedSessionIds);
    }

    [Fact]
    public async Task SessionDeleteUsesTheKnownRevisionAndReturnsTheTombstoneHighWater()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        await fixture.MetadataStore.SaveProfileAsync(fixture.ProfileStore.Profile);
        var scope = CloudSyncMetadataScope.FromProfile(fixture.ProfileStore.Profile);
        await fixture.MetadataStore.SaveRevisionAsync(scope, "session-delete", 4);

        var revision = await fixture.Service.DeleteSessionAsync("session-delete");

        Assert.Equal(5, revision);
        Assert.Equal(("session-delete", 4), fixture.CloudClient.DeletedSession);
        Assert.Equal(5, await fixture.MetadataStore.ReadRevisionAsync(scope, "session-delete"));
    }

    [Fact]
    public async Task SessionExportReturnsTheEngineDocumentWithoutCloudSerialization()
    {
        var fixture = new Fixture();
        var document = EngineSessionDocument.FromJson(
            "{\"private\":\"must-stay-native\"}");
        var engine = new RecordingEngineClient { ExportedDocument = document };

        var exported = await fixture.Service.ExportSessionAsync(
            engine,
            new SessionId("session-export"));

        Assert.Same(document, exported);
        Assert.Equal(["session-export"], engine.ExportedSessionIds);
        Assert.DoesNotContain("must-stay-native", fixture.Service.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentUploadsReuseOneRuntimeAndAdvanceRevisionsSerially()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        var firstUploadEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstUpload = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var uploadCalls = 0;
        fixture.CloudClient.BeforeUploadAsync = async () =>
        {
            if (Interlocked.Increment(ref uploadCalls) == 1)
            {
                firstUploadEntered.TrySetResult();
                await releaseFirstUpload.Task;
            }
        };
        var engine = new RecordingEngineClient
        {
            ExportedDocument = EngineSessionDocument.FromJson("{\"session\":\"local\"}"),
        };
        var sessionId = new SessionId("session-concurrent");

        var first = fixture.Service.UploadSessionAsync(engine, sessionId);
        await firstUploadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = fixture.Service.UploadSessionAsync(engine, sessionId);
        await Task.Delay(50);
        releaseFirstUpload.TrySetResult();
        var revisions = await Task.WhenAll(first, second);

        Assert.Equal([1, 2], revisions.Order().ToArray());
        Assert.Equal([1, 2], fixture.CloudClient.Uploads
            .Select(upload => upload.Revision)
            .Order()
            .ToArray());
    }

    [Fact]
    public async Task ConcurrentFirstUploadsCreateOneSharedRecoveryKey()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        fixture.Credentials.CoordinateFirstRecoveryKeyReads = true;
        var exportsEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var exportCount = 0;
        var engine = new RecordingEngineClient
        {
            ExportedDocument = EngineSessionDocument.FromJson("{\"session\":\"local\"}"),
            BeforeExportAsync = () =>
            {
                if (Interlocked.Increment(ref exportCount) == 2)
                {
                    exportsEntered.TrySetResult();
                }
                return exportsEntered.Task;
            },
        };

        var uploads = await Task.WhenAll(
            fixture.Service.UploadSessionAsync(engine, new SessionId("session-first")),
            fixture.Service.UploadSessionAsync(engine, new SessionId("session-second")));

        Assert.Equal([1, 1], uploads);
        Assert.Equal(1, fixture.Credentials.RecoveryKeySaveCount);
    }

    [Fact]
    public async Task HandoffIsAcknowledgedOnlyAfterTheEngineImportsIt()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        var sourceEngine = new RecordingEngineClient
        {
            ExportedDocument = EngineSessionDocument.FromJson("{\"handoff\":true}"),
        };
        await fixture.Service.CreateHandoffAsync(
            sourceEngine,
            new SessionId("session-1"),
            "device-1");
        var targetEngine = new RecordingEngineClient
        {
            ImportedSessionId = new SessionId("session-2"),
        };

        var received = await fixture.Service.ReceiveHandoffsAsync(
            targetEngine,
            "C:\\work\\target");

        var item = Assert.Single(received);
        Assert.Equal("session-2", item.ImportedSessionId.Value);
        Assert.Empty(fixture.CloudClient.Handoffs);
    }

    [Fact]
    public async Task ProtectedRecoveryPackagePairsTwoDevicesForEncryptedHandoff()
    {
        var cloudClient = new RecordingCloudClient();
        var source = new Fixture(cloudClient);
        var target = new Fixture(cloudClient);
        await source.Service.SaveRemoteProfileAsync(
            RemoteEndpoint,
            "team-1",
            "device-source",
            "source-token");
        await target.Service.SaveRemoteProfileAsync(
            RemoteEndpoint,
            "team-1",
            "device-target",
            "target-token");
        cloudClient.SourceDeviceId = "device-source";
        var passphrase = "correct horse battery staple".AsMemory();
        var sourceEngine = new RecordingEngineClient
        {
            ExportedDocument = EngineSessionDocument.FromJson("{\"handoff\":true}"),
        };
        var targetEngine = new RecordingEngineClient
        {
            ImportedSessionId = new SessionId("session-imported"),
        };

        var package = await source.Service
            .ExportRecoveryKeyPairingPackageAsync(passphrase);
        var packageBytes = package.ExportBytes();
        var recoveryKey = source.Credentials.Values
            .Select(TryDecodeRecoveryKey)
            .Single(value => value is not null)!;
        Assert.False(packageBytes.AsSpan().IndexOf(recoveryKey) >= 0);
        var transferredPackage = RecoveryKeyPairingPackage.FromBytes(packageBytes);
        await target.Service.ImportRecoveryKeyPairingPackageAsync(
            transferredPackage,
            passphrase);
        await source.Service.CreateHandoffAsync(
            sourceEngine,
            new SessionId("session-source"),
            "device-target");
        var received = await target.Service.ReceiveHandoffsAsync(
            targetEngine,
            "C:\\work\\target");

        Assert.Single(received);
        Assert.Equal("session-imported", received[0].ImportedSessionId.Value);
        Assert.DoesNotContain(
            passphrase.ToString(),
            package.ToString(),
            StringComparison.Ordinal);
        var json = JsonSerializer.Serialize(package);
        Assert.DoesNotContain(
            Convert.ToBase64String(package.ExportBytes()),
            json,
            StringComparison.Ordinal);
        CryptographicOperations.ZeroMemory(recoveryKey);
    }

    [Fact]
    public async Task RecoveryPackageWrongPassphraseDoesNotReplaceExistingKeyOrLeakSecrets()
    {
        var source = await Fixture.CreateRemoteAsync();
        var target = await Fixture.CreateRemoteAsync();
        var sourceEngine = new RecordingEngineClient
        {
            ExportedDocument = EngineSessionDocument.FromJson("{\"handoff\":true}"),
        };
        await target.Service.CreateHandoffAsync(
            sourceEngine,
            new SessionId("target-key-seed"),
            "device-elsewhere");
        var before = target.Credentials.Values.Order().ToArray();
        var package = await source.Service.ExportRecoveryKeyPairingPackageAsync(
            "right-password-value".AsMemory());

        var exception = await Assert.ThrowsAsync<RecoveryKeyPairingException>(() =>
            target.Service.ImportRecoveryKeyPairingPackageAsync(
                package,
                "wrong-password-value".AsMemory()));

        Assert.Equal(before, target.Credentials.Values.Order().ToArray());
        Assert.DoesNotContain(
            "right-password-value",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "wrong-password-value",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToBase64String(package.ExportBytes()),
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PolicyRunnerAndAutomationOperationsDelegateToTheCloudClient()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        var update = new CloudTeamPolicyUpdate(
            ["WslStrict"],
            remoteRunnerEnabled: true,
            uiAutomationEnabled: false,
            maximumConcurrentJobs: 2,
            allowedPluginPublishers: ["publisher-1"]);
        var envelope = new EncryptedEnvelope(
            EncryptedEnvelope.Aes256GcmAlgorithm,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        _ = await fixture.Service.GetPolicyAsync();
        _ = await fixture.Service.UpdatePolicyAsync(update);
        await fixture.Service.RegisterRunnerAsync("runner-1", ["windows"]);
        var queued = await fixture.Service.QueueRunnerJobAsync("windows", envelope);
        var claimed = await fixture.Service.ClaimRunnerJobAsync("runner-1", 30);
        await fixture.Service.CompleteRunnerJobAsync(
            claimed!.ClaimHandle,
            claimed.JobId,
            envelope);
        var automation = await fixture.Service.CreateAutomationAsync(
            "nightly",
            3600,
            "windows",
            envelope);
        var automations = await fixture.Service.ListAutomationsAsync();
        var disabled = await fixture.Service.DisableAutomationAsync("automation-1");

        Assert.Equal(1, fixture.CloudClient.GetPolicyCount);
        Assert.Same(update, fixture.CloudClient.PolicyUpdate);
        Assert.Equal(("runner-1", new[] { "windows" }), fixture.CloudClient.RunnerRegistration);
        Assert.False(string.IsNullOrWhiteSpace(queued.JobId));
        Assert.NotNull(claimed);
        Assert.Equal(queued.JobId, claimed.JobId);
        Assert.Same(claimed.Identity, fixture.CloudClient.CompletedIdentity);
        Assert.False(string.IsNullOrWhiteSpace(automation.AutomationId));
        Assert.Single(automations);
        Assert.True(disabled);
    }

    [Fact]
    public async Task FailedRunnerCompletionKeepsClaimHandleAvailableForRetry()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        var envelope = CreateEncryptedEnvelope();
        var queued = await fixture.Service.QueueRunnerJobAsync("windows", envelope);
        var claimed = await fixture.Service.ClaimRunnerJobAsync("runner-1", 30);
        fixture.CloudClient.CompleteJobException = new IOException("runner completion failed");

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.CompleteRunnerJobAsync(
            claimed!.ClaimHandle,
            queued.JobId,
            envelope));

        fixture.CloudClient.CompleteJobException = null;
        await fixture.Service.CompleteRunnerJobAsync(
            claimed!.ClaimHandle,
            queued.JobId,
            envelope);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CompleteRunnerJobAsync(
                claimed.ClaimHandle,
                queued.JobId,
                envelope));
        Assert.Equal(2, fixture.CloudClient.CompleteJobCallCount);
    }

    [Fact]
    public async Task MismatchedRunnerJobIdDoesNotConsumeClaimHandle()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        var envelope = CreateEncryptedEnvelope();
        var queued = await fixture.Service.QueueRunnerJobAsync("windows", envelope);
        var claimed = await fixture.Service.ClaimRunnerJobAsync("runner-1", 30);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CompleteRunnerJobAsync(
                claimed!.ClaimHandle,
                "different-job",
                envelope));
        await fixture.Service.CompleteRunnerJobAsync(
            claimed!.ClaimHandle,
            queued.JobId,
            envelope);

        Assert.Equal(1, fixture.CloudClient.CompleteJobCallCount);
    }

    [Fact]
    public async Task ExpiredRunnerClaimCannotBeCompleted()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        var envelope = CreateEncryptedEnvelope();
        fixture.CloudClient.ClaimLeaseExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(250);
        var queued = await fixture.Service.QueueRunnerJobAsync("windows", envelope);
        var claimed = await fixture.Service.ClaimRunnerJobAsync("runner-1", 30);
        var remaining = claimed!.Job.LeaseExpiresAt - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining + TimeSpan.FromMilliseconds(100));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CompleteRunnerJobAsync(
                claimed.ClaimHandle,
                queued.JobId,
                envelope));
        Assert.Equal(0, fixture.CloudClient.CompleteJobCallCount);
    }

    [Fact]
    public async Task ActiveRunnerClaimsHaveAHardLimit()
    {
        const int maximumActiveClaims = 1024;
        var fixture = await Fixture.CreateRemoteAsync();
        var envelope = CreateEncryptedEnvelope();
        _ = await fixture.Service.QueueRunnerJobAsync("windows", envelope);

        for (var index = 0; index < maximumActiveClaims; index++)
        {
            Assert.NotNull(await fixture.Service.ClaimRunnerJobAsync("runner-1", 30));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ClaimRunnerJobAsync("runner-1", 30));
        Assert.Equal(maximumActiveClaims, fixture.CloudClient.ClaimJobCallCount);
    }

    [Fact]
    public async Task RunnerTasksAndAutomationsEncryptPayloadsInsideTheDesktopService()
    {
        var fixture = await Fixture.CreateRemoteAsync();

        var queued = await fixture.Service.QueueRunnerTaskAsync(
            "windows",
            "inspect the active worktree");
        var claimed = await fixture.Service.ClaimRunnerTaskAsync("runner-1", 30);
        await fixture.Service.CompleteRunnerTaskAsync(
            claimed!.ClaimHandle,
            queued.JobId,
            "review completed without findings");
        var automation = await fixture.Service.CreateAutomationTaskAsync(
            "nightly review",
            3600,
            "windows",
            "review the default branch");

        Assert.False(string.IsNullOrWhiteSpace(queued.JobId));
        Assert.NotNull(claimed);
        Assert.Equal(queued.JobId, claimed.JobId);
        Assert.Equal("inspect the active worktree", claimed.Task);
        Assert.Equal("windows", claimed.RequiredCapability);
        Assert.False(string.IsNullOrWhiteSpace(automation.AutomationId));
        Assert.NotNull(fixture.CloudClient.QueuedEnvelope);
        Assert.NotNull(fixture.CloudClient.CompletedEnvelope);
        Assert.NotNull(fixture.CloudClient.AutomationEnvelope);
        var serializedEnvelopes = string.Join(
            '\n',
            fixture.CloudClient.QueuedEnvelope,
            fixture.CloudClient.CompletedEnvelope,
            fixture.CloudClient.AutomationEnvelope);
        Assert.DoesNotContain("inspect the active worktree", serializedEnvelopes, StringComparison.Ordinal);
        Assert.DoesNotContain("review completed", serializedEnvelopes, StringComparison.Ordinal);
        Assert.DoesNotContain("review the default branch", serializedEnvelopes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OlderClaimCannotBorrowTheLatestIdentityForTheSameJob()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        _ = await fixture.Service.QueueRunnerTaskAsync(
            "windows",
            "inspect the active worktree");
        var first = await fixture.Service.ClaimRunnerTaskAsync("runner-1", 30);
        var second = await fixture.Service.ClaimRunnerTaskAsync("runner-1", 30);

        await fixture.Service.CompleteRunnerTaskAsync(
            first!.ClaimHandle,
            first!.JobId,
            "result from the first execution");

        Assert.NotEqual(first.ClaimHandle, second!.ClaimHandle);
        Assert.Same(first.Identity, fixture.CloudClient.CompletedIdentity);
        Assert.NotSame(second.Identity, fixture.CloudClient.CompletedIdentity);
    }

    [Fact]
    public async Task RunnerTaskToStringDoesNotExposePlaintext()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        const string plaintext = "private runner task that must stay out of logs";
        _ = await fixture.Service.QueueRunnerTaskAsync("windows", plaintext);

        var claimed = await fixture.Service.ClaimRunnerTaskAsync("runner-1", 30);

        Assert.DoesNotContain(plaintext, claimed!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClaimedRunnerTaskRejectsAJobIdentitySwap()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        _ = await fixture.Service.QueueRunnerTaskAsync(
            "windows",
            "inspect the active worktree");
        fixture.CloudClient.ClaimedJobId = "job-swapped";

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Service.ClaimRunnerTaskAsync("runner-1", 30));
    }

    [Fact]
    public async Task ClaimedRunnerTaskRejectsARequiredCapabilitySwap()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        _ = await fixture.Service.QueueRunnerTaskAsync(
            "windows",
            "inspect the active worktree");
        fixture.CloudClient.ClaimedRequiredCapability = "wsl";

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Service.ClaimRunnerTaskAsync("runner-1", 30));
    }

    [Fact]
    public async Task AutomationTemplateAndRunResultRejectIdentityReplay()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        var automation = await fixture.Service.CreateAutomationTaskAsync(
            "nightly review",
            3600,
            "windows",
            "review the default branch");
        fixture.CloudClient.ClaimEnvelope = fixture.CloudClient.AutomationEnvelope;
        fixture.CloudClient.ClaimedJobId = "job-automation-1";
        fixture.CloudClient.ClaimedKind = CloudRunnerPayloadKinds.Automation;
        fixture.CloudClient.ClaimedAutomationId = automation.AutomationId;
        fixture.CloudClient.ClaimedRunId = "run-automation-1";

        var claimed = await fixture.Service.ClaimRunnerTaskAsync("runner-1", 30);
        await fixture.Service.CompleteRunnerTaskAsync(
            claimed!.ClaimHandle,
            claimed!.JobId,
            "automation completed");

        var codec = new AgentDeskCloudTaskPayloadCodec();
        var recoveryKeys = new CredentialRecoveryKeyStore(fixture.Credentials);
        Assert.Equal(
            "automation completed",
            codec.UnprotectResult(
                fixture.ProfileStore.Profile,
                recoveryKeys,
                claimed.Identity,
                fixture.CloudClient.CompletedEnvelope!));
        var replayedIdentity = new CloudRunnerJobIdentity(
            claimed.JobId,
            claimed.Kind,
            claimed.RequiredCapability,
            claimed.AutomationId,
            "run-automation-replayed");
        Assert.Throws<InvalidDataException>(() => codec.UnprotectResult(
            fixture.ProfileStore.Profile,
            recoveryKeys,
            replayedIdentity,
            fixture.CloudClient.CompletedEnvelope!));
    }

    [Fact]
    public async Task RunnerResultEnvelopeRejectsLeaseGenerationReplay()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        var codec = new AgentDeskCloudTaskPayloadCodec();
        var recoveryKeys = new CredentialRecoveryKeyStore(fixture.Credentials);
        var first = CreateClaimedIdentity("job-generation-aad", leaseGeneration: 1);
        var second = CreateClaimedIdentity("job-generation-aad", leaseGeneration: 2);
        var envelope = codec.ProtectResult(
            fixture.ProfileStore.Profile,
            recoveryKeys,
            first,
            "generation one result");

        Assert.Throws<InvalidDataException>(() => codec.UnprotectResult(
            fixture.ProfileStore.Profile,
            recoveryKeys,
            second,
            envelope));
    }

    [Fact]
    public async Task RunnerTaskEnvelopeRemainsReplayableForALegalNewLeaseGeneration()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        var codec = new AgentDeskCloudTaskPayloadCodec();
        var recoveryKeys = new CredentialRecoveryKeyStore(fixture.Credentials);
        var queuedIdentity = new CloudRunnerJobIdentity(
            "job-task-retry",
            CloudRunnerPayloadKinds.Task,
            "windows");
        var envelope = codec.ProtectTask(
            fixture.ProfileStore.Profile,
            recoveryKeys,
            queuedIdentity,
            "retry-safe task");
        var first = CreateRunnerJob(
            CreateClaimedIdentity("job-task-retry", leaseGeneration: 1),
            envelope);
        var second = CreateRunnerJob(
            CreateClaimedIdentity("job-task-retry", leaseGeneration: 2),
            envelope);

        Assert.Equal(
            "retry-safe task",
            codec.UnprotectTask(fixture.ProfileStore.Profile, recoveryKeys, first));
        Assert.Equal(
            "retry-safe task",
            codec.UnprotectTask(fixture.ProfileStore.Profile, recoveryKeys, second));
    }

    [Fact]
    public async Task AutomationTemplateRejectsAnAutomationIdentitySwap()
    {
        var fixture = await Fixture.CreateRemoteAsync();
        _ = await fixture.Service.CreateAutomationTaskAsync(
            "nightly review",
            3600,
            "windows",
            "review the default branch");
        fixture.CloudClient.ClaimEnvelope = fixture.CloudClient.AutomationEnvelope;
        fixture.CloudClient.ClaimedJobId = "job-automation-1";
        fixture.CloudClient.ClaimedKind = CloudRunnerPayloadKinds.Automation;
        fixture.CloudClient.ClaimedAutomationId = "automation-swapped";
        fixture.CloudClient.ClaimedRunId = "run-automation-1";

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Service.ClaimRunnerTaskAsync("runner-1", 30));
    }

    [Fact]
    public async Task DisposeIsIdempotentAndRejectsFurtherOperations()
    {
        var fixture = new Fixture();

        fixture.Service.Dispose();
        fixture.Service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Service.LoadProfileAsync());
    }

    [Fact]
    public async Task NotificationClientFollowsRemoteSwitchAndLocalMode()
    {
        var notifications = new RecordingNotificationClientFactory();
        var fixture = new Fixture(notificationClientFactory: notifications.Create);
        await fixture.Service.SaveRemoteProfileAsync(
            RemoteEndpoint,
            "team-1",
            "device-1",
            "test-token-1");
        await fixture.Service.StartNotificationsAsync((_, _) => Task.CompletedTask);
        var first = Assert.Single(notifications.Clients);

        await fixture.Service.SaveRemoteProfileAsync(
            RemoteEndpoint,
            "team-1",
            "device-2",
            "test-token-2");

        Assert.Equal(1, first.StopCount);
        Assert.Equal(1, first.DisposeCount);
        await fixture.Service.StartNotificationsAsync((_, _) => Task.CompletedTask);
        Assert.Equal(2, notifications.Clients.Count);
        var second = notifications.Clients[1];

        await fixture.Service.SaveLocalOnlyProfileAsync();

        Assert.Equal(1, second.StopCount);
        Assert.Equal(1, second.DisposeCount);
        await Assert.ThrowsAsync<AgentDeskCloudUnavailableException>(
            () => fixture.Service.StartNotificationsAsync((_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task ProfileSwitchCancelsAnEnteredOldGenerationNotificationHandler()
    {
        var notifications = new RecordingNotificationClientFactory();
        var fixture = new Fixture(notificationClientFactory: notifications.Create);
        await fixture.Service.SaveRemoteProfileAsync(
            RemoteEndpoint,
            "team-1",
            "device-1",
            "test-token-1");
        var handlerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var projected = 0;
        await fixture.Service.StartNotificationsAsync(async (notification, generationCancellation) =>
        {
            handlerEntered.TrySetResult();
            await releaseHandler.Task;
            generationCancellation.ThrowIfCancellationRequested();
            _ = await fixture.Service.GetPolicyAsync(generationCancellation);
            Interlocked.Increment(ref projected);
        });
        var first = Assert.Single(notifications.Clients);

        var delivery = first.DeliverAsync(new CloudNotification(
            CloudNotificationKind.PolicyChanged,
            PolicyVersion: 2));
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Service.SaveRemoteProfileAsync(
            RemoteEndpoint,
            "team-2",
            "device-2",
            "test-token-2");
        releaseHandler.TrySetResult();
        await delivery;

        Assert.Equal(0, fixture.CloudClient.GetPolicyCount);
        Assert.Equal(0, projected);
    }

    [Fact]
    public async Task DisposeStopsAnActiveNotificationClient()
    {
        var notifications = new RecordingNotificationClientFactory();
        var fixture = new Fixture(notificationClientFactory: notifications.Create);
        await fixture.Service.SaveRemoteProfileAsync(
            RemoteEndpoint,
            "team-1",
            "device-1",
            "test-token");
        await fixture.Service.StartNotificationsAsync((_, _) => Task.CompletedTask);
        var client = Assert.Single(notifications.Clients);

        fixture.Service.Dispose();

        Assert.Equal(1, client.StopCount);
        Assert.Equal(1, client.DisposeCount);
    }

    private static byte[]? TryDecodeRecoveryKey(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length == 32)
            {
                return bytes;
            }
            CryptographicOperations.ZeroMemory(bytes);
        }
        catch (FormatException)
        {
        }
        return null;
    }

    private static EncryptedEnvelope CreateEncryptedEnvelope() =>
        new(
            EncryptedEnvelope.Aes256GcmAlgorithm,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    private static CloudRunnerJobIdentity CreateClaimedIdentity(
        string jobId,
        long leaseGeneration,
        string kind = CloudRunnerPayloadKinds.Task,
        string requiredCapability = "windows",
        string? automationId = null,
        string? runId = null) =>
        (CloudRunnerJobIdentity)Activator.CreateInstance(
            typeof(CloudRunnerJobIdentity),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                jobId,
                kind,
                requiredCapability,
                automationId,
                runId,
                "runner-1",
                "adl_test-lease-token",
                leaseGeneration,
            ],
            culture: null)!;

    private static CloudRunnerJob CreateRunnerJob(
        CloudRunnerJobIdentity identity,
        EncryptedEnvelope envelope) =>
        (CloudRunnerJob)Activator.CreateInstance(
            typeof(CloudRunnerJob),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [identity, envelope, DateTimeOffset.UtcNow.AddMinutes(1)],
            culture: null)!;

    private sealed class Fixture
    {
        public Fixture()
            : this(new RecordingCloudClient(), notificationClientFactory: null)
        {
        }

        public Fixture(
            RecordingCloudClient cloudClient,
            Func<CloudConnectionProfile, ICloudAccessTokenProvider, ICloudNotificationClient>?
                notificationClientFactory = null)
        {
            CloudClient = cloudClient;
            Service = new AgentDeskCloudDesktopService(
                ProfileStore,
                Credentials,
                MetadataStore,
                CloudClient,
                notificationClientFactory);
        }

        public Fixture(
            Func<CloudConnectionProfile, ICloudAccessTokenProvider, ICloudNotificationClient>
                notificationClientFactory)
            : this(new RecordingCloudClient(), notificationClientFactory)
        {
        }

        public InMemoryProfileStore ProfileStore { get; } = new();

        public InMemoryCredentialStore Credentials { get; } = new();

        public InMemoryMetadataStore MetadataStore { get; } = new();

        public RecordingCloudClient CloudClient { get; }

        public AgentDeskCloudDesktopService Service { get; }

        public static async Task<Fixture> CreateRemoteAsync()
        {
            var fixture = new Fixture();
            await fixture.Service.SaveRemoteProfileAsync(
                RemoteEndpoint,
                "team-1",
                "device-1",
                "test-token");
            return fixture;
        }
    }

    private sealed class RecordingNotificationClientFactory
    {
        public List<RecordingNotificationClient> Clients { get; } = [];

        public ICloudNotificationClient Create(
            CloudConnectionProfile profile,
            ICloudAccessTokenProvider tokenProvider)
        {
            var client = new RecordingNotificationClient(profile, tokenProvider);
            Clients.Add(client);
            return client;
        }
    }

    private sealed class RecordingNotificationClient(
        CloudConnectionProfile profile,
        ICloudAccessTokenProvider tokenProvider) : ICloudNotificationClient
    {
        public CloudConnectionProfile Profile { get; } = profile;

        public ICloudAccessTokenProvider TokenProvider { get; } = tokenProvider;

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        private Func<CloudNotification, Task>? NotificationHandler { get; set; }

        public Task StartAsync(
            Func<CloudNotification, Task> notificationHandler,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            NotificationHandler = notificationHandler;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            NotificationHandler = null;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            NotificationHandler = null;
            return ValueTask.CompletedTask;
        }

        public Task DeliverAsync(CloudNotification notification) =>
            NotificationHandler?.Invoke(notification) ?? Task.CompletedTask;
    }

    private sealed class InMemoryProfileStore : ICloudConnectionProfileStore
    {
        public CloudConnectionProfile Profile { get; set; } = new();

        public Exception? SaveException { get; set; }

        public Task<CloudConnectionProfile> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Profile);
        }

        public Task SaveAsync(
            CloudConnectionProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SaveException is not null)
            {
                return Task.FromException(SaveException);
            }
            Profile = profile;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private readonly TaskCompletionSource _twoRecoveryReads = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;
        private int _recoveryKeyReadCount;
        private int _recoveryKeySaveCount;

        public IReadOnlyCollection<string> Values
        {
            get
            {
                lock (_gate)
                {
                    return _values.Values.ToArray();
                }
            }
        }

        public int ReadCount => Volatile.Read(ref _readCount);

        public int RecoveryKeySaveCount => Volatile.Read(ref _recoveryKeySaveCount);

        public bool CoordinateFirstRecoveryKeyReads { get; set; }

        public Exception? DeleteException { get; set; }

        public void Save(string name, string secret)
        {
            lock (_gate)
            {
                _values[name] = secret;
            }
            if (name.StartsWith("cloud/recovery/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _recoveryKeySaveCount);
            }
        }

        public string? Read(string name)
        {
            Interlocked.Increment(ref _readCount);
            string? value;
            lock (_gate)
            {
                value = _values.GetValueOrDefault(name);
            }
            if (CoordinateFirstRecoveryKeyReads &&
                name.StartsWith("cloud/recovery/", StringComparison.Ordinal) &&
                Interlocked.Increment(ref _recoveryKeyReadCount) <= 2)
            {
                if (Volatile.Read(ref _recoveryKeyReadCount) == 2)
                {
                    _twoRecoveryReads.TrySetResult();
                }
                _ = _twoRecoveryReads.Task.Wait(TimeSpan.FromMilliseconds(500));
            }
            return value;
        }

        public bool Delete(string name)
        {
            if (DeleteException is not null)
            {
                throw DeleteException;
            }
            lock (_gate)
            {
                return _values.Remove(name);
            }
        }
    }

    private sealed class InMemoryMetadataStore : ICloudSyncMetadataStore
    {
        private readonly Dictionary<string, int> _revisions = new(StringComparer.Ordinal);
        private CloudConnectionProfile? _profile;

        public IReadOnlyDictionary<string, int> Revisions => _revisions;

        public ValueTask<CloudConnectionProfile?> ReadProfileAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_profile);
        }

        public ValueTask SaveProfileAsync(
            CloudConnectionProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profile = profile;
            _revisions.Clear();
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
            _revisions[sessionId] = revision;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteRevisionAsync(
            CloudSyncMetadataScope scope,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _revisions.Remove(sessionId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCloudClient : IAgentDeskCloudClient
    {
        public List<(string SessionId, int Revision, EncryptedEnvelope Envelope)> Uploads { get; } = [];

        public CloudSyncedSession? SessionToDownload { get; set; }

        public (string SessionId, int KnownRevision)? DeletedSession { get; private set; }

        public List<CloudHandoff> Handoffs { get; } = [];

        public string SourceDeviceId { get; set; } = "device-1";

        public int GetPolicyCount { get; private set; }

        public CloudTeamPolicyUpdate? PolicyUpdate { get; private set; }

        public Func<Task>? BeforeUploadAsync { get; set; }

        public (string RunnerId, IReadOnlyList<string> Capabilities)? RunnerRegistration { get; private set; }

        public EncryptedEnvelope? QueuedEnvelope { get; private set; }

        public EncryptedEnvelope? CompletedEnvelope { get; private set; }

        public EncryptedEnvelope? AutomationEnvelope { get; private set; }

        public EncryptedEnvelope? ClaimEnvelope { get; set; }

        public CloudRunnerJobIdentity? QueuedIdentity { get; private set; }

        public CloudRunnerJobIdentity? CompletedIdentity { get; private set; }

        public int ClaimJobCallCount { get; private set; }

        public int CompleteJobCallCount { get; private set; }

        public Exception? CompleteJobException { get; set; }

        public DateTimeOffset? ClaimLeaseExpiresAt { get; set; }

        public string? ClaimedJobId { get; set; }

        public string ClaimedRequiredCapability { get; set; } = "windows";

        public string ClaimedKind { get; set; } = CloudRunnerPayloadKinds.Task;

        public string? ClaimedAutomationId { get; set; }

        public string? ClaimedRunId { get; set; }

        public Task<CloudTeamPolicy> GetPolicyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetPolicyCount++;
            return Task.FromResult(CreatePolicy());
        }

        public Task<CloudTeamPolicy> UpdatePolicyAsync(
            CloudTeamPolicyUpdate update,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PolicyUpdate = update;
            return Task.FromResult(CreatePolicy());
        }

        public async Task<CloudSessionWriteReceipt> PutSessionAsync(
            string sessionId,
            int revision,
            EncryptedEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeUploadAsync is not null)
            {
                await BeforeUploadAsync();
            }
            lock (Uploads)
            {
                Uploads.Add((sessionId, revision, envelope));
            }
            return new CloudSessionWriteReceipt(sessionId, revision);
        }

        public Task<CloudSyncedSession?> GetSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SessionToDownload);
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
            return Task.FromResult(Handoffs.RemoveAll(item => item.HandoffId == handoffId) > 0);
        }

        public Task RegisterRunnerAsync(
            string runnerId,
            IReadOnlyList<string> capabilities,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunnerRegistration = (runnerId, capabilities.ToArray());
            return Task.CompletedTask;
        }

        public Task<CloudJobReceipt> QueueJobAsync(
            CloudRunnerJobIdentity identity,
            EncryptedEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueuedIdentity = identity;
            QueuedEnvelope = envelope;
            return Task.FromResult(new CloudJobReceipt(identity.JobId));
        }

        public Task<CloudRunnerJob?> ClaimJobAsync(
            string runnerId,
            int leaseSeconds = 60,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClaimJobCallCount++;
            return Task.FromResult<CloudRunnerJob?>(CreateRunnerJob(
                ClaimEnvelope ?? QueuedEnvelope ??
                    throw new InvalidOperationException("No queued job payload.")));
        }

        public Task CompleteJobAsync(
            CloudRunnerJobIdentity identity,
            EncryptedEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompleteJobCallCount++;
            if (CompleteJobException is not null)
            {
                return Task.FromException(CompleteJobException);
            }
            CompletedIdentity = identity;
            CompletedEnvelope = envelope;
            return Task.CompletedTask;
        }

        public Task<CloudAutomation> CreateAutomationAsync(
            string automationId,
            string name,
            int intervalSeconds,
            string requiredCapability,
            EncryptedEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AutomationEnvelope = envelope;
            return Task.FromResult(new CloudAutomation(
                automationId,
                name,
                intervalSeconds,
                true,
                DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<CloudAutomation>> ListAutomationsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudAutomation>>(
                [new CloudAutomation("automation-1", "nightly", 3600, true, DateTimeOffset.UtcNow)]);

        public Task<bool> DisableAutomationAsync(
            string automationId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<CloudIssuedToken> CreateTokenAsync(
            string subjectId,
            CloudTokenRole role,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RevokeTokenAsync(
            string subjectId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CloudSessionDeleteReceipt> DeleteSessionAsync(
            string sessionId,
            int knownRevision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeletedSession = (sessionId, knownRevision);
            return Task.FromResult(new CloudSessionDeleteReceipt(sessionId, knownRevision + 1));
        }

        private static CloudTeamPolicy CreatePolicy() =>
            (CloudTeamPolicy)Activator.CreateInstance(
                typeof(CloudTeamPolicy),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [1, new[] { "WslStrict" }, true, false, 2, new[] { "publisher-1" }],
                culture: null)!;

        private CloudRunnerJob CreateRunnerJob(EncryptedEnvelope envelope) =>
            (CloudRunnerJob)Activator.CreateInstance(
                typeof(CloudRunnerJob),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args:
                [
                    CreateClaimedIdentity(
                        ClaimedJobId ?? QueuedIdentity?.JobId ?? "job-1",
                        leaseGeneration: 1,
                        kind: ClaimedKind,
                        requiredCapability: ClaimedRequiredCapability,
                        automationId: ClaimedAutomationId,
                        runId: ClaimedRunId),
                    envelope,
                    ClaimLeaseExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(1),
                ],
                culture: null)!;
    }

    private sealed class RecordingEngineClient : IEngineClient
    {
        public event EventHandler<EngineEvent>? EventReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<PermissionRequest>? PermissionRequested
        {
            add { }
            remove { }
        }

        public event EventHandler<EngineFaultedEventArgs>? Faulted
        {
            add { }
            remove { }
        }

        public EngineCapabilities Capabilities { get; } = EngineCapabilities.Uninitialized;

        public EngineSessionDocument? ExportedDocument { get; init; }

        public Func<Task>? BeforeExportAsync { get; init; }

        public SessionId ImportedSessionId { get; init; } = new("imported");

        public List<string> ExportedSessionIds { get; } = [];

        public string? ImportWorkingDirectory { get; private set; }

        public async Task<EngineSessionDocument> ExportSessionAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeExportAsync is not null)
            {
                await BeforeExportAsync();
            }
            lock (ExportedSessionIds)
            {
                ExportedSessionIds.Add(sessionId.Value);
            }
            return ExportedDocument ?? throw new InvalidOperationException("No export document.");
        }

        public Task<SessionId> ImportSessionAsync(
            EngineSessionDocument document,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImportWorkingDirectory = workingDirectory;
            return Task.FromResult(ImportedSessionId);
        }

        public Task<EngineCapabilities> InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Capabilities);

        public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SessionId> NewSessionAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default) => Task.FromResult(new SessionId("new"));

        public Task LoadSessionAsync(
            SessionId sessionId,
            string workingDirectory,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<RuntimeCommand>> ListRuntimeCommandsAsync(
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RuntimeCommand>>([]);

        public Task<IReadOnlyList<BackgroundTaskSnapshot>> ListBackgroundTasksAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackgroundTaskSnapshot>>([]);

        public Task<BackgroundTaskKillOutcome> KillBackgroundTaskAsync(
            SessionId sessionId,
            string taskId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(BackgroundTaskKillOutcome.NotFound);

        public Task<IReadOnlyList<SubagentSnapshot>> ListRunningSubagentsAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SubagentSnapshot>>([]);

        public Task<SubagentSnapshot?> GetSubagentAsync(
            SessionId sessionId,
            string subagentId,
            bool block = false,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) => Task.FromResult<SubagentSnapshot?>(null);

        public Task<SubagentCancelResult> CancelSubagentAsync(
            SessionId sessionId,
            string subagentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SubagentCancelResult(SubagentCancelOutcome.NotFound));

        public Task<SessionPage> ListSessionsAsync(
            string? workingDirectory,
            string? query,
            string? cursor,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionPage([], null));

        public Task RenameSessionAsync(
            SessionId sessionId,
            string title,
            string? workingDirectory,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SessionForkResult> ForkSessionAsync(
            SessionId sourceSessionId,
            string sourceWorkingDirectory,
            string targetWorkingDirectory,
            int? targetPromptIndex = null,
            string? modelId = null,
            string? sessionKind = null,
            string? sourceWorkspacePath = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<SessionForkResult>(new NotSupportedException());

        public Task CompactSessionAsync(
            SessionId sessionId,
            string? userContext = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task FlushMemoryAsync(
            SessionId activeSessionId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SessionRewindPoint>> GetRewindPointsAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionRewindPoint>>([]);

        public Task<SessionRewindResult> RewindSessionAsync(
            SessionId sessionId,
            int targetPromptIndex,
            SessionRewindMode mode,
            bool force = false,
            CancellationToken cancellationToken = default) =>
            Task.FromException<SessionRewindResult>(new NotSupportedException());

        public Task SetSessionModeAsync(
            SessionId sessionId,
            SessionMode mode,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PromptResult> PromptAsync(
            SessionId sessionId,
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PromptResult(EngineStopReason.EndTurn, "end_turn"));

        public Task CancelAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> RespondToPermissionAsync(
            string requestId,
            PermissionDecision decision,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
