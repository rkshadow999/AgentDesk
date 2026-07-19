using System.Security.Cryptography;
using AgentDesk.Core.Engine;

namespace AgentDesk.Cloud.Client.Tests;

public sealed class EngineCloudSessionWorkflowTests
{
    [Fact]
    public async Task UploadExportsTheEngineSessionBeforeEncryptingIt()
    {
        var fixture = CreateFixture("device-a");
        var engine = new RecordingEngineClient
        {
            ExportedDocument = EngineSessionDocument.FromJson("{\"session\":\"local\"}"),
        };
        var workflow = new EngineCloudSessionWorkflow(
            fixture.SyncCoordinator,
            fixture.HandoffCoordinator);

        var revision = await workflow.UploadAsync(engine, new SessionId("session-1"));

        Assert.Equal(1, revision);
        Assert.Equal(["session-1"], engine.ExportedSessionIds);
        Assert.Single(fixture.CloudClient.Uploads);
    }

    [Fact]
    public async Task DownloadImportsTheDecryptedSessionIntoTheRequestedWorkspace()
    {
        var fixture = CreateFixture("device-a");
        await fixture.SyncCoordinator.UploadAsync(
            "session-remote",
            SessionSyncDocument.FromJson("{\"session\":\"remote\"}"));
        fixture.CloudClient.SessionToDownload = new CloudSyncedSession(
            fixture.CloudClient.Uploads[0].SessionId,
            fixture.CloudClient.Uploads[0].Revision,
            fixture.CloudClient.Uploads[0].Envelope,
            DateTimeOffset.UtcNow);
        var engine = new RecordingEngineClient
        {
            ImportedSessionId = new SessionId("session-imported"),
        };
        var workflow = new EngineCloudSessionWorkflow(
            fixture.SyncCoordinator,
            fixture.HandoffCoordinator);

        var result = await workflow.DownloadAndImportAsync(
            engine,
            "session-remote",
            "C:\\work\\project");

        Assert.NotNull(result);
        Assert.Equal("session-imported", result.ImportedSessionId.Value);
        Assert.Equal(1, result.Revision);
        Assert.Equal("C:\\work\\project", engine.ImportWorkingDirectory);
        Assert.Equal(
            "{\"session\":\"remote\"}",
            System.Text.Encoding.UTF8.GetString(engine.ImportedDocument!.ExportUtf8Json()));
    }

    [Fact]
    public async Task DownloadOnAnotherDeviceUsesTheSharedTeamRecoveryKey()
    {
        var source = CreateFixture("device-source");
        var target = CreateFixture(
            "device-target",
            source.CloudClient,
            source.RecoveryKeyStore);
        await source.SyncCoordinator.UploadAsync(
            "session-remote",
            SessionSyncDocument.FromJson("{\"session\":\"remote\"}"));
        source.CloudClient.SessionToDownload = new CloudSyncedSession(
            source.CloudClient.Uploads[0].SessionId,
            source.CloudClient.Uploads[0].Revision,
            source.CloudClient.Uploads[0].Envelope,
            DateTimeOffset.UtcNow);
        var workflow = new EngineCloudSessionWorkflow(
            target.SyncCoordinator,
            target.HandoffCoordinator);
        var engine = new RecordingEngineClient
        {
            ImportedSessionId = new SessionId("session-imported"),
        };

        var result = await workflow.DownloadAndImportAsync(
            engine,
            "session-remote",
            "C:\\work\\target");

        Assert.NotNull(result);
        Assert.Equal("session-imported", result.ImportedSessionId.Value);
    }

    [Fact]
    public async Task ReceiveHandoffsAcknowledgesOnlyAfterSuccessfulImport()
    {
        var source = CreateFixture("device-source");
        var target = CreateFixture(
            "device-target",
            source.CloudClient,
            source.RecoveryKeyStore);
        var sourceWorkflow = new EngineCloudSessionWorkflow(
            source.SyncCoordinator,
            source.HandoffCoordinator);
        var targetWorkflow = new EngineCloudSessionWorkflow(
            target.SyncCoordinator,
            target.HandoffCoordinator);
        var sourceEngine = new RecordingEngineClient
        {
            ExportedDocument = EngineSessionDocument.FromJson("{\"handoff\":true}"),
        };
        await sourceWorkflow.CreateHandoffAsync(
            sourceEngine,
            new SessionId("session-1"),
            "device-target");
        var targetEngine = new RecordingEngineClient
        {
            ImportedSessionId = new SessionId("session-2"),
        };

        var received = await targetWorkflow.ReceiveHandoffsAsync(
            targetEngine,
            "C:\\work\\target");

        var item = Assert.Single(received);
        Assert.Equal("session-2", item.ImportedSessionId.Value);
        Assert.Empty(source.CloudClient.Handoffs);
    }

