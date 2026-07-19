using System.Collections.Concurrent;
using AgentDesk.App.Attachments;
using AgentDesk.App.Bridge;
using AgentDesk.App.Maintenance;
using AgentDesk.Core.Engine;
using AgentDesk.Platform.Windows.Backup;
using AgentDesk.Updater.Core;

namespace AgentDesk.App.Tests;

public sealed class AgentDeskMaintenanceCoordinatorTests : IDisposable
{
    private const string RequestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"agentdesk-maintenance-{Guid.NewGuid():N}");
    private readonly List<IDisposable> _disposables = [];

    [Fact]
    public async Task ExportAndImport_RoundTripThroughNativeFilesWithoutPublishingTheDocument()
    {
        var fixture = CreateFixture();
        var destination = Path.Combine(_root, "exports", "session-42.agentdesk-session");
        var document = EngineSessionDocument.FromJson(
            "{\"schemaVersion\":1,\"sessionId\":\"session-42\",\"body\":\"private\"}");
        fixture.Host.Lease.ExportHandler = (sessionId, _) =>
        {
            Assert.Equal("session-42", sessionId.Value);
            return Task.FromResult(document);
        };
        fixture.Host.Lease.ImportHandler = (imported, _) =>
        {
            Assert.Equal(document.ExportUtf8Json(), imported.ExportUtf8Json());
            return Task.FromResult(new SessionId("session-imported"));
        };

        await fixture.Coordinator.HandleAsync(
            new SessionExportWebCommand(RequestId, "session-42"),
            destination);
        await fixture.Coordinator.HandleAsync(
            new SessionImportWebCommand("7028f6d0-9070-4c1a-811f-489761b0fdaa"),
            destination);

        Assert.True(File.Exists(destination));
        var projectedEvents = fixture.Events.ToArray();
        var exported = Assert.IsType<SessionExportedWebEvent>(projectedEvents[0]);
        Assert.Equal(Path.GetFileName(destination), exported.FileName);
        var imported = Assert.IsType<SessionImportedWebEvent>(projectedEvents[1]);
        Assert.Equal("session-imported", imported.SessionId);
        Assert.Equal("C:\\workspace", imported.WorkspacePath);
        Assert.All(
            fixture.Events,
            webEvent => Assert.DoesNotContain("private", webEvent.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelledFilePicker_PublishesNeutralCancellationWithoutOpeningTheHost()
    {
        var fixture = CreateFixture();
        MaintenanceWebCommand[] commands =
        [
            new SessionExportWebCommand(RequestId, "session-42"),
            new SessionImportWebCommand("7028f6d0-9070-4c1a-811f-489761b0fdaa"),
            new BackupCreateWebCommand("29da6011-ab74-43ac-b24f-3ee4611ddb67"),
            new BackupRestoreWebCommand("164919e4-b67f-413a-a38d-8fbdf97c5c83"),
        ];

        foreach (var command in commands)
        {
            await fixture.Coordinator.HandleAsync(command, nativePath: null);
        }

        Assert.Equal(0, fixture.Host.BeginCount);
        Assert.Equal(
            ["session-export", "session-import", "backup-create", "backup-restore"],
            fixture.Events
                .OfType<MaintenanceCancelledWebEvent>()
                .Select(webEvent => webEvent.Operation));
    }

    [Fact]
    public async Task PromptBusy_RejectsAllMaintenanceOperationsBeforeAnySideEffect()
    {
        var fixture = CreateFixture();
        fixture.Host.BeginException = new InvalidOperationException("prompt is active");
        var path = Path.Combine(_root, "selected.agentdesk");
        MaintenanceWebCommand[] commands =
        [
            new SessionExportWebCommand(RequestId, "session-42"),
            new SessionImportWebCommand("7028f6d0-9070-4c1a-811f-489761b0fdaa"),
            new BackupCreateWebCommand("29da6011-ab74-43ac-b24f-3ee4611ddb67"),
            new BackupRestoreWebCommand("164919e4-b67f-413a-a38d-8fbdf97c5c83"),
            new UpdateCheckWebCommand("d37014e0-774a-4b5f-adb7-73ae22c33352"),
            new UpdateApplyWebCommand("367bd1e6-da36-4fdd-80fb-ff80f4a605f9"),
        ];

        foreach (var command in commands)
        {
            await fixture.Coordinator.HandleAsync(
                command,
                command is SessionExportWebCommand or SessionImportWebCommand or
                    BackupCreateWebCommand or BackupRestoreWebCommand
                    ? path
                    : null);
        }

        Assert.Equal(6, fixture.Host.BeginCount);
        Assert.Equal(0, fixture.UpdateStager.CallCount);
        Assert.Equal(
            [
                "session-export", "session-import", "backup-create",
                "backup-restore", "update-check", "update-apply",
            ],
            fixture.Events.OfType<MaintenanceErrorWebEvent>().Select(webEvent => webEvent.Operation));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task MaintenanceOperations_UseOneGlobalNonBlockingLease()
    {
        var fixture = CreateFixture();
        var exportStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExport = new TaskCompletionSource<EngineSessionDocument>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Host.Lease.ExportHandler = (_, _) =>
        {
            exportStarted.TrySetResult();
            return releaseExport.Task;
        };
        var export = fixture.Coordinator.HandleAsync(
            new SessionExportWebCommand(RequestId, "session-42"),
            Path.Combine(_root, "session.agentdesk-session"));
        await exportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        MaintenanceWebCommand[] competingCommands =
        [
            new SessionExportWebCommand(
                "7028f6d0-9070-4c1a-811f-489761b0fdaa",
                "session-42"),
            new SessionImportWebCommand("29da6011-ab74-43ac-b24f-3ee4611ddb67"),
            new BackupCreateWebCommand("164919e4-b67f-413a-a38d-8fbdf97c5c83"),
            new BackupRestoreWebCommand("d37014e0-774a-4b5f-adb7-73ae22c33352"),
            new UpdateCheckWebCommand("367bd1e6-da36-4fdd-80fb-ff80f4a605f9"),
            new UpdateApplyWebCommand("e667f61b-f978-46c0-9b4e-5429018f8bcf"),
        ];
        foreach (var command in competingCommands)
        {
            await fixture.Coordinator.HandleAsync(
                command,
                command is SessionExportWebCommand or SessionImportWebCommand or
                    BackupCreateWebCommand or BackupRestoreWebCommand
                    ? Path.Combine(_root, "must-not-be-opened.agentdesk")
                    : null).WaitAsync(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(
            [
                "session-export", "session-import", "backup-create",
                "backup-restore", "update-check", "update-apply",
            ],
            fixture.Events.OfType<MaintenanceErrorWebEvent>().Select(error => error.Operation));
        Assert.Equal(1, fixture.Host.BeginCount);
        Assert.Equal(0, fixture.UpdateStager.CallCount);
        releaseExport.SetResult(EngineSessionDocument.FromJson("{\"schemaVersion\":1}"));
        await export;
    }

    [Fact]
    public async Task BackupCreate_StopsTheSidecarBeforeReadingAuthoritativeData()
    {
        var fixture = CreateFixture();
        var backup = Path.Combine(_root, "backups", "agentdesk.zip");
        Directory.CreateDirectory(fixture.Options.DataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Options.DataDirectory, "settings.json"),
            "{}");
        fixture.Host.Lease.StopHandler = async _ =>
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixture.Options.DataDirectory, "stopped-before-backup.marker"),
                "stopped");
        };

        await fixture.Coordinator.HandleAsync(new BackupCreateWebCommand(RequestId), backup);

        var verifyDirectory = Path.Combine(_root, "verified-backup");
        var result = await new AgentDeskBackupService().RestoreAsync(backup, verifyDirectory);
        Assert.True(File.Exists(Path.Combine(verifyDirectory, "stopped-before-backup.marker")));
        Assert.Equal(2, result.FileCount);
        var completed = Assert.Single(fixture.Events.OfType<BackupCompletedWebEvent>());
        Assert.Equal("create", completed.Operation);
        Assert.False(completed.RestartRequired);
    }

    [Fact]
    public async Task BackupRestore_PreflightsThenClosesStopsReplacesAndRestarts()
    {
        var fixture = CreateFixture();
        var source = Path.Combine(_root, "restore-source");
        var backup = Path.Combine(_root, "restore.zip");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(fixture.Options.DataDirectory);
        await File.WriteAllTextAsync(Path.Combine(source, "restored.json"), "restored");
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Options.DataDirectory, "old.json"),
            "old");
        _ = await new AgentDeskBackupService().CreateAsync(source, backup);
        fixture.Host.Lease.StopHandler = async _ =>
        {
            fixture.Order.Add("stop");
            await File.WriteAllTextAsync(
                Path.Combine(fixture.Options.DataDirectory, "stop.marker"),
                "stopped");
        };
        fixture.PrepareRestore = _ =>
        {
            fixture.Order.Add("prepare");
            return Task.CompletedTask;
        };
        fixture.Restart = _ =>
        {
            fixture.Order.Add("restart");
            Assert.True(File.Exists(
                Path.Combine(fixture.Options.DataDirectory, "restored.json")));
            Assert.False(File.Exists(
                Path.Combine(fixture.Options.DataDirectory, "stop.marker")));
            return Task.CompletedTask;
        };

        await fixture.Coordinator.HandleAsync(new BackupRestoreWebCommand(RequestId), backup);

        Assert.Equal(["prepare", "stop", "restart"], fixture.Order);
        Assert.False(File.Exists(Path.Combine(fixture.Options.DataDirectory, "old.json")));
        var completed = Assert.Single(fixture.Events.OfType<BackupCompletedWebEvent>());
        Assert.Equal("restore", completed.Operation);
        Assert.True(completed.RestartRequired);
    }

    [Fact]
    public async Task BackupRestore_ReleasesLiveAttachmentLeaseBeforeReplacingData()
    {
        var fixture = CreateFixture();
        var source = Path.Combine(_root, "restore-with-attachments-source");
        var backup = Path.Combine(_root, "restore-with-attachments.zip");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(fixture.Options.DataDirectory);
        await File.WriteAllTextAsync(Path.Combine(source, "restored.json"), "restored");
        _ = await new AgentDeskBackupService().CreateAsync(source, backup);
        await using var attachmentStore = new NativeImageAttachmentStore(Path.Combine(
            fixture.Options.DataDirectory,
            "AttachmentStaging"));
        fixture.PrepareRestore = async cancellationToken =>
        {
            fixture.Order.Add("prepare");
            cancellationToken.ThrowIfCancellationRequested();
            await attachmentStore.DisposeAsync();
        };
        fixture.Host.Lease.StopHandler = _ =>
        {
            fixture.Order.Add("stop");
            return Task.CompletedTask;
        };
        fixture.Restart = _ =>
        {
            fixture.Order.Add("restart");
            return Task.CompletedTask;
        };

        await fixture.Coordinator.HandleAsync(new BackupRestoreWebCommand(RequestId), backup);

        Assert.Equal(["prepare", "stop", "restart"], fixture.Order);
        Assert.True(File.Exists(
            Path.Combine(fixture.Options.DataDirectory, "restored.json")));
        Assert.Single(fixture.Events.OfType<BackupCompletedWebEvent>());
    }

    [Fact]
    public async Task BackupRestore_InvalidArchiveNeverClosesOrChangesTheRunningApplication()
    {
        var fixture = CreateFixture();
        var invalidBackup = Path.Combine(_root, "invalid.zip");
        Directory.CreateDirectory(fixture.Options.DataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Options.DataDirectory, "keep.json"),
            "keep");
        await File.WriteAllTextAsync(invalidBackup, "not a backup");

        await fixture.Coordinator.HandleAsync(
            new BackupRestoreWebCommand(RequestId),
            invalidBackup);

        Assert.Empty(fixture.Order);
        Assert.Equal(0, fixture.Host.Lease.StopCount);
        Assert.Equal(
            "keep",
            await File.ReadAllTextAsync(
                Path.Combine(fixture.Options.DataDirectory, "keep.json")));
        Assert.Single(fixture.Events.OfType<MaintenanceErrorWebEvent>());
    }

    [Fact]
    public async Task UpdateCheck_PublishesCheckingBeforeThePortableResult()
    {
        var fixture = CreateFixture();

        await fixture.Coordinator.HandleAsync(new UpdateCheckWebCommand(RequestId), nativePath: null);

        Assert.Collection(
            fixture.Events.OfType<UpdateStatusWebEvent>(),
            status => Assert.Equal("checking", status.Status),
            status =>
            {
                Assert.Equal("available", status.Status);
                Assert.Equal("2.0.0", status.Version);
            });
        Assert.Equal(1, fixture.UpdateStager.CallCount);
    }

    [Fact]
    public async Task UpdateApply_LaunchesTheExternalUpdaterBeforeRequestingASafeExit()
    {
        var fixture = CreateFixture();
        await fixture.Coordinator.HandleAsync(new UpdateCheckWebCommand(RequestId), nativePath: null);
        fixture.Events.Clear();
        fixture.UpdateLauncher.OnStart = _ => fixture.Order.Add("launch");
        fixture.Exit = _ =>
        {
            fixture.Order.Add("exit");
            return Task.CompletedTask;
        };

        await fixture.Coordinator.HandleAsync(
            new UpdateApplyWebCommand("367bd1e6-da36-4fdd-80fb-ff80f4a605f9"),
            nativePath: null);

        Assert.Equal(["launch", "exit"], fixture.Order);
        Assert.Single(fixture.UpdateLauncher.Starts);
        var status = Assert.Single(fixture.Events.OfType<UpdateStatusWebEvent>());
        Assert.Equal("launching", status.Status);
    }

    [Fact]
    public async Task MsixUpdateCommands_ReportUnsupportedWithoutStagingOrLaunching()
    {
        var fixture = CreateFixture(AgentDeskPackageMode.Msix);

        await fixture.Coordinator.HandleAsync(new UpdateCheckWebCommand(RequestId), nativePath: null);
        await fixture.Coordinator.HandleAsync(
            new UpdateApplyWebCommand("367bd1e6-da36-4fdd-80fb-ff80f4a605f9"),
            nativePath: null);

        Assert.Equal(0, fixture.UpdateStager.CallCount);
        Assert.Empty(fixture.UpdateLauncher.Starts);
        Assert.Equal(
            ["unsupported", "unsupported"],
            fixture.Events.OfType<UpdateStatusWebEvent>().Select(status => status.Status));
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Fixture CreateFixture(AgentDeskPackageMode packageMode = AgentDeskPackageMode.Portable)
    {
        Directory.CreateDirectory(_root);
        var host = new FakeMaintenanceHost();
        var events = new ConcurrentQueue<WebEvent>();
        var options = new AgentDeskMaintenanceOptions(
            Path.Combine(_root, "data"),
            packageMode,
            CurrentVersion: "1.0.0",
            ParentProcessId: 42);
        var updateFixture = CreateUpdateFixture();
        var fixture = new Fixture(
            host,
            options,
            updateFixture.Coordinator,
            updateFixture.Stager,
            updateFixture.Launcher,
            events);
        fixture.RebuildCoordinator();
        return fixture;
    }

    private UpdateFixture CreateUpdateFixture()
    {
        var state = Path.Combine(_root, "update-state");
        var install = Path.Combine(_root, "install");
        var payload = Path.Combine(_root, "staged", "payload");
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "AgentDesk.Updater.exe"), "fixture");
        var asset = new UpdateAsset(
            UpdateArchitecture.X64,
            new Uri(
                "https://github.com/rkshadow999/AgentDesk/releases/download/v2.0.0/AgentDesk-updater.exe"),
            new string('a', 64),
            7,
            "AgentDesk.Updater.exe");
        var staged = new StagedUpdate(
            new UpdateManifest(1, "AgentDesk.Updater", SemanticVersion.Parse("2.0.0"), [asset]),
            asset,
            Path.Combine(_root, "staged"),
            Path.Combine(_root, "staged", "package.zip"),
            payload);
        var stager = new FakeUpdateStager(staged);
        var launcher = new FakeUpdateLauncher();
        var coordinator = new AgentDeskUpdateCoordinator(
            new AgentDeskUpdateOptions(
                new Uri("https://github.com/rkshadow999/AgentDesk/releases/latest/download/updater.json"),
                new Uri("https://github.com/rkshadow999/AgentDesk/releases/latest/download/updater.json.sig"),
                new Uri("https://github.com/rkshadow999/AgentDesk/releases/latest/download/app.json"),
                new Uri("https://github.com/rkshadow999/AgentDesk/releases/latest/download/app.json.sig"),
                Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
                SemanticVersion.Parse("1.0.0"),
                UpdateArchitecture.X64,
                state,
                install,
                allowPrerelease: false,
                restartArguments: []),
            stager,
            launcher);
        _disposables.Add(coordinator);
        return new UpdateFixture(coordinator, stager, launcher);
    }

    private sealed class Fixture(
        FakeMaintenanceHost host,
        AgentDeskMaintenanceOptions options,
        AgentDeskUpdateCoordinator updateCoordinator,
        FakeUpdateStager updateStager,
        FakeUpdateLauncher updateLauncher,
        ConcurrentQueue<WebEvent> events)
    {
        public FakeMaintenanceHost Host { get; } = host;

        public AgentDeskMaintenanceOptions Options { get; } = options;

        public AgentDeskUpdateCoordinator UpdateCoordinator { get; } = updateCoordinator;

        public FakeUpdateStager UpdateStager { get; } = updateStager;

        public FakeUpdateLauncher UpdateLauncher { get; } = updateLauncher;

        public ConcurrentQueue<WebEvent> Events { get; } = events;

        public List<string> Order { get; } = [];

        public Func<CancellationToken, Task> PrepareRestore { get; set; } = _ => Task.CompletedTask;

        public Func<CancellationToken, Task> Restart { get; set; } = _ => Task.CompletedTask;

        public Func<CancellationToken, Task> Exit { get; set; } = _ => Task.CompletedTask;

        public AgentDeskMaintenanceCoordinator Coordinator { get; private set; } = null!;

        public void RebuildCoordinator()
        {
            Coordinator = new AgentDeskMaintenanceCoordinator(
                Host,
                new SessionDocumentFileStore(),
                new AgentDeskBackupService(),
                UpdateCoordinator,
                Options,
                webEvent =>
                {
                    Events.Enqueue(webEvent);
                    return Task.CompletedTask;
                },
                cancellationToken => PrepareRestore(cancellationToken),
                cancellationToken => Restart(cancellationToken),
                cancellationToken => Exit(cancellationToken));
        }
    }

    private sealed class FakeMaintenanceHost : IAgentDeskMaintenanceHost
    {
        public FakeMaintenanceLease Lease { get; } = new();

        public Exception? BeginException { get; set; }

        public int BeginCount { get; private set; }

        public Task<IAgentDeskMaintenanceLease> BeginMaintenanceAsync(
            CancellationToken cancellationToken = default)
        {
            BeginCount++;
            return BeginException is null
                ? Task.FromResult<IAgentDeskMaintenanceLease>(Lease)
                : Task.FromException<IAgentDeskMaintenanceLease>(BeginException);
        }
    }

    private sealed class FakeMaintenanceLease : IAgentDeskMaintenanceLease
    {
        public string WorkspacePath => "C:\\workspace";

        public Func<SessionId, CancellationToken, Task<EngineSessionDocument>>? ExportHandler
        { get; set; }

        public Func<EngineSessionDocument, CancellationToken, Task<SessionId>>? ImportHandler
        { get; set; }

        public Func<CancellationToken, Task>? StopHandler { get; set; }

        public int StopCount { get; private set; }

        public Task<EngineSessionDocument> ExportSessionAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default) =>
            ExportHandler?.Invoke(sessionId, cancellationToken) ??
            Task.FromException<EngineSessionDocument>(new NotSupportedException());

        public Task<SessionId> ImportSessionAsync(
            EngineSessionDocument document,
            CancellationToken cancellationToken = default) =>
            ImportHandler?.Invoke(document, cancellationToken) ??
            Task.FromException<SessionId>(new NotSupportedException());

        public Task StopEngineAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return StopHandler?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeUpdateStager(StagedUpdate? result) : IAgentDeskUpdateStager
    {
        public int CallCount { get; private set; }

        public Task<StagedUpdate?> CheckAndStageAsync(
            UpdateCheckRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeUpdateLauncher : IAgentDeskUpdateProcessLauncher
    {
        public List<AgentDeskUpdateProcessStart> Starts { get; } = [];

        public Action<AgentDeskUpdateProcessStart>? OnStart { get; set; }

        public void Start(AgentDeskUpdateProcessStart start)
        {
            Starts.Add(start);
            OnStart?.Invoke(start);
        }
    }

    private sealed record UpdateFixture(
        AgentDeskUpdateCoordinator Coordinator,
        FakeUpdateStager Stager,
        FakeUpdateLauncher Launcher);
}