    [Fact]
    public async Task ReceiveHandoffsLeavesTheRemoteItemWhenImportFails()
    {
        var source = CreateFixture("device-source");
        var target = CreateFixture(
            "device-target",
            source.CloudClient,
            source.RecoveryKeyStore);
        var sourceWorkflow = new EngineCloudSessionWorkflow(
            source.SyncCoordinator,
            source.HandoffCoordinator);
        var targetWorkflow = new EngineCloudSessionWorkflow(
            target.SyncCoordinator,
            target.HandoffCoordinator);
        var sourceEngine = new RecordingEngineClient
        {
            ExportedDocument = EngineSessionDocument.FromJson("{\"handoff\":true}"),
        };
        await sourceWorkflow.CreateHandoffAsync(
            sourceEngine,
            new SessionId("session-1"),
            "device-target");
        var targetEngine = new RecordingEngineClient
        {
            ImportException = new InvalidOperationException("import failed"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            targetWorkflow.ReceiveHandoffsAsync(targetEngine, "C:\\work\\target"));

        Assert.Single(source.CloudClient.Handoffs);
    }

    private static WorkflowFixture CreateFixture(
        string deviceId,
        RecordingCloudClient? cloudClient = null,
        RecordingRecoveryKeyStore? recoveryKeyStore = null)
    {
        var profile = new CloudConnectionProfile(
            new Uri("https://cloud.example.test"),
            "team-1",
            deviceId);
        cloudClient ??= new RecordingCloudClient { SourceDeviceId = deviceId };
        recoveryKeyStore ??= new RecordingRecoveryKeyStore(
            RandomNumberGenerator.GetBytes(32));
        var metadata = new RecordingCloudSyncMetadataStore();
        return new WorkflowFixture(
            cloudClient,
            recoveryKeyStore,
            new CloudSyncCoordinator(
                profile,
                cloudClient,
                recoveryKeyStore,
                metadata),
            new EncryptedHandoffCoordinator(
                profile,
                cloudClient,
                recoveryKeyStore));
    }

    private sealed record WorkflowFixture(
        RecordingCloudClient CloudClient,
        RecordingRecoveryKeyStore RecoveryKeyStore,
        CloudSyncCoordinator SyncCoordinator,
        EncryptedHandoffCoordinator HandoffCoordinator);

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

        public SessionId ImportedSessionId { get; init; } = new("imported");

        public Exception? ImportException { get; init; }

        public List<string> ExportedSessionIds { get; } = [];

        public EngineSessionDocument? ImportedDocument { get; private set; }

        public string? ImportWorkingDirectory { get; private set; }

        public Task<EngineSessionDocument> ExportSessionAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExportedSessionIds.Add(sessionId.Value);
            return Task.FromResult(
                ExportedDocument ?? throw new InvalidOperationException("No export document."));
        }

        public Task<SessionId> ImportSessionAsync(
            EngineSessionDocument document,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ImportException is not null)
            {
                return Task.FromException<SessionId>(ImportException);
            }

            ImportedDocument = document;
            ImportWorkingDirectory = workingDirectory;
            return Task.FromResult(ImportedSessionId);
        }

        public Task<EngineCapabilities> InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Capabilities);

        public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SessionId> NewSessionAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionId("new"));

        public Task LoadSessionAsync(SessionId sessionId, string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<RuntimeCommand>> ListRuntimeCommandsAsync(string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RuntimeCommand>>([]);

        public Task<IReadOnlyList<BackgroundTaskSnapshot>> ListBackgroundTasksAsync(SessionId sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackgroundTaskSnapshot>>([]);

        public Task<BackgroundTaskKillOutcome> KillBackgroundTaskAsync(SessionId sessionId, string taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(BackgroundTaskKillOutcome.NotFound);

        public Task<IReadOnlyList<SubagentSnapshot>> ListRunningSubagentsAsync(SessionId sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SubagentSnapshot>>([]);

        public Task<SubagentSnapshot?> GetSubagentAsync(SessionId sessionId, string subagentId, bool block = false, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<SubagentSnapshot?>(null);

        public Task<SubagentCancelResult> CancelSubagentAsync(SessionId sessionId, string subagentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SubagentCancelResult(SubagentCancelOutcome.NotFound));

        public Task<SessionPage> ListSessionsAsync(string? workingDirectory, string? query, string? cursor, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionPage([], null));

        public Task RenameSessionAsync(SessionId sessionId, string title, string? workingDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SessionForkResult> ForkSessionAsync(SessionId sourceSessionId, string sourceWorkingDirectory, string targetWorkingDirectory, int? targetPromptIndex = null, string? modelId = null, string? sessionKind = null, string? sourceWorkspacePath = null, CancellationToken cancellationToken = default) =>
            Task.FromException<SessionForkResult>(new NotSupportedException());

        public Task CompactSessionAsync(SessionId sessionId, string? userContext = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task FlushMemoryAsync(SessionId activeSessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SessionRewindPoint>> GetRewindPointsAsync(SessionId sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionRewindPoint>>([]);

        public Task<SessionRewindResult> RewindSessionAsync(SessionId sessionId, int targetPromptIndex, SessionRewindMode mode, bool force = false, CancellationToken cancellationToken = default) =>
            Task.FromException<SessionRewindResult>(new NotSupportedException());

        public Task SetSessionModeAsync(SessionId sessionId, SessionMode mode, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PromptResult> PromptAsync(SessionId sessionId, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PromptResult(EngineStopReason.EndTurn, "end_turn"));

        public Task CancelAsync(SessionId sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> RespondToPermissionAsync(string requestId, PermissionDecision decision, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
