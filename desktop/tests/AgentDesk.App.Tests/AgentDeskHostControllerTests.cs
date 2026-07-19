using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using AgentDesk.App.Bridge;
using AgentDesk.App.Cloud;
using AgentDesk.App.Notifications;
using AgentDesk.App.Recovery;
using AgentDesk.App.Settings;
using AgentDesk.App.Workspace;
using AgentDesk.Core.Engine;
using AgentDesk.Core.Execution;
using AgentDesk.Core.Providers;
using AgentDesk.Core.Security;
using AgentDesk.Engine.Acp;
using AgentDesk.Engine.Sidecar;

namespace AgentDesk.App.Tests;

public sealed class AgentDeskHostControllerTests
{
    private static readonly ProviderProfile RecoveryProvider = new(
        "https://recovery.example/v1",
        "grok-4.5",
        ProviderBackend.Responses);

    private static readonly AgentDeskHostOptions Options = new("C:\\workspace")
    {
        NativeEnginePath = "C:\\engine\\agentdesk-engine.exe",
        WslEnginePath = "/opt/agentdesk/agentdesk-engine",
        IsTrustedWorkspace = true,
        IsWslStrictAvailable = true,
    };

    [Fact]
    public async Task UiReady_ProjectsWorkspaceAndIdleEngineState()
    {
        var fixture = new ControllerFixture();
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(new UiReadyWebCommand());

        Assert.Collection(
            fixture.Events.Snapshot(),
            item => Assert.Equal(
                new UiPreferencesChangedWebEvent(UiPreferences.Default),
                item),
            item => Assert.Equal(new WorkspaceSelectedWebEvent(Options.WorkspacePath!, 1), item),
            item =>
            {
                var status = Assert.IsType<EngineStatusWebEvent>(item);
                Assert.Equal("idle", status.Status);
                Assert.Equal(
                    [ExecutionProfile.NativeProtected, ExecutionProfile.WslStrict],
                    status.ExecutionProfiles);
            });
    }

    [Fact]
    public async Task PromptWithAttachments_UsesTheImagePromptPathWithoutEchoingBase64()
    {
        const string data =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.InitializeCapabilities = Capabilities(imagePrompts: true);
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(new PromptWebCommand(
            "inspect",
            ExecutionProfile.NativeProtected,
            WorkspaceGeneration: 1,
            AttachmentItems: [new PromptAttachment("pixel.png", "image/png", data)]));

        var sent = Assert.Single(engine.AttachmentPrompts);
        Assert.Equal("inspect", sent.Text);
        Assert.Equal(data, Assert.Single(sent.Attachments).Base64Data);
        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => WebMessageProtocol.SerializeEvent(item).Contains(data, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PromptWithAttachments_FailsClosedWhenTheEngineDoesNotAdvertiseImages()
    {
        const string data =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(new PromptWebCommand(
            "inspect",
            ExecutionProfile.NativeProtected,
            WorkspaceGeneration: 1,
            AttachmentItems: [new PromptAttachment("pixel.png", "image/png", data)]));

        Assert.Empty(engine.AttachmentPrompts);
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "error" });
        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => WebMessageProtocol.SerializeEvent(item).Contains(data, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorktreeCreate_UsesTheSidecarWorkspaceAndMapsEveryOption()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("/mnt/c/workspace", "session-42");
        WorktreeCreateRequest? captured = null;
        var result = new WorktreeCreateResult(
            WorktreeCreateStatus.Creating,
            new SessionId("session-42"),
            "/tmp/agentdesk-worktrees/feature",
            "/mnt/c/workspace",
            "abc123");
        engine.CreateWorktreeHandler = (request, _) =>
        {
            captured = request;
            return Task.FromResult(result);
        };
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.WslStrict));
        fixture.Events.Clear();

        await controller.HandleAsync(
            new WorktreeCreateWebCommand(
                WorkspaceGeneration: 1,
                SessionId: "session-42",
                CopyMode: WorktreeCopyMode.Clean,
                GitReference: "feature/base",
                CopyIgnoredInBackground: true,
                IgnoredSkipPatterns: ["node_modules", "*.tmp"],
                CreationType: WorktreeCreationType.Git,
                Label: "feature"));

        Assert.NotNull(captured);
        Assert.Equal(new SessionId("session-42"), captured.SessionId);
        Assert.Equal("/mnt/c/workspace", captured.SourcePath);
        Assert.Null(captured.DestinationPath);
        Assert.Equal(WorktreeCopyMode.Clean, captured.CopyMode);
        Assert.Equal("feature/base", captured.GitReference);
        Assert.True(captured.CopyIgnoredInBackground);
        Assert.Equal(["node_modules", "*.tmp"], captured.IgnoredSkipPatterns);
        Assert.Equal(WorktreeCreationType.Git, captured.CreationType);
        Assert.Equal("feature", captured.Label);
        var created = Assert.Single(
            fixture.Events.Snapshot().OfType<WorktreeCreatedWebEvent>());
        Assert.Equal(1, created.WorkspaceGeneration);
        Assert.Equal(result, created.Result);
    }

    [Fact]
    public async Task WorktreeList_MapsRepositoryFiltersAndPublishesTheAuthoritativeList()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\engine-workspace", "session-42");
        WorktreeListRequest? captured = null;
        var worktree = Worktree("wt-list", "C:\\worktrees\\list", "session-42");
        engine.ListWorktreesHandler = (request, _) =>
        {
            captured = request;
            return Task.FromResult<IReadOnlyList<WorktreeRecord>>([worktree]);
        };
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new WorktreeListWebCommand(
                WorkspaceGeneration: 1,
                IncludeAll: true,
                Types: [WorktreeKind.Session, WorktreeKind.Manual]));

        Assert.NotNull(captured);
        Assert.Equal("C:\\engine-workspace", captured.Repository);
        Assert.Equal([WorktreeKind.Session, WorktreeKind.Manual], captured.Types);
        Assert.True(captured.IncludeAll);
        var changed = Assert.Single(
            fixture.Events.Snapshot().OfType<WorktreeListChangedWebEvent>());
        Assert.Equal(1, changed.WorkspaceGeneration);
        Assert.Equal(worktree, Assert.Single(changed.Worktrees));
    }

    [Fact]
    public async Task WorktreeShow_MapsTheIdentifierAndPublishesTheDetail()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        WorktreeShowRequest? captured = null;
        var worktree = Worktree("wt-show", "C:\\worktrees\\show", "session-42");
        engine.ShowWorktreeHandler = (request, _) =>
        {
            captured = request;
            return Task.FromResult<WorktreeRecord?>(worktree);
        };
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new WorktreeShowWebCommand(WorkspaceGeneration: 1, IdOrPath: "wt-show"));

        Assert.Equal(new WorktreeShowRequest("wt-show"), captured);
        var detail = Assert.Single(
            fixture.Events.Snapshot().OfType<WorktreeDetailWebEvent>());
        Assert.Equal(1, detail.WorkspaceGeneration);
        Assert.Equal(worktree, detail.Worktree);
    }

    [Fact]
    public async Task WorktreeApply_MapsTheActiveSessionPathAndMode()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        WorktreeApplyRequest? captured = null;
        var result = new WorktreeApplyResult(
            WorktreeApplyStatus.Success,
            [new WorktreeFileChange(
                "src/parser.cs",
                null,
                WorktreeChangeType.Edit,
                Staged: false,
                Additions: 3,
                Deletions: 1)],
            [],
            "C:\\workspace");
        engine.ApplyWorktreeHandler = (request, _) =>
        {
            captured = request;
            return Task.FromResult(result);
        };
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new WorktreeApplyWebCommand(
                WorkspaceGeneration: 1,
                SessionId: "session-42",
                WorktreePath: "C:\\worktrees\\feature",
                Mode: WorktreeApplyMode.Merge));

        Assert.Equal(
            new WorktreeApplyRequest(
                new SessionId("session-42"),
                "C:\\worktrees\\feature",
                WorktreeApplyMode.Merge),
            captured);
        var applied = Assert.Single(
            fixture.Events.Snapshot().OfType<WorktreeAppliedWebEvent>());
        Assert.Equal(1, applied.WorkspaceGeneration);
        Assert.Equal(result, applied.Result);
    }

    [Fact]
    public async Task WorktreeRemove_MapsSafetyFlagsAndPublishesTheResolvedResult()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        WorktreeRemoveRequest? captured = null;
        var result = new WorktreeRemoveResult(
            Removed: true,
            ResolvedPath: "C:\\worktrees\\obsolete");
        engine.RemoveWorktreeHandler = (request, _) =>
        {
            captured = request;
            return Task.FromResult(result);
        };
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new WorktreeRemoveWebCommand(
                WorkspaceGeneration: 1,
                IdOrPath: "wt-obsolete",
                Force: true,
                DryRun: false));

        Assert.Equal(
            new WorktreeRemoveRequest("wt-obsolete", Force: true, DryRun: false),
            captured);
        var removed = Assert.Single(
            fixture.Events.Snapshot().OfType<WorktreeRemovedWebEvent>());
        Assert.Equal(1, removed.WorkspaceGeneration);
        Assert.Equal("wt-obsolete", removed.IdOrPath);
        Assert.Equal(result, removed.Result);
    }

    [Fact]
    public async Task WorktreeGc_ConvertsWholeSecondsToTimeSpanAndMapsSafetyFlags()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        WorktreeGcRequest? captured = null;
        var result = new WorktreeGcResult(
            DeadRemoved: 2,
            ExpiredRemoved: 3,
            SkippedAlive: 5,
            RemoveFailed: 0);
        engine.GcWorktreesHandler = (request, _) =>
        {
            captured = request;
            return Task.FromResult(result);
        };
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new WorktreeGcWebCommand(
                WorkspaceGeneration: 1,
                DryRun: false,
                MaximumAgeSeconds: 86_401,
                Force: true));

        Assert.Equal(
            new WorktreeGcRequest(
                DryRun: false,
                MaximumAge: TimeSpan.FromSeconds(86_401),
                Force: true),
            captured);
        var completed = Assert.Single(
            fixture.Events.Snapshot().OfType<WorktreeGcCompletedWebEvent>());
        Assert.Equal(1, completed.WorkspaceGeneration);
        Assert.Equal(result, completed.Result);
    }

    [Fact]
    public async Task WorktreeOperations_RejectASecondOperationUntilTheFirstCompletes()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<WorktreeRecord>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ListWorktreesHandler = (_, _) =>
        {
            started.TrySetResult();
            return release.Task;
        };
        engine.ShowWorktreeHandler = (_, _) =>
            Task.FromResult<WorktreeRecord?>(
                Worktree("must-not-run", "C:\\worktrees\\must-not-run", "session-42"));
        await using var controller = fixture.CreateController(
            Options with { RuntimeOperationTimeout = TimeSpan.FromSeconds(30) });

        var first = controller.HandleAsync(
            new WorktreeListWebCommand(1, IncludeAll: false, Types: []));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.HandleAsync(
            new WorktreeShowWebCommand(1, "must-not-run"));

        var error = Assert.Single(
            fixture.Events.Snapshot().OfType<WorktreeErrorWebEvent>());
        Assert.Equal(WorktreeOperation.Show, error.Operation);
        Assert.DoesNotContain(engine.Calls, call => call.StartsWith("show-worktree:", StringComparison.Ordinal));

        release.TrySetResult([]);
        await first;
    }

    [Fact]
    public async Task WorktreeMutations_AreRejectedWhileAPromptIsRunning()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var promptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPrompt = new TaskCompletionSource<PromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PromptHandler = (_, _, _) =>
        {
            promptStarted.TrySetResult();
            return finishPrompt.Task;
        };
        await using var controller = fixture.CreateController();
        var prompt = controller.HandleAsync(
            new PromptWebCommand("keep running", ExecutionProfile.NativeProtected));
        await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Events.Clear();
        (WebCommand Command, WorktreeOperation Operation)[] commands =
        [
            (new WorktreeCreateWebCommand(
                1,
                "session-42",
                WorktreeCopyMode.Dirty,
                GitReference: null,
                CopyIgnoredInBackground: false,
                IgnoredSkipPatterns: [],
                CreationType: null,
                Label: null), WorktreeOperation.Create),
            (new WorktreeApplyWebCommand(
                1,
                "session-42",
                "C:\\worktrees\\feature",
                WorktreeApplyMode.Overwrite), WorktreeOperation.Apply),
            (new WorktreeRemoveWebCommand(
                1,
                "wt-feature",
                Force: false,
                DryRun: true), WorktreeOperation.Remove),
            (new WorktreeGcWebCommand(
                1,
                DryRun: true,
                MaximumAgeSeconds: null,
                Force: false), WorktreeOperation.Gc),
        ];

        foreach (var (command, _) in commands)
        {
            await controller.HandleAsync(command);
        }

        Assert.Equal(
            commands.Select(item => item.Operation),
            fixture.Events.Snapshot().OfType<WorktreeErrorWebEvent>().Select(error => error.Operation));
        Assert.DoesNotContain(
            engine.Calls,
            call => call.Contains("worktree", StringComparison.Ordinal));

        finishPrompt.TrySetResult(new PromptResult(EngineStopReason.EndTurn, "end_turn"));
        await prompt;
    }

    [Fact]
    public async Task BeginMaintenanceAsync_IsRejectedWhileAPromptIsRunning()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var promptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPrompt = new TaskCompletionSource<PromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PromptHandler = (_, _, _) =>
        {
            promptStarted.TrySetResult();
            return finishPrompt.Task;
        };
        await using var controller = fixture.CreateController();
        var prompt = controller.HandleAsync(
            new PromptWebCommand("keep running", ExecutionProfile.NativeProtected));
        await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.BeginMaintenanceAsync());

        finishPrompt.TrySetResult(new PromptResult(EngineStopReason.EndTurn, "end_turn"));
        await prompt;
    }

    [Fact]
    public async Task MaintenanceLease_BlocksPromptsUntilItIsReleased()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));

        await using (await controller.BeginMaintenanceAsync())
        {
            fixture.Events.Clear();
            await controller.HandleAsync(
                new PromptWebCommand("must not run", ExecutionProfile.NativeProtected));

            Assert.DoesNotContain("must not run", engine.PromptTexts);
            Assert.Contains(
                fixture.Events.Snapshot(),
                item => item is EngineStatusWebEvent { Status: "running", SessionId: "session-42" });
        }

        await controller.HandleAsync(
            new PromptWebCommand("runs after maintenance", ExecutionProfile.NativeProtected));
        Assert.Contains("runs after maintenance", engine.PromptTexts);
    }

    [Fact]
    public async Task MaintenanceLease_ExportsAndImportsThroughTheActiveEngine()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var exported = EngineSessionDocument.FromJson(
            "{\"schemaVersion\":1,\"sessionId\":\"session-42\"}");
        engine.ExportSessionHandler = (sessionId, _) =>
        {
            Assert.Equal("session-42", sessionId.Value);
            return Task.FromResult(exported);
        };
        engine.ImportSessionHandler = (document, workingDirectory, _) =>
        {
            Assert.Equal(exported.ExportUtf8Json(), document.ExportUtf8Json());
            Assert.Equal("C:\\workspace", workingDirectory);
            return Task.FromResult(new SessionId("session-imported"));
        };
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));

        await using (var lease = await controller.BeginMaintenanceAsync())
        {
            var document = await lease.ExportSessionAsync(new SessionId("session-42"));
            var imported = await lease.ImportSessionAsync(document);

            Assert.Same(exported, document);
            Assert.Equal("session-imported", imported.Value);
            Assert.Equal("C:\\workspace", lease.WorkspacePath);
        }

        await controller.HandleAsync(
            new PromptWebCommand("continue imported session", ExecutionProfile.NativeProtected));
        Assert.Contains("prompt:session-imported", engine.Calls);
    }

    [Fact]
    public async Task MaintenanceLease_ImportsOnColdStartThroughTheConfiguredEngine()
    {
        var fixture = new ControllerFixture();
        var provider = new ProviderProfile("https://api.example.test/v1", "grok-4.5");
        fixture.ProviderSettings.Profile = provider;
        fixture.Credentials.Save(provider.CredentialName, "provider-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-temporary");
        var document = EngineSessionDocument.FromJson(
            "{\"schemaVersion\":1,\"sessionId\":\"session-imported\"}");
        engine.ImportSessionHandler = (candidate, workingDirectory, _) =>
        {
            Assert.Equal(document.ExportUtf8Json(), candidate.ExportUtf8Json());
            Assert.Equal("C:\\workspace", workingDirectory);
            return Task.FromResult(new SessionId("session-imported"));
        };
        await using var controller = fixture.CreateController();

        await using (var lease = await controller.BeginMaintenanceAsync())
        {
            var imported = await lease.ImportSessionAsync(document);

            Assert.Equal("session-imported", imported.Value);
            Assert.Equal("C:\\workspace", lease.WorkspacePath);
        }

        var launch = Assert.Single(fixture.Factory.Launches);
        Assert.Equal(provider, launch.ProviderProfile);
        Assert.Equal("provider-test-key", launch.ApiKey);
        Assert.Contains("initialize", engine.Calls);
        Assert.Contains("authenticate", engine.Calls);
        Assert.Contains("new-session:C:\\workspace", engine.Calls);
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is SessionActiveChangedWebEvent
            {
                SessionId: "session-imported",
                WorkspacePath: "C:\\workspace",
            });
    }

    [Fact]
    public async Task MaintenanceLease_StopEngineAsyncCompletesBeforeReturning()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        var host = Assert.Single(fixture.Factory.Hosts);

        await using (var lease = await controller.BeginMaintenanceAsync())
        {
            await lease.StopEngineAsync();

            Assert.Equal(1, host.StopCount);
            Assert.Equal(1, host.DisposeCount);
        }
    }

    [Fact]
    public async Task CloudEngineLease_StartsColdBlocksPromptsAndActivatesImportedSession()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-temporary");
        await using var controller = fixture.CreateController();

        await using (var lease = await controller.BeginCloudEngineOperationAsync())
        {
            Assert.Same(engine, lease.Engine);
            Assert.Equal("C:\\workspace", lease.WorkspacePath);
            Assert.Equal("C:\\workspace", lease.EngineWorkspacePath);

            await controller.HandleAsync(
                new PromptWebCommand("must not run", ExecutionProfile.NativeProtected));
            Assert.DoesNotContain("must not run", engine.PromptTexts);

            await lease.ActivateSessionAsync(new SessionId("session-imported"));
        }

        await controller.HandleAsync(
            new PromptWebCommand("continue imported", ExecutionProfile.NativeProtected));
        Assert.Contains("prompt:session-imported", engine.Calls);
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is SessionActiveChangedWebEvent
            {
                SessionId: "session-imported",
                WorkspacePath: "C:\\workspace",
            });
    }

    [Fact]
    public async Task CloudSessionStatus_SerializesTheActivatedEngineEpoch()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "session-temporary");
        await using var controller = fixture.CreateController();

        await using (var lease = await controller.BeginCloudEngineOperationAsync())
        {
            await lease.ActivateSessionAsync(new SessionId("session-imported"));
        }

        var events = fixture.Events.Snapshot();
        var active = Assert.Single(events.OfType<SessionActiveChangedWebEvent>());
        var ready = Assert.Single(
            events.OfType<EngineStatusWebEvent>(),
            item => item is { Status: "ready", SessionId: "session-imported" });
        using var serialized = JsonDocument.Parse(WebMessageProtocol.SerializeEvent(ready));

        Assert.Equal(
            active.EngineEpoch,
            serialized.RootElement.GetProperty("engineEpoch").GetInt64());
    }

    [Fact]
    public async Task EngineWithoutWorktreeCapability_PublishesAGenericErrorWithoutLeakingDetails()
    {
        const string sensitiveDetail = "SECRET_WORKTREE_CAPABILITY";
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        fixture.UiPreferences.Preferences = UiPreferences.Default with { Language = "en-US" };
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.ListWorktreesHandler = (_, _) =>
            Task.FromException<IReadOnlyList<WorktreeRecord>>(
                new NotSupportedException(sensitiveDetail));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new WorktreeListWebCommand(1, IncludeAll: false, Types: []));

        var error = Assert.Single(
            fixture.Events.Snapshot().OfType<WorktreeErrorWebEvent>());
        Assert.Equal(1, error.WorkspaceGeneration);
        Assert.Equal(WorktreeOperation.List, error.Operation);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.DoesNotContain(sensitiveDetail, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorktreeListFailure_RemainsLocalAndPreservesReadyEngineStatus()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.ListWorktreesHandler = (_, _) =>
            Task.FromException<IReadOnlyList<WorktreeRecord>>(
                new InvalidOperationException("git worktree list failed"));
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(
            new WorktreeListWebCommand(1, IncludeAll: false, Types: []));

        var events = fixture.Events.Snapshot();
        Assert.Single(events.OfType<WorktreeErrorWebEvent>());
        Assert.DoesNotContain(events, item => item is EngineStatusWebEvent { Status: "error" });

        fixture.Events.Clear();
        await controller.HandleAsync(new UiReadyWebCommand());
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "ready", SessionId: "session-42" });
    }

    [Fact]
    public async Task WorktreeList_DropsALateResultAfterTheWorkspaceChanges()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<WorktreeRecord>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ListWorktreesHandler = (_, _) =>
        {
            started.TrySetResult();
            return release.Task;
        };
        await using var controller = fixture.CreateController(
            Options with { RuntimeOperationTimeout = TimeSpan.FromSeconds(30) });

        var request = controller.HandleAsync(
            new WorktreeListWebCommand(1, IncludeAll: false, Types: []));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await controller.UpdateWorkspaceAsync("C:\\other"));
        fixture.Events.Clear();
        release.TrySetResult(
            [Worktree("wt-late", "C:\\worktrees\\late", "session-42")]);
        await request;

        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => item is WorktreeListChangedWebEvent or WorktreeErrorWebEvent);
    }

    [Fact]
    public async Task WorktreeShow_DropsALateResultAfterTheActiveSessionChanges()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WorktreeRecord?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ShowWorktreeHandler = (_, _) =>
        {
            started.TrySetResult();
            return release.Task;
        };
        await using var controller = fixture.CreateController(
            Options with { RuntimeOperationTimeout = TimeSpan.FromSeconds(30) });
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        var request = controller.HandleAsync(new WorktreeShowWebCommand(1, "wt-late"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.HandleAsync(
            new SessionOpenWebCommand(
                "saved-session",
                "C:\\workspace",
                ExecutionProfile.NativeProtected));
        fixture.Events.Clear();
        release.TrySetResult(
            Worktree("wt-late", "C:\\worktrees\\late", "session-42"));
        await request;

        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => item is WorktreeDetailWebEvent or WorktreeErrorWebEvent);
    }

    [Fact]
    public async Task WorktreeList_DropsALateResultAfterTheEngineChanges()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var native = fixture.Factory.EnqueueEngine("C:\\workspace", "native-session");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<WorktreeRecord>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        native.ListWorktreesHandler = (_, _) =>
        {
            started.TrySetResult();
            return release.Task;
        };
        var wsl = fixture.Factory.EnqueueEngine("/mnt/c/workspace", "wsl-session");
        await using var controller = fixture.CreateController(
            Options with { RuntimeOperationTimeout = TimeSpan.FromSeconds(30) });

        var request = controller.HandleAsync(
            new WorktreeListWebCommand(1, IncludeAll: false, Types: []));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.HandleAsync(
            new PromptWebCommand("switch engine", ExecutionProfile.WslStrict));
        fixture.Events.Clear();
        release.TrySetResult(
            [Worktree("wt-late", "C:\\worktrees\\late", "native-session")]);
        await request;

        Assert.Equal(["switch engine"], wsl.PromptTexts);
        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => item is WorktreeListChangedWebEvent or WorktreeErrorWebEvent);
    }

    [Fact]
    public async Task RuntimeCommands_ListDropsAResultFromAnOldWorkspaceGeneration()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<RuntimeCommand>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ListRuntimeCommandsHandler = (_, _) =>
        {
            started.TrySetResult();
            return release.Task;
        };
        await using var controller = fixture.CreateController();

        var request = controller.HandleAsync(new RuntimeCommandsListWebCommand(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await controller.UpdateWorkspaceAsync("C:\\other"));
        release.TrySetResult([new RuntimeCommand("review", "Review the workspace")]);
        await request;

        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => item is RuntimeCommandsChangedWebEvent or RuntimeCommandsErrorWebEvent);
    }

    [Fact]
    public async Task RuntimeCommands_ListProjectsDescriptionsInputsAndSkillScope()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var command = new RuntimeCommand(
            "skill",
            "Run a repository skill",
            new RuntimeCommandInput("skill-name"),
            new RuntimeSkillMetadata(RuntimeSkillScope.Repo, "C:\\workspace\\.agents\\skill.md"));
        engine.ListRuntimeCommandsHandler = (_, _) =>
            Task.FromResult<IReadOnlyList<RuntimeCommand>>([command]);
        engine.InitializeCapabilities = Capabilities(
            imagePrompts: true,
            sessionModes: [SessionMode.Default, SessionMode.Plan]);
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(new RuntimeCommandsListWebCommand(1));

        var changed = Assert.Single(
            fixture.Events.Snapshot().OfType<RuntimeCommandsChangedWebEvent>());
        Assert.Equal(1, changed.WorkspaceGeneration);
        Assert.Equal(command, Assert.Single(changed.Commands));
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is EngineCapabilitiesChangedWebEvent
            {
                SessionId: "session-42",
                ImagePrompts: true,
            });
    }

    [Fact]
    public async Task RuntimeCommands_ListReportsStartupFailureForTheCurrentWorkspaceGeneration()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        fixture.UiPreferences.Preferences = UiPreferences.Default with { Language = "en-US" };
        fixture.Factory.EnqueueFailure(new InvalidOperationException("startup failed"));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(new RuntimeCommandsListWebCommand(1));

        var error = Assert.Single(
            fixture.Events.Snapshot().OfType<RuntimeCommandsErrorWebEvent>());
        Assert.Equal(1, error.WorkspaceGeneration);
        Assert.Equal("Runtime commands could not be loaded.", error.Message);
        Assert.DoesNotContain("startup failed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryFlush_DropsCompletionAfterTheActiveSessionChanges()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.FlushMemoryHandler = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
        };
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        var flush = controller.HandleAsync(new MemoryFlushWebCommand("session-42"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await controller.UpdateWorkspaceAsync("C:\\other"));
        release.TrySetResult();
        await flush;

        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => item is MemoryFlushStatusWebEvent { Status: "succeeded" or "error" });
    }

    [Fact]
    public async Task MemoryBrowser_RoutesListReadWriteAndDeleteThroughTheActiveSession()
    {
        const string requestId = "865b214e-9411-43f6-a3a0-0cff2f52b5a2";
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var file = new MemoryFileDescriptor(
            new MemoryFileId("workspace"),
            MemoryFileScope.Workspace,
            "MEMORY.md",
            9,
            DateTimeOffset.Parse("2026-07-19T08:30:00Z"),
            Writable: true);
        engine.InitializeCapabilities = Capabilities() with
        {
            Memory = new MemoryManagementCapabilities(
                1,
                List: true,
                Read: true,
                Write: true,
                Delete: true,
                MutationConfirmationRequired: true),
        };
        engine.ListMemoryFilesHandler = (sessionId, _) =>
        {
            Assert.Equal("session-42", sessionId.Value);
            return Task.FromResult(new MemoryFileListing([file], Truncated: false));
        };
        engine.ReadMemoryFileHandler = (sessionId, fileId, _) =>
        {
            Assert.Equal("session-42", sessionId.Value);
            Assert.Equal("workspace", fileId.Value);
            return Task.FromResult(new MemoryFileDocument(file, "# Memory\n"));
        };
        engine.WriteMemoryFileHandler = (sessionId, fileId, content, confirmed, _) =>
        {
            Assert.Equal("session-42", sessionId.Value);
            Assert.Equal("workspace", fileId.Value);
            Assert.Equal("updated", content);
            Assert.True(confirmed);
            return Task.FromResult(new MemoryMutationResult(
                MemoryMutationStatus.Success,
                "sidecar write text",
                file));
        };
        engine.DeleteMemoryFileHandler = (sessionId, fileId, confirmed, _) =>
        {
            Assert.Equal("session-42", sessionId.Value);
            Assert.Equal("workspace", fileId.Value);
            Assert.True(confirmed);
            return Task.FromResult(new MemoryMutationResult(
                MemoryMutationStatus.Success,
                "sidecar delete text",
                File: null));
        };
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(new MemoryListWebCommand(requestId, 1, "session-42"));
        await controller.HandleAsync(new MemoryReadWebCommand(
            Guid.NewGuid().ToString("D"), 1, "session-42", new MemoryFileId("workspace")));
        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "updated",
            confirmed: false));
        var writeToken = MemoryConfirmationToken(
            fixture.Events.Snapshot().OfType<MemoryMutationWebEvent>().Last());
        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "updated",
            confirmed: true,
            confirmationToken: writeToken));
        await controller.HandleAsync(ParseMemoryDeleteCommand(
            Guid.NewGuid().ToString("D"),
            confirmed: true));
        var deleteToken = MemoryConfirmationToken(
            fixture.Events.Snapshot().OfType<MemoryMutationWebEvent>().Last());
        await controller.HandleAsync(ParseMemoryDeleteCommand(
            Guid.NewGuid().ToString("D"),
            confirmed: true,
            confirmationToken: deleteToken));

        Assert.Single(fixture.Events.Snapshot().OfType<MemoryListedWebEvent>());
        Assert.Single(fixture.Events.Snapshot().OfType<MemoryDocumentWebEvent>());
        var mutations = fixture.Events.Snapshot().OfType<MemoryMutationWebEvent>().ToArray();
        Assert.Equal(4, mutations.Length);
        Assert.Equal(MemoryMutationStatus.ConfirmationRequired, mutations[0].Result.Status);
        Assert.Equal(MemoryMutationStatus.Success, mutations[1].Result.Status);
        Assert.Equal(MemoryMutationStatus.ConfirmationRequired, mutations[2].Result.Status);
        Assert.Equal(MemoryMutationStatus.Success, mutations[3].Result.Status);
        Assert.All(
            mutations,
            mutation => Assert.DoesNotContain(
                "sidecar",
                mutation.Result.Message,
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is MemoryCapabilitiesWebEvent
            {
                Capabilities.SchemaVersion: 1,
            });
    }

    [Fact]
    public async Task MemoryMutation_RequiresAHostChallengeEvenWhenTheWebClaimsConfirmation()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.InitializeCapabilities = MemoryCapabilities();
        engine.WriteMemoryFileHandler = (_, _, _, _, _) => Task.FromResult(new MemoryMutationResult(
            MemoryMutationStatus.Success,
            "sidecar write text",
            File: null));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "updated",
            confirmed: true));

        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("write-memory:", StringComparison.Ordinal));
        var mutation = Assert.Single(
            fixture.Events.Snapshot().OfType<MemoryMutationWebEvent>());
        Assert.Equal(MemoryMutationStatus.ConfirmationRequired, mutation.Result.Status);
        Assert.Equal(64, MemoryConfirmationToken(mutation).Length);
    }

    [Fact]
    public async Task MemoryMutation_ConsumesAChallengeOnWrongContentAndRejectsReplay()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.InitializeCapabilities = MemoryCapabilities();
        engine.WriteMemoryFileHandler = (_, _, _, _, _) => Task.FromResult(new MemoryMutationResult(
            MemoryMutationStatus.Success,
            "sidecar write text",
            File: null));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "original",
            confirmed: false));
        var token = MemoryConfirmationToken(
            Assert.Single(fixture.Events.Snapshot().OfType<MemoryMutationWebEvent>()));
        fixture.Events.Clear();

        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "changed",
            confirmed: true,
            confirmationToken: token));
        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "original",
            confirmed: true,
            confirmationToken: token));

        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("write-memory:", StringComparison.Ordinal));
        Assert.Equal(2, fixture.Events.Snapshot().OfType<MemoryErrorWebEvent>().Count());
    }

    [Fact]
    public async Task MemoryMutation_RejectsAChallengeReplayAfterSuccess()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.InitializeCapabilities = MemoryCapabilities();
        engine.WriteMemoryFileHandler = (_, _, _, _, _) => Task.FromResult(new MemoryMutationResult(
            MemoryMutationStatus.Success,
            "sidecar write text",
            File: null));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "updated",
            confirmed: false));
        var token = MemoryConfirmationToken(
            Assert.Single(fixture.Events.Snapshot().OfType<MemoryMutationWebEvent>()));
        fixture.Events.Clear();

        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "updated",
            confirmed: true,
            confirmationToken: token));
        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "updated",
            confirmed: true,
            confirmationToken: token));

        Assert.Single(
            engine.Calls,
            call => call.StartsWith("write-memory:", StringComparison.Ordinal));
        Assert.Single(fixture.Events.Snapshot().OfType<MemoryMutationWebEvent>());
        Assert.Single(fixture.Events.Snapshot().OfType<MemoryErrorWebEvent>());
    }

    [Fact]
    public async Task MemoryMutation_RejectsAChallengeBoundToAnotherOperationOrFile()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.InitializeCapabilities = MemoryCapabilities();
        engine.WriteMemoryFileHandler = (_, _, _, _, _) => Task.FromResult(new MemoryMutationResult(
            MemoryMutationStatus.Success,
            "sidecar write text",
            File: null));
        engine.DeleteMemoryFileHandler = (_, _, _, _) => Task.FromResult(new MemoryMutationResult(
            MemoryMutationStatus.Success,
            "sidecar delete text",
            File: null));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "updated",
            confirmed: false));
        var operationToken = MemoryConfirmationToken(
            Assert.Single(fixture.Events.Snapshot().OfType<MemoryMutationWebEvent>()));
        fixture.Events.Clear();
        await controller.HandleAsync(ParseMemoryDeleteCommand(
            Guid.NewGuid().ToString("D"),
            confirmed: true,
            confirmationToken: operationToken));
        Assert.Single(fixture.Events.Snapshot().OfType<MemoryErrorWebEvent>());

        fixture.Events.Clear();
        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "updated",
            confirmed: false));
        var fileToken = MemoryConfirmationToken(
            Assert.Single(fixture.Events.Snapshot().OfType<MemoryMutationWebEvent>()));
        fixture.Events.Clear();
        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            fileId: "global",
            content: "updated",
            confirmed: true,
            confirmationToken: fileToken));

        Assert.Single(fixture.Events.Snapshot().OfType<MemoryErrorWebEvent>());
        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("write-memory:", StringComparison.Ordinal) ||
                call.StartsWith("delete-memory:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MemoryMutation_RejectsAChallengeAfterTheEngineGenerationChanges()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var nativeEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        nativeEngine.InitializeCapabilities = MemoryCapabilities();
        var wslEngine = fixture.Factory.EnqueueEngine("/mnt/c/workspace", "session-42");
        wslEngine.InitializeCapabilities = MemoryCapabilities();
        wslEngine.WriteMemoryFileHandler = (_, _, _, _, _) => Task.FromResult(
            new MemoryMutationResult(
                MemoryMutationStatus.Success,
                "sidecar write text",
                File: null));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "updated",
            confirmed: false));
        var token = MemoryConfirmationToken(
            Assert.Single(fixture.Events.Snapshot().OfType<MemoryMutationWebEvent>()));
        await controller.HandleAsync(new PromptWebCommand(
            "restart in WSL",
            ExecutionProfile.WslStrict,
            WorkspaceGeneration: 1));
        fixture.Events.Clear();

        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "updated",
            confirmed: true,
            confirmationToken: token));

        Assert.DoesNotContain(
            wslEngine.Calls,
            call => call.StartsWith("write-memory:", StringComparison.Ordinal));
        Assert.Single(fixture.Events.Snapshot().OfType<MemoryErrorWebEvent>());
    }

    [Fact]
    public async Task MemoryMutation_RejectsAChallengeAfterTheWorkspaceGenerationChanges()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.InitializeCapabilities = MemoryCapabilities();
        engine.WriteMemoryFileHandler = (_, _, _, _, _) => Task.FromResult(new MemoryMutationResult(
            MemoryMutationStatus.Success,
            "sidecar write text",
            File: null));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "updated",
            confirmed: false));
        var token = MemoryConfirmationToken(
            Assert.Single(fixture.Events.Snapshot().OfType<MemoryMutationWebEvent>()));
        Assert.True(await controller.UpdateWorkspaceAsync("C:\\workspace"));
        fixture.Events.Clear();

        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "updated",
            confirmed: true,
            confirmationToken: token,
            workspaceGeneration: 2));

        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("write-memory:", StringComparison.Ordinal));
        Assert.Single(fixture.Events.Snapshot().OfType<MemoryErrorWebEvent>());
    }

    [Fact]
    public async Task MemoryMutation_HoldsTheIdleLeaseAgainstPromptsWorktreesAndOtherMutations()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.InitializeCapabilities = MemoryCapabilities();
        var mutationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishMutation = new TaskCompletionSource<MemoryMutationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.WriteMemoryFileHandler = (_, _, _, _, _) => Task.FromResult(
            new MemoryMutationResult(
                MemoryMutationStatus.Success,
                "sidecar write text",
                File: null));
        engine.RemoveWorktreeHandler = (_, _) => Task.FromResult(
            new WorktreeRemoveResult(Removed: true, ResolvedPath: "C:\\worktree"));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            fileId: "workspace",
            content: "first",
            confirmed: false));
        var firstToken = MemoryConfirmationToken(
            fixture.Events.Snapshot().OfType<MemoryMutationWebEvent>().Last());
        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            fileId: "global",
            content: "second",
            confirmed: false));
        var secondToken = MemoryConfirmationToken(
            fixture.Events.Snapshot().OfType<MemoryMutationWebEvent>().Last());
        fixture.Events.Clear();
        engine.WriteMemoryFileHandler = (_, _, _, _, _) =>
        {
            mutationStarted.TrySetResult();
            return finishMutation.Task;
        };

        var firstMutation = controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            fileId: "workspace",
            content: "first",
            confirmed: true,
            confirmationToken: firstToken));
        await mutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.HandleAsync(new PromptWebCommand(
            "blocked prompt",
            ExecutionProfile.NativeProtected,
            WorkspaceGeneration: 1));
        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            fileId: "global",
            content: "second",
            confirmed: true,
            confirmationToken: secondToken));
        await controller.HandleAsync(new WorktreeRemoveWebCommand(
            1,
            "worktree-1",
            Force: false,
            DryRun: false));
        await controller.HandleAsync(new MemoryFlushWebCommand("session-42"));

        Assert.DoesNotContain("blocked prompt", engine.PromptTexts);
        Assert.Single(
            engine.Calls,
            call => call.StartsWith("write-memory:", StringComparison.Ordinal));
        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("remove-worktree:", StringComparison.Ordinal));
        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("flush-memory:", StringComparison.Ordinal));

        finishMutation.TrySetResult(new MemoryMutationResult(
            MemoryMutationStatus.Success,
            "sidecar write text",
            File: null));
        await firstMutation;
        await controller.HandleAsync(new PromptWebCommand(
            "allowed prompt",
            ExecutionProfile.NativeProtected,
            WorkspaceGeneration: 1));
        Assert.Contains("allowed prompt", engine.PromptTexts);
    }

    [Fact]
    public async Task MemoryMutation_ReleasesTheIdleLeaseAfterFailureAndCancellation()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.InitializeCapabilities = MemoryCapabilities();
        engine.WriteMemoryFileHandler = (_, _, _, _, _) =>
            Task.FromException<MemoryMutationResult>(new InvalidOperationException("failed"));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "failure",
            confirmed: false));
        var failedToken = MemoryConfirmationToken(
            fixture.Events.Snapshot().OfType<MemoryMutationWebEvent>().Last());
        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "failure",
            confirmed: true,
            confirmationToken: failedToken));
        await controller.HandleAsync(new PromptWebCommand(
            "after failure",
            ExecutionProfile.NativeProtected,
            WorkspaceGeneration: 1));
        Assert.Contains("after failure", engine.PromptTexts);

        var cancellationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.WriteMemoryFileHandler = async (_, _, _, _, cancellationToken) =>
        {
            cancellationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        };
        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "cancel",
            confirmed: false));
        var cancellationToken = MemoryConfirmationToken(
            fixture.Events.Snapshot().OfType<MemoryMutationWebEvent>().Last());
        using var cancellation = new CancellationTokenSource();
        var cancelledMutation = controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "cancel",
            confirmed: true,
            confirmationToken: cancellationToken), cancellation.Token);
        await cancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledMutation);
        await controller.HandleAsync(new PromptWebCommand(
            "after cancellation",
            ExecutionProfile.NativeProtected,
            WorkspaceGeneration: 1));
        Assert.Contains("after cancellation", engine.PromptTexts);
    }

    [Fact]
    public async Task MemoryBrowser_DropsARequestForAStaleSessionBeforeContextCreation()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand(
            "initialize",
            ExecutionProfile.NativeProtected,
            WorkspaceGeneration: 1));
        fixture.Events.Clear();

        await controller.HandleAsync(new MemoryListWebCommand(
            Guid.NewGuid().ToString("D"),
            1,
            "stale-session"));

        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("list-memory:", StringComparison.Ordinal));
        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => item is MemoryListedWebEvent or MemoryErrorWebEvent);
    }

    [Fact]
    public async Task MemoryMutation_ConsumesAChallengePresentedByAStaleSessionWithoutPublishing()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.InitializeCapabilities = MemoryCapabilities();
        engine.WriteMemoryFileHandler = (_, _, _, _, _) => Task.FromResult(new MemoryMutationResult(
            MemoryMutationStatus.Success,
            "sidecar write text",
            File: null));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "updated",
            confirmed: false));
        var token = MemoryConfirmationToken(
            Assert.Single(fixture.Events.Snapshot().OfType<MemoryMutationWebEvent>()));
        fixture.Events.Clear();

        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "updated",
            confirmed: true,
            confirmationToken: token,
            sessionId: "stale-session"));
        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => item is MemoryMutationWebEvent or MemoryErrorWebEvent);

        await controller.HandleAsync(ParseMemoryWriteCommand(
            Guid.NewGuid().ToString("D"),
            content: "updated",
            confirmed: true,
            confirmationToken: token));

        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("write-memory:", StringComparison.Ordinal));
        Assert.Single(fixture.Events.Snapshot().OfType<MemoryErrorWebEvent>());
    }

    [Fact]
    public async Task MemoryBrowser_DropsCompletionAfterTheWorkspaceChanges()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ListMemoryFilesHandler = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new MemoryFileListing([], Truncated: false);
        };
        await using var controller = fixture.CreateController();

        var list = controller.HandleAsync(new MemoryListWebCommand(
            Guid.NewGuid().ToString("D"), 1, "session-42"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await controller.UpdateWorkspaceAsync("C:\\other"));
        release.TrySetResult();
        await list;

        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => item is MemoryListedWebEvent or MemoryErrorWebEvent);
    }

    [Fact]
    public async Task MemoryBrowser_ReportsALocalizedErrorWithoutSidecarDetails()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        fixture.UiPreferences.Preferences = UiPreferences.Default with { Language = "en-US" };
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.ReadMemoryFileHandler = (_, _, _) =>
            Task.FromException<MemoryFileDocument>(
                new InvalidOperationException("C:\\private\\provider-key.txt"));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(new MemoryReadWebCommand(
            Guid.NewGuid().ToString("D"),
            1,
            "session-42",
            new MemoryFileId("workspace")));

        var error = Assert.Single(fixture.Events.Snapshot().OfType<MemoryErrorWebEvent>());
        Assert.Equal("Memory files could not be read or updated.", error.Message);
        Assert.DoesNotContain("private", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider-key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preferences_SaveNormalizesUnavailableWslAndReportsLanguageRestart()
    {
        var fixture = new ControllerFixture();
        fixture.UiPreferences.Preferences = UiPreferences.Default;
        await using var controller = fixture.CreateController(
            Options with { IsWslStrictAvailable = false });

        await controller.HandleAsync(new SaveUiPreferencesWebCommand(new UiPreferences(
            "en-US",
            "continue",
            SessionMode.Plan,
            ExecutionProfile.WslStrict)));

        Assert.Equal(ExecutionProfile.NativeProtected, fixture.UiPreferences.Preferences.ExecutionProfile);
        var changed = Assert.Single(
            fixture.Events.Snapshot().OfType<UiPreferencesChangedWebEvent>());
        Assert.Equal("en-US", changed.Preferences.Language);
        Assert.True(changed.RestartRequired);
    }

    [Fact]
    public async Task InitialWorkspace_RejectsZeroGenerationAcknowledgement()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand(
                "stale acknowledgement",
                ExecutionProfile.NativeProtected,
                NativeRiskAcknowledged: true,
                WorkspaceGeneration: 0));

        Assert.Empty(fixture.Factory.Hosts);
        Assert.Empty(engine.Calls);

        await controller.HandleAsync(
            new PromptWebCommand(
                "current acknowledgement",
                ExecutionProfile.NativeProtected,
                NativeRiskAcknowledged: true,
                WorkspaceGeneration: 1));

        Assert.Single(fixture.Factory.Hosts);
        Assert.Equal(["current acknowledgement"], engine.PromptTexts);
    }

    [Fact]
    public async Task NoWorkspace_UiReadyDoesNotInventAPathAndPromptDoesNotStartSidecar()
    {
        var fixture = new ControllerFixture();
        await using var controller = fixture.CreateController(new AgentDeskHostOptions());

        await controller.HandleAsync(new UiReadyWebCommand());
        await controller.HandleAsync(
            new PromptWebCommand("must not run", ExecutionProfile.WslStrict));

        Assert.DoesNotContain(fixture.Events.Snapshot(), item => item is WorkspaceSelectedWebEvent);
        var error = Assert.Single(
            fixture.Events.Snapshot().OfType<EngineStatusWebEvent>(),
            item => item.Status == "error");
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Empty(fixture.Factory.Hosts);
    }

    [Fact]
    public async Task NativeProtected_UntrustedWorkspaceWithoutAcknowledgementDoesNotStartSidecar()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "native-session");
        await using var controller = fixture.CreateController(
            Options with { IsTrustedWorkspace = false });

        await controller.HandleAsync(
            new PromptWebCommand("must be confirmed", ExecutionProfile.NativeProtected));

        Assert.Empty(fixture.Factory.Hosts);
        Assert.Empty(engine.Calls);
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "error", Message: not null } status &&
                status.Message.Contains("确认", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NativeProtected_UntrustedWorkspaceWithAcknowledgementStartsSidecar()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "native-session");
        await using var controller = fixture.CreateController(
            Options with { IsTrustedWorkspace = false });

        await controller.HandleAsync(
            new PromptWebCommand(
                "confirmed native execution",
                ExecutionProfile.NativeProtected,
                NativeRiskAcknowledged: true));

        Assert.Single(fixture.Factory.Hosts);
        Assert.Equal(["confirmed native execution"], engine.PromptTexts);
    }

    [Fact]
    public async Task WslStrict_UnavailableDoesNotStartSidecar()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("/mnt/c/workspace", "wsl-session");
        await using var controller = fixture.CreateController(
            Options with { IsWslStrictAvailable = false });

        await controller.HandleAsync(
            new PromptWebCommand("must stay unavailable", ExecutionProfile.WslStrict));

        Assert.Empty(fixture.Factory.Hosts);
        Assert.Empty(engine.Calls);
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "error", Message: not null } status &&
                status.Message.Contains("不可用", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownExecutionProfile_DoesNotStartSidecar()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "unknown-session");
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand(
                "must stay rejected",
                (ExecutionProfile)int.MaxValue,
                NativeRiskAcknowledged: true));

        Assert.Empty(fixture.Factory.Hosts);
        Assert.Empty(engine.Calls);
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "error" });
    }

    [Fact]
    public async Task NoWorkspace_UpdateWorkspaceEnablesTheFirstPrompt()
    {
        const string selectedWorkspace = "D:\\selected-workspace";
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine(selectedWorkspace, "session-1");
        await using var controller = fixture.CreateController(new AgentDeskHostOptions());

        Assert.True(await controller.UpdateWorkspaceAsync(selectedWorkspace));
        await controller.HandleAsync(
            new PromptWebCommand(
                "now run",
                ExecutionProfile.NativeProtected,
                NativeRiskAcknowledged: true,
                WorkspaceGeneration: 1));

        Assert.Equal(selectedWorkspace, Assert.Single(fixture.Factory.Launches).WorkspacePath);
        Assert.Contains($"new-session:{selectedWorkspace}", engine.Calls);
    }

    [Fact]
    public async Task SaveProviderReplacement_FailureNeverEchoesTheNativeCredential()
    {
        const string nativeCredential = "synthetic-native-credential-from-exception";
        var fixture = new ControllerFixture();
        fixture.Credentials.SaveException = new InvalidOperationException(nativeCredential);
        await using var controller = fixture.CreateController();

        await controller.SaveProviderAsync(
            new SaveProviderWebCommand(
                new ProviderProfile("https://example.com/v1", "grok-4.5"),
                UseExistingCredential: false,
                ReplaceCredential: true),
            nativeCredential);

        var status = Assert.IsType<ProviderStatusWebEvent>(Assert.Single(fixture.Events.Snapshot()));
        Assert.Equal("error", status.Status);
        Assert.DoesNotContain(nativeCredential, status.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveProviderReplacement_RestartsTheSidecarBeforeTheNextPrompt()
    {
        var fixture = new ControllerFixture();
        var profile = new ProviderProfile("https://example.com/v1", "grok-4.5");
        fixture.ProviderSettings.Profile = profile;
        fixture.Credentials.Save(profile.CredentialName, "old-key");
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "old-session");
        var newEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "new-session");
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand("first", ExecutionProfile.NativeProtected));
        await controller.SaveProviderAsync(
            new SaveProviderWebCommand(
                profile,
                UseExistingCredential: false,
                ReplaceCredential: true),
            "new-key");
        await controller.HandleAsync(
            new PromptWebCommand("second", ExecutionProfile.NativeProtected));

        Assert.Equal(2, fixture.Factory.Launches.Count);
        Assert.Equal("old-key", fixture.Factory.Launches[0].ApiKey);
        Assert.Equal("new-key", fixture.Factory.Launches[1].ApiKey);
        Assert.Equal(1, fixture.Factory.Hosts[0].StopCount);
        Assert.Equal(1, fixture.Factory.Hosts[0].DisposeCount);
        Assert.Equal(["second"], newEngine.PromptTexts);
    }

    [Fact]
    public async Task UiReady_LoadsProviderSettingsAndReportsEndpointBoundCredentialState()
    {
        var fixture = new ControllerFixture();
        var profile = new ProviderProfile("https://example.com/v1", "grok-4.5");
        fixture.ProviderSettings.Profile = profile;
        fixture.Credentials.Save(profile.CredentialName, "endpoint-key");
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(new UiReadyWebCommand());

        var status = Assert.Single(fixture.Events.Snapshot().OfType<ProviderStatusWebEvent>());
        Assert.Equal("loaded", status.Status);
        Assert.Equal(profile.BaseUrl, status.BaseUrl);
        Assert.Equal(profile.Model, status.Model);
        Assert.True(status.HasCredential);
    }

    [Fact]
    public async Task SaveProvider_PersistsNonSecretSettingsAndEndpointBoundCredential()
    {
        var fixture = new ControllerFixture();
        var profile = new ProviderProfile("https://example.com/v1/", "grok-4.5");
        await using var controller = fixture.CreateController();

        await controller.SaveProviderAsync(
            new SaveProviderWebCommand(
                profile,
                UseExistingCredential: false,
                ReplaceCredential: true),
            "endpoint-key");

        Assert.Equal(profile, fixture.ProviderSettings.Profile);
        Assert.Equal("endpoint-key", fixture.Credentials.Read(profile.CredentialName));
        Assert.Null(fixture.Credentials.Read("xai"));
        var status = Assert.IsType<ProviderStatusWebEvent>(Assert.Single(fixture.Events.Snapshot()));
        Assert.Equal("saved", status.Status);
        Assert.True(status.HasCredential);
    }

    [Fact]
    public async Task SaveProvider_WhenSettingsFail_RestoresThePreviousCredentialAndProfile()
    {
        var fixture = new ControllerFixture();
        var previous = new ProviderProfile("https://previous.example/v1", "grok-4.5");
        var replacement = new ProviderProfile("https://replacement.example/v1", "grok-4.5", ProviderBackend.Responses);
        fixture.ProviderSettings.Profile = previous;
        fixture.Credentials.Save(previous.CredentialName, "previous-secret");
        fixture.ProviderSettings.SaveException = new IOException("settings unavailable");
        await using var controller = fixture.CreateController();

        await controller.SaveProviderAsync(
            new SaveProviderWebCommand(
                replacement,
                UseExistingCredential: false,
                ReplaceCredential: true),
            "replacement-secret");

        Assert.Equal(previous, fixture.ProviderSettings.Profile);
        Assert.Equal("previous-secret", fixture.Credentials.Read(previous.CredentialName));
        Assert.Null(fixture.Credentials.Read(replacement.CredentialName));
        var status = Assert.Single(fixture.Events.Snapshot().OfType<ProviderStatusWebEvent>());
        Assert.Equal("error", status.Status);
        Assert.Equal(previous.BaseUrl, status.BaseUrl);
        Assert.Equal(previous.Model, status.Model);
        Assert.Equal("chat_completions", status.Backend);
        Assert.True(status.HasCredential);
    }

    [Fact]
    public async Task SaveProvider_WhenCredentialReadFails_DoesNotOverwriteTheExistingCredential()
    {
        const string readFailure = "credential store unavailable";
        var fixture = new ControllerFixture();
        var previous = new ProviderProfile("https://example.com/v1", "grok-4.5");
        var replacement = new ProviderProfile("https://example.com/v1", "grok-4.6");
        Assert.Equal(previous.CredentialName, replacement.CredentialName);
        fixture.ProviderSettings.Profile = previous;
        fixture.Credentials.Save(previous.CredentialName, "old-secret");
        var saveCallCount = fixture.Credentials.SaveCalls.Count;
        fixture.Credentials.ReadException = new IOException(readFailure);
        await using var controller = fixture.CreateController();

        await controller.SaveProviderAsync(
            new SaveProviderWebCommand(
                replacement,
                UseExistingCredential: false,
                ReplaceCredential: true),
            "new-secret");

        Assert.Equal(previous, fixture.ProviderSettings.Profile);
        Assert.Equal("old-secret", fixture.Credentials.Peek(previous.CredentialName));
        Assert.Equal(saveCallCount, fixture.Credentials.SaveCalls.Count);
        Assert.Empty(fixture.Credentials.DeleteCalls);
        var status = Assert.Single(fixture.Events.Snapshot().OfType<ProviderStatusWebEvent>());
        Assert.Equal("error", status.Status);
        Assert.DoesNotContain(readFailure, status.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveProvider_WhenSettingsFailForTheSameCredentialName_RestoresTheOldSecret()
    {
        var fixture = new ControllerFixture();
        var previous = new ProviderProfile("https://example.com/v1", "grok-4.5");
        var replacement = new ProviderProfile(
            "https://example.com/v1",
            "grok-4.6",
            ProviderBackend.Responses);
        Assert.Equal(previous.CredentialName, replacement.CredentialName);
        fixture.ProviderSettings.Profile = previous;
        fixture.Credentials.Save(previous.CredentialName, "old-secret");
        fixture.ProviderSettings.SaveException = new IOException("settings unavailable");
        await using var controller = fixture.CreateController();

        await controller.SaveProviderAsync(
            new SaveProviderWebCommand(
                replacement,
                UseExistingCredential: false,
                ReplaceCredential: true),
            "new-secret");

        Assert.Equal(previous, fixture.ProviderSettings.Profile);
        Assert.Equal("old-secret", fixture.Credentials.Peek(previous.CredentialName));
        var status = Assert.Single(fixture.Events.Snapshot().OfType<ProviderStatusWebEvent>());
        Assert.Equal("error", status.Status);
        Assert.True(status.HasCredential);
    }

    [Fact]
    public async Task PlainHttpWithoutOptIn_DoesNotSendCredentialOrStartSidecar()
    {
        var fixture = new ControllerFixture();
        var profile = new ProviderProfile("http://example.com/v1", "grok-4.5");
        fixture.ProviderSettings.Profile = profile;
        fixture.Credentials.Save(profile.CredentialName, "must-not-leave-machine");
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand("blocked", ExecutionProfile.NativeProtected));

        Assert.Empty(fixture.Factory.Hosts);
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "error", Message: not null } status &&
                status.Message.Contains("HTTPS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Prompt_PassesProviderProfileAndItsBoundCredentialToTheSidecar()
    {
        var fixture = new ControllerFixture();
        var profile = new ProviderProfile("https://example.com/v1", "grok-4.5");
        fixture.ProviderSettings.Profile = profile;
        fixture.Credentials.Save(profile.CredentialName, "endpoint-key");
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "provider-session");
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand("run", ExecutionProfile.NativeProtected));

        var launch = Assert.Single(fixture.Factory.Launches);
        Assert.Equal(profile, launch.ProviderProfile);
        Assert.Equal("endpoint-key", launch.ApiKey);
    }

    [Fact]
    public async Task ReusingCredentialForAnotherEndpointFailsWithoutChangingTheProfile()
    {
        var fixture = new ControllerFixture();
        var first = new ProviderProfile("https://first.example/v1", "grok-4.5");
        var second = new ProviderProfile("https://second.example/v1", "grok-4.5");
        fixture.ProviderSettings.Profile = first;
        fixture.Credentials.Save(first.CredentialName, "first-key");
        await using var controller = fixture.CreateController();

        await controller.SaveProviderAsync(
            new SaveProviderWebCommand(
                second,
                UseExistingCredential: true,
                ReplaceCredential: false),
            replacementCredential: null);

        Assert.Equal(first, fixture.ProviderSettings.Profile);
        Assert.Null(fixture.Credentials.Read(second.CredentialName));
        var status = Assert.Single(fixture.Events.Snapshot().OfType<ProviderStatusWebEvent>());
        Assert.Equal("error", status.Status);
        Assert.Equal(first.BaseUrl, status.BaseUrl);
        Assert.True(status.HasCredential);
    }

    [Fact]
    public async Task ReusingCredentialForTheExactEndpointPersistsMetadataWithoutReplacingIt()
    {
        var fixture = new ControllerFixture();
        var profile = new ProviderProfile("https://example.com/v1", "grok-4.5");
        fixture.Credentials.Save(profile.CredentialName, "existing-key");
        await using var controller = fixture.CreateController();

        await controller.SaveProviderAsync(
            new SaveProviderWebCommand(
                profile,
                UseExistingCredential: true,
                ReplaceCredential: false),
            replacementCredential: null);

        Assert.Equal(profile, fixture.ProviderSettings.Profile);
        Assert.Equal("existing-key", fixture.Credentials.Read(profile.CredentialName));
        var status = Assert.Single(fixture.Events.Snapshot().OfType<ProviderStatusWebEvent>());
        Assert.Equal("saved", status.Status);
        Assert.True(status.HasCredential);
    }

    [Fact]
    public async Task MissingNativeReplacementCannotAlterProviderProfileOrCredential()
    {
        var fixture = new ControllerFixture();
        var previous = new ProviderProfile("https://previous.example/v1", "grok-4.5");
        var replacement = new ProviderProfile("https://replacement.example/v1", "grok-4.5");
        fixture.ProviderSettings.Profile = previous;
        fixture.Credentials.Save(previous.CredentialName, "previous-key");
        await using var controller = fixture.CreateController();

        await controller.SaveProviderAsync(
            new SaveProviderWebCommand(
                replacement,
                UseExistingCredential: false,
                ReplaceCredential: true),
            replacementCredential: null);

        Assert.Equal(previous, fixture.ProviderSettings.Profile);
        Assert.Equal("previous-key", fixture.Credentials.Read(previous.CredentialName));
        Assert.Null(fixture.Credentials.Read(replacement.CredentialName));
        var status = Assert.Single(fixture.Events.Snapshot().OfType<ProviderStatusWebEvent>());
        Assert.Equal("error", status.Status);
        Assert.Equal(previous.BaseUrl, status.BaseUrl);
    }

    [Fact]
    public async Task FirstPrompt_StartsAndInitializesSidecarStreamsTextAndCompletes()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("/mnt/c/workspace", "session-42");
        engine.PromptHandler = (sessionId, _, _) =>
        {
            engine.EmitText(sessionId, "first chunk");
            return Task.FromResult(new PromptResult(EngineStopReason.EndTurn, "end_turn"));
        };
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand("inspect the changes", ExecutionProfile.WslStrict));

        var launch = Assert.Single(fixture.Factory.Launches);
        Assert.Equal(Options.WorkspacePath, launch.WorkspacePath);
        Assert.Equal(ExecutionProfile.WslStrict, launch.ExecutionProfile);
        Assert.Equal(Options.WslEnginePath, launch.EnginePath);
        Assert.Equal("xai-test-key", launch.ApiKey);
        Assert.Equal(
            ["initialize", "authenticate", "new-session:/mnt/c/workspace", "prompt:session-42"],
            engine.Calls);
        var updateEvent = Assert.Single(
            fixture.Events.Snapshot().OfType<SessionUpdateWebEvent>());
        Assert.Equal("session-42", updateEvent.SessionId);
        Assert.Equal("agent_message_chunk", updateEvent.UpdateKind);
        Assert.Equal("first chunk", updateEvent.Text);
        Assert.True(updateEvent.EngineEpoch > 0);
        Assert.Equal(
            engine.LastUpdate.GetRawText(),
            Assert.IsType<JsonElement>(updateEvent.Update).GetRawText());
        Assert.Contains(
            new PromptCompletedWebEvent("session-42", "end_turn"),
            fixture.Events.Snapshot());
        var ready = Assert.IsType<EngineStatusWebEvent>(fixture.Events.Snapshot().Last());
        Assert.Equal("ready", ready.Status);
        Assert.Equal("session-42", ready.SessionId);
        Assert.Equal(updateEvent.EngineEpoch, ready.EngineEpoch);
    }

    [Fact]
    public async Task NotificationsEnabled_PublishesGenericCompletionAndPermissionStatus()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        fixture.UiPreferences.Preferences = UiPreferences.Default with
        {
            Language = "en-US",
            NotificationsEnabled = true,
        };
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand("sensitive prompt", ExecutionProfile.NativeProtected));

        var completed = Assert.Single(fixture.Notifications.Snapshot());
        Assert.Equal(AgentDeskNotificationKind.TaskCompleted, completed.Notification.Kind);
        Assert.Equal("session-1", completed.Notification.SessionId);
        Assert.Equal("en-US", completed.Language);
        Assert.DoesNotContain(
            "sensitive prompt",
            completed.Notification.ToString(),
            StringComparison.Ordinal);

        fixture.Notifications.Clear();
        engine.EmitPermission(CreatePermissionRequest("permission-1", "session-1"));
        var permission = await fixture.Notifications.WaitForAsync(
            AgentDeskNotificationKind.PermissionRequired,
            TimeSpan.FromSeconds(5));

        Assert.Equal("session-1", permission.Notification.SessionId);
        Assert.Equal("en-US", permission.Language);
        Assert.DoesNotContain("tool-1", permission.Notification.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotificationsDisabled_DoesNotPublishPromptCompletion()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand("done", ExecutionProfile.NativeProtected));

        Assert.Empty(fixture.Notifications.Snapshot());
    }

    [Fact]
    public async Task PromptFailure_NotificationFailureDoesNotChangeTheGenericErrorProjection()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        fixture.UiPreferences.Preferences = UiPreferences.Default with
        {
            Language = "en-US",
            NotificationsEnabled = true,
        };
        fixture.Notifications.Failure = new InvalidOperationException("SENSITIVE_NOTIFICATION");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        engine.PromptHandler = (_, _, _) =>
            Task.FromException<PromptResult>(new InvalidOperationException("SENSITIVE_PROMPT"));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand("will fail", ExecutionProfile.NativeProtected));

        var failed = Assert.Single(fixture.Notifications.Snapshot());
        Assert.Equal(AgentDeskNotificationKind.TaskFailed, failed.Notification.Kind);
        var error = Assert.Single(
            fixture.Events.Snapshot().OfType<EngineStatusWebEvent>(),
            item => item.Status == "error");
        Assert.DoesNotContain("SENSITIVE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanMode_IsConfirmedAfterSessionCreationAndBeforePrompt()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "plan-session");
        engine.InitializeCapabilities = Capabilities(
            sessionModes: [SessionMode.Default, SessionMode.Plan]);
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand(
                "make a plan",
                ExecutionProfile.NativeProtected,
                SessionMode: SessionMode.Plan));

        Assert.Equal(
            [
                "initialize",
                "authenticate",
                "new-session:C:\\workspace",
                "set-mode:plan",
                "prompt:plan-session",
            ],
            engine.Calls);
        Assert.Contains(
            new SessionModeChangedWebEvent(
                "plan-session",
                SessionMode.Plan,
                PlanAvailable: true),
            fixture.Events.Snapshot());
    }

    [Fact]
    public async Task UnsupportedPlanMode_NeverSendsThePrompt()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "default-session");
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand(
                "must not run",
                ExecutionProfile.NativeProtected,
                SessionMode: SessionMode.Plan));

        Assert.DoesNotContain(engine.Calls, call => call.StartsWith("prompt:", StringComparison.Ordinal));
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "error" });
    }

    [Fact]
    public async Task FailedPlanModeConfirmation_NeverSendsThePrompt()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "plan-session");
        engine.InitializeCapabilities = Capabilities(
            sessionModes: [SessionMode.Default, SessionMode.Plan]);
        engine.SetSessionModeHandler = (_, _, _) =>
            Task.FromException(new InvalidOperationException("mode failed"));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand(
                "must not run",
                ExecutionProfile.NativeProtected,
                SessionMode: SessionMode.Plan));

        Assert.Contains("set-mode:plan", engine.Calls);
        Assert.DoesNotContain(engine.Calls, call => call.StartsWith("prompt:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SessionList_WithoutWorkspacePublishesCorrelatedEmptyPageWithoutStartingEngine()
    {
        var fixture = new ControllerFixture();
        fixture.Factory.EnqueueEngine("C:\\unused-workspace", "unused-session");
        await using var controller = fixture.CreateController(new AgentDeskHostOptions());
        const string requestId = "11111111-1111-4111-8111-111111111111";

        await controller.HandleAsync(
            new SessionListWebCommand(requestId, string.Empty, null, 50));

        var events = fixture.Events.Snapshot();
        var listEvent = Assert.Single(events.OfType<SessionListChangedWebEvent>());
        Assert.Equal(requestId, listEvent.RequestId);
        Assert.Empty(listEvent.Sessions);
        Assert.Null(listEvent.NextCursor);
        Assert.DoesNotContain(events, item => item is SessionListErrorWebEvent);
        Assert.Empty(fixture.Factory.Hosts);
        Assert.Empty(fixture.Factory.Launches);
    }

    [Fact]
    public async Task SessionList_StartsTheEngineAndPublishesTheAuthoritativePage()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "new-session");
        var summary = Session("saved-session", "Saved task", "C:\\workspace");
        engine.ListSessionsHandler = (_, query, cursor, limit, _) =>
        {
            Assert.Equal("parser", query);
            Assert.Equal("cursor-1", cursor);
            Assert.Equal(50, limit);
            return Task.FromResult(new SessionPage([summary], "cursor-2"));
        };
        await using var controller = fixture.CreateController();
        const string requestId = "11111111-1111-4111-8111-111111111111";
        var command = new SessionListWebCommand(
            requestId,
            "parser",
            "cursor-1",
            50,
            false);

        await controller.HandleAsync(command);

        Assert.Contains("list-sessions:C:\\workspace:parser:cursor-1:50", engine.Calls);
        var listEvent = Assert.Single(fixture.Events.Snapshot().OfType<SessionListChangedWebEvent>());
        Assert.Equal(requestId, listEvent.RequestId);
        Assert.Equal("cursor-2", listEvent.NextCursor);
        Assert.Equal(summary, Assert.Single(listEvent.Sessions));
    }

    [Fact]
    public async Task SessionList_FailurePublishesOnlyTheCorrelatedListError()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "new-session");
        engine.ListSessionsHandler = (_, _, _, _, _) =>
            Task.FromException<SessionPage>(
                new InvalidOperationException("sensitive-sidecar-failure"));
        await using var controller = fixture.CreateController();
        const string requestId = "11111111-1111-4111-8111-111111111111";

        await controller.HandleAsync(
            new SessionListWebCommand(requestId, string.Empty, null, 50));

        var events = fixture.Events.Snapshot();
        var errorEvent = Assert.Single(events.OfType<SessionListErrorWebEvent>());
        Assert.Equal(requestId, errorEvent.RequestId);
        Assert.DoesNotContain(
            "sensitive-sidecar-failure",
            errorEvent.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            events,
            item => item is EngineStatusWebEvent { Status: "error" });
    }

    [Fact]
    public async Task SessionOpen_LoadsTheSavedSessionAndPublishesActiveState()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "new-session");
        engine.InitializeCapabilities = Capabilities(
            imagePrompts: true,
            sessionModes: [SessionMode.Default, SessionMode.Plan]);
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new SessionOpenWebCommand(
                "saved-session",
                "C:\\workspace",
                ExecutionProfile.NativeProtected));

        Assert.Contains("load-session:saved-session:C:\\workspace", engine.Calls);
        var active = Assert.Single(
            fixture.Events.Snapshot().OfType<SessionActiveChangedWebEvent>(),
            item => item is
            {
                SessionId: "saved-session",
                WorkspacePath: "C:\\workspace",
                EngineEpoch: > 0,
            });
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is EngineCapabilitiesChangedWebEvent
            {
                SessionId: "saved-session",
                ImagePrompts: true,
            });
        var ready = Assert.IsType<EngineStatusWebEvent>(fixture.Events.Snapshot().Last());
        Assert.Equal("ready", ready.Status);
        Assert.Equal("saved-session", ready.SessionId);
        Assert.Equal(active.EngineEpoch, ready.EngineEpoch);
    }

    [Fact]
    public async Task SessionOpen_ProjectsLoadReplayEventsBeforeLoadReturnsInOrder()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "new-session");
        engine.LoadSessionHandler = async (sessionId, _, _) =>
        {
            engine.EmitText(sessionId, "history-one");
            await Task.Yield();
            engine.EmitText(sessionId, "history-two");
        };
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new SessionOpenWebCommand(
                "saved-session",
                "C:\\workspace",
                ExecutionProfile.NativeProtected));

        var replay = fixture.Events.Snapshot()
            .OfType<SessionUpdateWebEvent>()
            .Where(item => item.SessionId == "saved-session")
            .ToArray();
        Assert.Equal(
            ["history-one", "history-two"],
            replay.Select(item => item.Text).OfType<string>());
        Assert.Equal(1, engine.EventReceivedAddCount);
        Assert.Equal(0, engine.EventReceivedRemoveCount);
        Assert.Equal(1, engine.EventReceivedSubscriberCount);
    }

    [Fact]
    public async Task SessionOpen_LoadFailureRemovesReplaySubscription()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "new-session");
        engine.LoadSessionHandler = async (sessionId, _, _) =>
        {
            engine.EmitText(sessionId, "partial-history");
            await Task.Yield();
            throw new InvalidOperationException("load failed");
        };
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new SessionOpenWebCommand(
                "saved-session",
                "C:\\workspace",
                ExecutionProfile.NativeProtected));

        Assert.Equal(1, engine.EventReceivedAddCount);
        Assert.Equal(1, engine.EventReceivedRemoveCount);
        Assert.Equal(0, engine.EventReceivedSubscriberCount);
        Assert.Equal(1, fixture.Factory.Hosts[0].DisposeCount);
        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => item is SessionUpdateWebEvent { SessionId: "saved-session" });
    }

    [Fact]
    public async Task SessionOpen_InitializeFailureDoesNotSubscribeEngineEvents()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "new-session");
        engine.InitializeHandler = _ =>
            Task.FromException<EngineCapabilities>(new InvalidOperationException("initialize failed"));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new SessionOpenWebCommand(
                "saved-session",
                "C:\\workspace",
                ExecutionProfile.NativeProtected));

        Assert.Equal(0, engine.EventReceivedAddCount);
        Assert.Equal(0, engine.EventReceivedRemoveCount);
        Assert.Equal(0, engine.EventReceivedSubscriberCount);
        Assert.Equal(1, fixture.Factory.Hosts[0].DisposeCount);
    }

    [Fact]
    public async Task ReplacingEngine_RemovesPreviousGenerationReplaySubscription()
    {
        var fixture = new ControllerFixture();
        var firstEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "new-session");
        firstEngine.LoadSessionHandler = (sessionId, _, _) =>
        {
            firstEngine.EmitText(sessionId, "first-history");
            return Task.CompletedTask;
        };
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new SessionOpenWebCommand(
                "first-session",
                "C:\\workspace",
                ExecutionProfile.NativeProtected));

        var secondEngine = fixture.Factory.EnqueueEngine("C:\\workspace-two", "new-session-two");
        secondEngine.LoadSessionHandler = (sessionId, _, _) =>
        {
            secondEngine.EmitText(sessionId, "second-history");
            return Task.CompletedTask;
        };
        Assert.True(await controller.UpdateWorkspaceAsync("C:\\workspace-two"));
        await controller.HandleAsync(
            new SessionOpenWebCommand(
                "second-session",
                "C:\\workspace-two",
                ExecutionProfile.NativeProtected));

        firstEngine.EmitText(new SessionId("first-session"), "stale-history");
        var replay = fixture.Events.Snapshot()
            .OfType<SessionUpdateWebEvent>()
            .Select(item => item.Text)
            .OfType<string>()
            .ToArray();
        Assert.Equal(["first-history", "second-history"], replay);
        Assert.Equal(1, firstEngine.EventReceivedAddCount);
        Assert.Equal(1, firstEngine.EventReceivedRemoveCount);
        Assert.Equal(0, firstEngine.EventReceivedSubscriberCount);
        Assert.Equal(1, secondEngine.EventReceivedAddCount);
        Assert.Equal(0, secondEngine.EventReceivedRemoveCount);
        Assert.Equal(1, secondEngine.EventReceivedSubscriberCount);
        Assert.Equal(1, fixture.Factory.Hosts[0].StopCount);
    }

    [Fact]
    public async Task Dispose_RemovesActiveReplaySubscription()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "new-session");
        engine.LoadSessionHandler = (sessionId, _, _) =>
        {
            engine.EmitText(sessionId, "history");
            return Task.CompletedTask;
        };
        var controller = fixture.CreateController();
        await controller.HandleAsync(
            new SessionOpenWebCommand(
                "saved-session",
                "C:\\workspace",
                ExecutionProfile.NativeProtected));

        await controller.DisposeAsync();

        Assert.Equal(1, engine.EventReceivedAddCount);
        Assert.Equal(1, engine.EventReceivedRemoveCount);
        Assert.Equal(0, engine.EventReceivedSubscriberCount);
        Assert.Equal(1, fixture.Factory.Hosts[0].StopCount);
        Assert.Equal(1, fixture.Factory.Hosts[0].DisposeCount);
    }

    [Fact]
    public async Task NotificationSessionOpen_ResolvesTheWorkspaceFromTheLocalIndex()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\indexed-workspace", "new-session");
        await fixture.SessionIndex.UpsertAsync(
            [Session("saved-session", "Saved task", "C:\\indexed-workspace")]);
        await using var controller = fixture.CreateController();

        var opened = await controller.OpenIndexedSessionAsync("saved-session");

        Assert.True(opened);
        Assert.Contains("load-session:saved-session:C:\\indexed-workspace", engine.Calls);
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is SessionActiveChangedWebEvent
            {
                SessionId: "saved-session",
                WorkspacePath: "C:\\indexed-workspace",
                EngineEpoch: > 0,
            });
    }

    [Fact]
    public async Task NotificationSessionOpen_IgnoresAStaleNotificationWithoutStartingTheEngine()
    {
        var fixture = new ControllerFixture();
        await using var controller = fixture.CreateController();

        var opened = await controller.OpenIndexedSessionAsync("missing-session");

        Assert.False(opened);
        Assert.Empty(fixture.Events.Snapshot());
    }

    [Fact]
    public async Task SessionRename_UsesTheEngineAndRefreshesTheCatalog()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "new-session");
        var renamed = Session("saved-session", "Renamed task", "C:\\workspace");
        engine.ListSessionsHandler = (_, _, _, _, _) =>
            Task.FromResult(new SessionPage([renamed]));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new SessionRenameWebCommand(
                "11111111-1111-4111-8111-111111111111",
                "saved-session",
                "Renamed task",
                "C:\\workspace"));

        Assert.Contains("rename-session:saved-session:Renamed task:C:\\workspace", engine.Calls);
        var listEvent = Assert.Single(fixture.Events.Snapshot().OfType<SessionListChangedWebEvent>());
        Assert.Null(listEvent.NextCursor);
        Assert.Null(listEvent.RequestId);
        Assert.Equal(renamed, Assert.Single(listEvent.Sessions));
    }

    [Fact]
    public async Task SessionRenameFailure_IsCorrelatedWithoutPublishingAnEngineError()
    {
        const string requestId = "55555555-5555-4555-8555-555555555555";
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        engine.RenameSessionHandler = (_, _, _, _) =>
            Task.FromException(new IOException("rename failed"));
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(
            new SessionRenameWebCommand(
                requestId,
                "saved-session",
                "Renamed task",
                "C:\\workspace"));

        var error = Assert.Single(
            fixture.Events.Snapshot().OfType<SessionOperationErrorWebEvent>());
        Assert.Equal(requestId, error.RequestId);
        Assert.Equal("rename", error.Operation);
        Assert.Equal("saved-session", error.SessionId);
        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "error" });
    }

    [Fact]
    public async Task SessionArchive_IsLocalReversibleAndSearchableWithoutDeletingEngineData()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "new-session");
        var summary = Session("saved-session", "Saved task", "C:\\workspace");
        engine.ListSessionsHandler = (_, _, _, _, _) =>
            Task.FromResult(new SessionPage([summary]));
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new SessionListWebCommand(
            "22222222-2222-4222-8222-222222222222",
            string.Empty,
            null,
            50));
        fixture.Events.Clear();

        await controller.HandleAsync(
            new SessionArchiveWebCommand(
                "22222222-2222-4222-8222-222222222222",
                "saved-session",
                Archived: true));
        await controller.HandleAsync(
            new SessionListWebCommand(
                "33333333-3333-4333-8333-333333333333",
                string.Empty,
                null,
                50,
                Archived: true));

        Assert.Contains(
            new SessionArchiveChangedWebEvent(
                "22222222-2222-4222-8222-222222222222",
                "saved-session",
                Archived: true),
            fixture.Events.Snapshot());
        var archivedPage = Assert.Single(
            fixture.Events.Snapshot().OfType<SessionListChangedWebEvent>());
        Assert.Equal("33333333-3333-4333-8333-333333333333", archivedPage.RequestId);
        Assert.Equal(summary, Assert.Single(archivedPage.Sessions));
        Assert.DoesNotContain(engine.Calls, call => call.StartsWith("delete-session", StringComparison.Ordinal));

        await controller.HandleAsync(
            new SessionArchiveWebCommand(
                "44444444-4444-4444-8444-444444444444",
                "saved-session",
                Archived: false));
        Assert.Contains(
            new SessionArchiveChangedWebEvent(
                "44444444-4444-4444-8444-444444444444",
                "saved-session",
                Archived: false),
            fixture.Events.Snapshot());
    }

    [Fact]
    public async Task SessionArchiveFailure_IsCorrelatedWithoutStoppingTheRunningPermission()
    {
        const string requestId = "44444444-4444-4444-8444-444444444444";
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        var promptStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPrompt = new TaskCompletionSource<PromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PromptHandler = (_, _, _) =>
        {
            promptStarted.TrySetResult();
            return finishPrompt.Task;
        };
        await fixture.SessionIndex.UpsertAsync(
            [Session("saved-session", "Saved task", "C:\\workspace")]);
        await using var controller = fixture.CreateController();
        var prompt = controller.HandleAsync(
            new PromptWebCommand("keep running", ExecutionProfile.NativeProtected));
        await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        engine.EmitPermission(CreatePermissionRequest("permission-1", "session-1"));
        _ = await fixture.Events.WaitForAsync<PermissionRequestedWebEvent>(
            item => item.RequestId == "permission-1",
            TimeSpan.FromSeconds(5));
        fixture.Events.Clear();
        fixture.SessionIndex.SetArchivedException = new IOException("index unavailable");

        await controller.HandleAsync(
            new SessionArchiveWebCommand(requestId, "saved-session", Archived: true));

        var error = Assert.Single(
            fixture.Events.Snapshot().OfType<SessionOperationErrorWebEvent>());
        Assert.Equal(requestId, error.RequestId);
        Assert.Equal("archive", error.Operation);
        Assert.Equal("saved-session", error.SessionId);
        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "error" });
        await controller.HandleAsync(
            new PermissionRespondWebCommand(
                "permission-1",
                PermissionDecision.Selected("allow-once")));
        Assert.Contains(
            ("permission-1", PermissionDecision.Selected("allow-once")),
            engine.PermissionResponses);

        finishPrompt.TrySetResult(new PromptResult(EngineStopReason.EndTurn, "end_turn"));
        await prompt;
    }

    [Fact]
    public async Task SessionFork_UsesPersistedEngineStateAndPublishesCopyEvidence()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "new-session");
        var result = new SessionForkResult(
            new SessionId("fork-session"),
            "C:\\workspace",
            "saved-session",
            8,
            20,
            PlanStateCopied: true,
            ModelId: "grok-4.5");
        engine.ForkSessionHandler = (_, _, _, _, _, _, _, _) => Task.FromResult(result);
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new SessionForkWebCommand(
                "saved-session",
                "C:\\workspace",
                "C:\\workspace",
                TargetPromptIndex: 3));

        Assert.Contains("fork-session:saved-session:C:\\workspace:C:\\workspace:3:fork", engine.Calls);
        Assert.Contains(new SessionForkedWebEvent(result), fixture.Events.Snapshot());
    }

    [Fact]
    public async Task ActiveSession_CanCompactInspectAndExecuteARewind()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var point = new SessionRewindPoint(
            3,
            DateTimeOffset.Parse("2026-07-16T09:30:00Z"),
            2,
            HasFileChanges: true,
            PromptPreview: "Refactor parser");
        var result = new SessionRewindResult(
            Success: true,
            3,
            SessionRewindMode.ConversationOnly,
            RevertedFiles: [],
            CleanFiles: ["src/parser.rs"],
            Conflicts: [],
            PromptText: "Refactor parser");
        engine.RewindPointsHandler = (_, _) => Task.FromResult<IReadOnlyList<SessionRewindPoint>>([point]);
        engine.RewindSessionHandler = (_, _, _, _, _) => Task.FromResult(result);
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(
            new SessionCompactWebCommand("session-42", "Keep the API contract"));
        await controller.HandleAsync(new SessionRewindPointsWebCommand("session-42"));
        await controller.HandleAsync(
            new SessionRewindWebCommand(
                "session-42",
                3,
                SessionRewindMode.ConversationOnly,
                Force: false));

        Assert.Contains("compact-session:session-42:Keep the API contract", engine.Calls);
        Assert.Contains("rewind-points:session-42", engine.Calls);
        Assert.Contains("rewind-session:session-42:3:conversationonly:False", engine.Calls);
        Assert.Contains(new SessionCompactedWebEvent("session-42"), fixture.Events.Snapshot());
        Assert.Contains(
            fixture.Events.Snapshot().OfType<SessionRewindPointsWebEvent>(),
            item => item.SessionId == "session-42" && Assert.Single(item.Points) == point);
        Assert.Contains(new SessionRewoundWebEvent("session-42", result), fixture.Events.Snapshot());
    }

    [Fact]
    public async Task RewindPointsFailure_PublishesLocalErrorWithoutChangingReadyEngineStatus()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.RewindPointsHandler = (_, _) =>
            Task.FromException<IReadOnlyList<SessionRewindPoint>>(
                new NotSupportedException("rewind points unavailable"));
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(new SessionRewindPointsWebCommand("session-42"));

        var events = fixture.Events.Snapshot();
        Assert.DoesNotContain(events, item => item is SessionRewindPointsWebEvent);
        var error = Assert.Single(events.OfType<SessionRewindPointsErrorWebEvent>());
        Assert.Equal("session-42", error.SessionId);
        Assert.NotEmpty(error.Message);
        Assert.DoesNotContain("rewind points unavailable", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(events, item => item is EngineStatusWebEvent { Status: "error" });

        fixture.Events.Clear();
        await controller.HandleAsync(new UiReadyWebCommand());
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "ready", SessionId: "session-42" });
    }

    [Fact]
    public async Task ActiveSession_RuntimeDashboardProjectsAuthoritativeTasksAndSubagents()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var backgroundTask = BackgroundTask("task-7", "session-42");
        var subagent = RunningSubagent("subagent-7", "session-42");
        engine.ListBackgroundTasksHandler = (_, _) =>
            Task.FromResult<IReadOnlyList<BackgroundTaskSnapshot>>([backgroundTask]);
        engine.ListRunningSubagentsHandler = (_, _) =>
            Task.FromResult<IReadOnlyList<SubagentSnapshot>>([subagent]);
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(new RuntimeDashboardRefreshWebCommand("session-42"));

        Assert.Contains("list-background-tasks:session-42", engine.Calls);
        Assert.Contains("list-running-subagents:session-42", engine.Calls);
        var dashboard = Assert.Single(
            fixture.Events.Snapshot().OfType<RuntimeDashboardChangedWebEvent>());
        Assert.Equal(backgroundTask, Assert.Single(dashboard.BackgroundTasks));
        Assert.Equal(subagent, Assert.Single(dashboard.Subagents));
    }

    [Fact]
    public async Task RuntimeDashboard_RemainsAvailableWhileThePromptIsRunning()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var promptStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPrompt = new TaskCompletionSource<PromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PromptHandler = (_, _, cancellationToken) =>
        {
            promptStarted.TrySetResult();
            return finishPrompt.Task.WaitAsync(cancellationToken);
        };
        engine.ListBackgroundTasksHandler = (_, _) =>
            Task.FromResult<IReadOnlyList<BackgroundTaskSnapshot>>([
                BackgroundTask("task-7", "session-42"),
            ]);
        engine.ListRunningSubagentsHandler = (_, _) =>
            Task.FromResult<IReadOnlyList<SubagentSnapshot>>([
                RunningSubagent("subagent-7", "session-42"),
            ]);
        await using var controller = fixture.CreateController();

        var prompt = controller.HandleAsync(
            new PromptWebCommand("keep running", ExecutionProfile.NativeProtected));
        await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.HandleAsync(new RuntimeDashboardRefreshWebCommand("session-42"));

        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is RuntimeDashboardChangedWebEvent { SessionId: "session-42" });

        finishPrompt.TrySetResult(new PromptResult(EngineStopReason.EndTurn, "end_turn"));
        await prompt;
    }

    [Fact]
    public async Task RuntimeDashboard_CoalescesARefreshAlreadyRunningForTheSession()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var refreshStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ListBackgroundTasksHandler = async (_, cancellationToken) =>
        {
            refreshStarted.TrySetResult();
            await releaseRefresh.Task.WaitAsync(cancellationToken);
            return [];
        };
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));

        var firstRefresh = controller.HandleAsync(
            new RuntimeDashboardRefreshWebCommand("session-42"));
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicateRefresh = controller.HandleAsync(
            new RuntimeDashboardRefreshWebCommand("session-42"));

        try
        {
            var completed = await Task.WhenAny(
                duplicateRefresh,
                Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(duplicateRefresh, completed);
            Assert.Equal(
                1,
                engine.Calls.Count(call => call == "list-background-tasks:session-42"));
        }
        finally
        {
            releaseRefresh.TrySetResult();
            await Task.WhenAll(firstRefresh, duplicateRefresh);
        }
    }

    [Fact]
    public async Task RuntimeDashboard_TimesOutAHungRefreshWithoutBlockingTheController()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.ListBackgroundTasksHandler = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        };
        var options = Options with
        {
            RuntimeOperationTimeout = TimeSpan.FromMilliseconds(100),
        };
        await using var controller = fixture.CreateController(options);
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();
        using var cleanup = new CancellationTokenSource();

        var refresh = controller.HandleAsync(
            new RuntimeDashboardRefreshWebCommand("session-42"),
            cleanup.Token);
        try
        {
            await refresh.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains(
                fixture.Events.Snapshot(),
                item => item is RuntimeDashboardErrorWebEvent { SessionId: "session-42" });
        }
        finally
        {
            cleanup.Cancel();
            try
            {
                await refresh;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task RuntimeDashboard_WorkspaceChangeCancelsTheOldSessionRefresh()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var refreshStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ListBackgroundTasksHandler = async (_, cancellationToken) =>
        {
            refreshStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                refreshCancelled.TrySetResult();
                throw;
            }
            return [];
        };
        var options = Options with
        {
            RuntimeOperationTimeout = TimeSpan.FromSeconds(30),
        };
        await using var controller = fixture.CreateController(options);
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        var refresh = controller.HandleAsync(
            new RuntimeDashboardRefreshWebCommand("session-42"));
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(await controller.UpdateWorkspaceAsync("C:\\other"));
        await refreshCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => item is RuntimeDashboardChangedWebEvent or RuntimeDashboardErrorWebEvent);
    }

    [Fact]
    public async Task RuntimeDashboard_KillAndCancelRefreshTheAuthoritativeState()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.KillBackgroundTaskHandler = (_, _, _) =>
            Task.FromResult(BackgroundTaskKillOutcome.Killed);
        engine.CancelSubagentHandler = (_, _, _) =>
            Task.FromResult(new SubagentCancelResult(SubagentCancelOutcome.Cancelled));
        engine.ListBackgroundTasksHandler = (_, _) =>
            Task.FromResult<IReadOnlyList<BackgroundTaskSnapshot>>([]);
        engine.ListRunningSubagentsHandler = (_, _) =>
            Task.FromResult<IReadOnlyList<SubagentSnapshot>>([]);
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(
            new RuntimeTaskKillWebCommand("session-42", "task-7"));
        await controller.HandleAsync(
            new RuntimeSubagentCancelWebCommand("session-42", "subagent-7"));

        Assert.Contains("kill-background-task:session-42:task-7", engine.Calls);
        Assert.Contains("cancel-subagent:session-42:subagent-7", engine.Calls);
        Assert.Contains(
            new RuntimeTaskKilledWebEvent(
                "session-42",
                "task-7",
                BackgroundTaskKillOutcome.Killed),
            fixture.Events.Snapshot());
        Assert.Contains(
            new RuntimeSubagentCancelledWebEvent(
                "session-42",
                "subagent-7",
                new SubagentCancelResult(SubagentCancelOutcome.Cancelled)),
            fixture.Events.Snapshot());
        Assert.Equal(
            2,
            fixture.Events.Snapshot().OfType<RuntimeDashboardChangedWebEvent>().Count());
    }

    [Fact]
    public async Task RuntimeDashboard_TaskKillFailureIdentifiesThePendingAction()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.KillBackgroundTaskHandler = (_, _, _) =>
            throw new InvalidOperationException("kill failed");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(
            new RuntimeTaskKillWebCommand("session-42", "task-7"));

        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is RuntimeDashboardErrorWebEvent
            {
                SessionId: "session-42",
                Operation: RuntimeDashboardOperation.TaskKill,
                ItemId: "task-7",
            });
    }

    [Fact]
    public async Task RuntimeDashboard_SubagentCancelFailureIdentifiesThePendingAction()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.CancelSubagentHandler = (_, _, _) =>
            throw new InvalidOperationException("cancel failed");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(
            new RuntimeSubagentCancelWebCommand("session-42", "subagent-7"));

        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is RuntimeDashboardErrorWebEvent
            {
                SessionId: "session-42",
                Operation: RuntimeDashboardOperation.SubagentCancel,
                ItemId: "subagent-7",
            });
    }

    [Fact]
    public async Task RuntimeDashboard_TaskKillFollowUpRefreshFailureIsNotReportedAsAnActionFailure()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.KillBackgroundTaskHandler = (_, _, _) =>
            Task.FromResult(BackgroundTaskKillOutcome.Killed);
        engine.ListBackgroundTasksHandler = (_, _) =>
            throw new InvalidOperationException("refresh failed");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(
            new RuntimeTaskKillWebCommand("session-42", "task-7"));

        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is RuntimeTaskKilledWebEvent { TaskId: "task-7" });
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is RuntimeDashboardErrorWebEvent
            {
                SessionId: "session-42",
                Operation: RuntimeDashboardOperation.Refresh,
                ItemId: null,
            });
    }

    [Fact]
    public async Task RuntimeDashboard_SubagentCancelFollowUpRefreshFailureIsNotReportedAsAnActionFailure()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.CancelSubagentHandler = (_, _, _) =>
            Task.FromResult(new SubagentCancelResult(SubagentCancelOutcome.Cancelled));
        engine.ListBackgroundTasksHandler = (_, _) =>
            throw new InvalidOperationException("refresh failed");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(
            new RuntimeSubagentCancelWebCommand("session-42", "subagent-7"));

        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is RuntimeSubagentCancelledWebEvent { SubagentId: "subagent-7" });
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is RuntimeDashboardErrorWebEvent
            {
                SessionId: "session-42",
                Operation: RuntimeDashboardOperation.Refresh,
                ItemId: null,
            });
    }

    [Fact]
    public async Task RuntimeDashboard_GetProjectsTheRequestedSubagentDetail()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var detail = RunningSubagent("subagent-7", "session-42") with
        {
            Status = SubagentStatus.Completed,
            Output = "All tests passed",
        };
        engine.GetSubagentHandler = (_, _, _, _, _) =>
            Task.FromResult<SubagentSnapshot?>(detail);
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(
            new RuntimeSubagentGetWebCommand("session-42", "subagent-7"));

        Assert.Contains("get-subagent:session-42:subagent-7:False:", engine.Calls);
        Assert.Contains(
            new RuntimeSubagentDetailWebEvent("session-42", "subagent-7", detail),
            fixture.Events.Snapshot());
    }

    [Fact]
    public async Task RuntimeDashboard_RejectsANonActiveSessionWithoutPoisoningEngineStatus()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(new RuntimeDashboardRefreshWebCommand("other-session"));

        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("list-background-tasks:", StringComparison.Ordinal));
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is RuntimeDashboardErrorWebEvent { SessionId: "other-session" });
        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "error" });
    }

    [Fact]
    public async Task RuntimeDashboard_RejectsASubagentFromAnotherParentSession()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.ListRunningSubagentsHandler = (_, _) =>
            Task.FromResult<IReadOnlyList<SubagentSnapshot>>([
                RunningSubagent("subagent-7", "other-session"),
            ]);
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(new RuntimeDashboardRefreshWebCommand("session-42"));

        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => item is RuntimeDashboardChangedWebEvent);
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is RuntimeDashboardErrorWebEvent { SessionId: "session-42" });
    }

    [Fact]
    public async Task RewindForANonActiveSession_IsRejectedWithoutCallingTheEngine()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "active-session");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(
            new SessionRewindWebCommand(
                "other-session",
                1,
                SessionRewindMode.All,
                Force: false));

        Assert.DoesNotContain(engine.Calls, call => call.StartsWith("rewind-session:", StringComparison.Ordinal));
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "error" });
    }

    [Fact]
    public async Task RepeatedPlanMode_DoesNotResendTheConfirmation()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "plan-session");
        engine.InitializeCapabilities = Capabilities(
            sessionModes: [SessionMode.Default, SessionMode.Plan]);
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand(
                "first plan",
                ExecutionProfile.NativeProtected,
                SessionMode: SessionMode.Plan));
        await controller.HandleAsync(
            new PromptWebCommand(
                "second plan",
                ExecutionProfile.NativeProtected,
                SessionMode: SessionMode.Plan));

        Assert.Single(engine.Calls, call => call == "set-mode:plan");
        Assert.Equal(2, engine.Calls.Count(call => call == "prompt:plan-session"));
    }

    [Fact]
    public async Task PlanMode_CanReturnToDefaultBeforeTheNextPrompt()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "plan-session");
        engine.InitializeCapabilities = Capabilities(
            sessionModes: [SessionMode.Default, SessionMode.Plan]);
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand(
                "plan",
                ExecutionProfile.NativeProtected,
                SessionMode: SessionMode.Plan));
        await controller.HandleAsync(
            new PromptWebCommand(
                "execute",
                ExecutionProfile.NativeProtected,
                SessionMode: SessionMode.Default));

        Assert.Equal(
            ["set-mode:plan", "set-mode:default"],
            engine.Calls.Where(call => call.StartsWith("set-mode:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CurrentModeUpdate_IsAuthoritativeAndMalformedUpdatesAreIgnored()
    {
        var fixture = new ControllerFixture();
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "mode-session");
        engine.InitializeCapabilities = Capabilities(
            sessionModes: [SessionMode.Default, SessionMode.Plan]);
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        engine.EmitUpdate(
            new SessionId("mode-session"),
            "current_mode_update",
            """{ "currentModeId": "plan" }""");

        _ = await fixture.Events.WaitForAsync<SessionModeChangedWebEvent>(
            item => item.SessionId == "mode-session" && item.Mode == SessionMode.Plan,
            TimeSpan.FromSeconds(1));
        fixture.Events.Clear();
        await controller.HandleAsync(
            new PromptWebCommand(
                "continue planning",
                ExecutionProfile.NativeProtected,
                SessionMode: SessionMode.Plan));
        Assert.DoesNotContain("set-mode:plan", engine.Calls);

        fixture.Events.Clear();
        engine.EmitUpdate(
            new SessionId("mode-session"),
            "current_mode_update",
            """{ "currentModeId": 42 }""");
        Assert.Empty(fixture.Events.Snapshot());
    }

    [Fact]
    public async Task StaleCurrentModeUpdate_FromThePreviousEngineIsIgnored()
    {
        var fixture = new ControllerFixture();
        var oldEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "old-session");
        oldEngine.InitializeCapabilities = Capabilities(
            sessionModes: [SessionMode.Default, SessionMode.Plan]);
        var newEngine = fixture.Factory.EnqueueEngine("/mnt/c/workspace", "new-session");
        newEngine.InitializeCapabilities = Capabilities(
            strictSandboxActive: true,
            sessionModes: [SessionMode.Default, SessionMode.Plan]);
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("old", ExecutionProfile.NativeProtected));
        var staleEmitter = oldEngine.CaptureEventEmitter();
        await controller.HandleAsync(
            new PromptWebCommand("new", ExecutionProfile.WslStrict));
        fixture.Events.Clear();

        using var update = JsonDocument.Parse("""{ "currentModeId": "plan" }""");
        staleEmitter(
            new EngineEvent(
                new SessionId("old-session"),
                "current_mode_update",
                update.RootElement.Clone(),
                metadata: null));

        Assert.Empty(fixture.Events.Snapshot());
        await controller.HandleAsync(
            new PromptWebCommand(
                "new plan",
                ExecutionProfile.WslStrict,
                SessionMode: SessionMode.Plan));
        Assert.Contains("set-mode:plan", newEngine.Calls);
    }

    [Fact]
    public async Task CapturedEngineCallback_FromThePreviousSessionEpochIsIgnored()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "old-session");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("old", ExecutionProfile.NativeProtected));
        var staleEmitter = engine.CaptureEventEmitter();
        await controller.HandleAsync(
            new SessionOpenWebCommand(
                "new-session",
                "C:\\workspace",
                ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        using var update = JsonDocument.Parse(
            """{ "content": { "type": "text", "text": "stale chunk" } }""");
        staleEmitter(
            new EngineEvent(
                new SessionId("new-session"),
                "agent_message_chunk",
                update.RootElement.Clone(),
                metadata: null));

        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => item is SessionUpdateWebEvent);
    }

    [Fact]
    public async Task SidecarRestart_RequiresPlanModeConfirmationForTheRestoredSession()
    {
        var fixture = new ControllerFixture();
        var crashedEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "crashed-session");
        crashedEngine.InitializeCapabilities = Capabilities(
            sessionModes: [SessionMode.Default, SessionMode.Plan]);
        var recoveredEngine = fixture.Factory.EnqueueEngine(
            "C:\\workspace",
            "new-session-would-be");
        recoveredEngine.InitializeCapabilities = Capabilities(
            sessionModes: [SessionMode.Default, SessionMode.Plan]);
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand(
                "first plan",
                ExecutionProfile.NativeProtected,
                SessionMode: SessionMode.Plan));

        fixture.Factory.Hosts[0].RaiseExited(exitCode: 9, wasExpected: false);
        _ = await fixture.Events.WaitForAsync<EngineStatusWebEvent>(
            item => item.Status == "stopped",
            TimeSpan.FromSeconds(5));
        await controller.HandleAsync(
            new PromptWebCommand(
                "recovered plan",
                ExecutionProfile.NativeProtected,
                SessionMode: SessionMode.Plan));

        Assert.Single(recoveredEngine.Calls, call => call == "set-mode:plan");
        Assert.Contains(
            "load-session:crashed-session:C:\\workspace",
            recoveredEngine.Calls);
        Assert.Contains("prompt:crashed-session", recoveredEngine.Calls);
    }

    [Fact]
    public async Task ProcessRestart_LoadsThePersistedActiveSessionBeforeRunningTheNextPrompt()
    {
        var recoveryStore = new RecordingCrashRecoveryStore();
        var firstFixture = new ControllerFixture(recoveryStore);
        firstFixture.ProviderSettings.Profile = RecoveryProvider;
        var firstEngine = firstFixture.Factory.EnqueueEngine("C:\\workspace", "persisted-session");
        firstEngine.InitializeCapabilities = Capabilities(
            sessionModes: [SessionMode.Default, SessionMode.Plan]);
        var firstController = firstFixture.CreateController();

        await firstController.HandleAsync(new PromptWebCommand(
            "before process crash",
            ExecutionProfile.NativeProtected,
            SessionMode: SessionMode.Plan));

        var persisted = Assert.IsType<CrashRecoveryMarker>(recoveryStore.Marker);
        Assert.Equal("persisted-session", persisted.SessionId.Value);
        Assert.Equal("C:\\workspace", persisted.WorkspacePath);
        Assert.Equal(ExecutionProfile.NativeProtected, persisted.ExecutionProfile);
        Assert.Equal(SessionMode.Plan, persisted.SessionMode);

        var restartedFixture = new ControllerFixture(recoveryStore);
        restartedFixture.ProviderSettings.Profile = RecoveryProvider;
        var restartedEngine = restartedFixture.Factory.EnqueueEngine(
            "C:\\workspace",
            "new-session-must-not-be-used");
        restartedEngine.InitializeCapabilities = Capabilities(
            sessionModes: [SessionMode.Default, SessionMode.Plan]);
        await using var restartedController = restartedFixture.CreateController();

        await restartedController.HandleAsync(new UiReadyWebCommand());
        await restartedController.HandleAsync(new PromptWebCommand(
            "after process restart",
            ExecutionProfile.NativeProtected,
            SessionMode: SessionMode.Plan));

        Assert.Contains(
            "load-session:persisted-session:C:\\workspace",
            restartedEngine.Calls);
        Assert.DoesNotContain(
            restartedEngine.Calls,
            call => call.StartsWith("new-session:", StringComparison.Ordinal));
        Assert.Equal(["after process restart"], restartedEngine.PromptTexts);

        GC.KeepAlive(firstController);
    }

    [Fact]
    public async Task ProcessRestart_WithAChangedProviderStartsFreshWithoutLoadingOldContext()
    {
        var oldProvider = RecoveryProvider;
        var newProvider = new ProviderProfile(
            oldProvider.BaseUrl,
            "grok-4.6",
            oldProvider.Backend);
        var recoveryStore = new RecordingCrashRecoveryStore();
        recoveryStore.Seed(new CrashRecoveryMarker(
            new SessionId("old-provider-session"),
            "C:\\workspace",
            ExecutionProfile.NativeProtected,
            SessionMode.Default,
            DateTimeOffset.UtcNow,
            CrashRecoveryProviderIdentity.Create(oldProvider)));
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = newProvider;
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "fresh-session");
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(new UiReadyWebCommand());
        await controller.HandleAsync(
            new PromptWebCommand("fresh", ExecutionProfile.NativeProtected));

        Assert.Contains("new-session:C:\\workspace", engine.Calls);
        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("load-session:", StringComparison.Ordinal));
        Assert.Equal(["fresh"], engine.PromptTexts);
        Assert.Equal(
            CrashRecoveryProviderIdentity.Create(newProvider),
            Assert.IsType<CrashRecoveryMarker>(recoveryStore.Marker).ProviderIdentity);
    }

    [Fact]
    public async Task ProviderChange_DoesNotRebindTheActiveOldEngineMarkerBeforeRestart()
    {
        var oldProvider = RecoveryProvider;
        var newProvider = new ProviderProfile(
            oldProvider.BaseUrl,
            "grok-4.6",
            oldProvider.Backend);
        var recoveryStore = new RecordingCrashRecoveryStore();
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = oldProvider;
        fixture.Credentials.Save(oldProvider.CredentialName, "test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "old-session");
        var restartedEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "new-session");
        engine.InitializeCapabilities = Capabilities(
            sessionModes: [SessionMode.Default, SessionMode.Plan]);
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("old", ExecutionProfile.NativeProtected));
        var oldIdentity = CrashRecoveryProviderIdentity.Create(oldProvider);
        Assert.Equal(
            oldIdentity,
            Assert.IsType<CrashRecoveryMarker>(recoveryStore.Marker).ProviderIdentity);

        await controller.SaveProviderAsync(
            new SaveProviderWebCommand(
                newProvider,
                UseExistingCredential: true,
                ReplaceCredential: false),
            replacementCredential: null);
        engine.EmitUpdate(
            new SessionId("old-session"),
            "current_mode_update",
            """{ "currentModeId": "plan" }""");
        _ = await fixture.Events.WaitForAsync<SessionModeChangedWebEvent>(
            item => item.Mode is SessionMode.Plan,
            TimeSpan.FromSeconds(5));

        Assert.Equal(
            oldIdentity,
            Assert.IsType<CrashRecoveryMarker>(recoveryStore.Marker).ProviderIdentity);

        fixture.Factory.Hosts[0].RaiseExited(exitCode: 9, wasExpected: false);
        _ = await fixture.Events.WaitForAsync<EngineStatusWebEvent>(
            item => item.Status == "stopped",
            TimeSpan.FromSeconds(5));
        await controller.HandleAsync(
            new PromptWebCommand("new", ExecutionProfile.NativeProtected));

        Assert.Contains("new-session:C:\\workspace", restartedEngine.Calls);
        Assert.DoesNotContain(
            restartedEngine.Calls,
            call => call.StartsWith("load-session:", StringComparison.Ordinal));
        Assert.Equal(["new"], restartedEngine.PromptTexts);
    }

    [Fact]
    public async Task FailedAutomaticRecovery_IsDiscardedBeforeTheNextPromptStartsANewSession()
    {
        var recoveryStore = new RecordingCrashRecoveryStore();
        var providerIdentity = CrashRecoveryProviderIdentity.Create(RecoveryProvider);
        recoveryStore.Seed(new CrashRecoveryMarker(
            new SessionId("unloadable-session"),
            "C:\\workspace",
            ExecutionProfile.NativeProtected,
            SessionMode.Default,
            DateTimeOffset.UtcNow,
            providerIdentity));
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = RecoveryProvider;
        var failedRecovery = fixture.Factory.EnqueueEngine(
            "C:\\workspace",
            "unused-first-session");
        failedRecovery.LoadSessionHandler = (_, _, _) =>
            Task.FromException(new InvalidDataException("session cannot be loaded"));
        var freshEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "fresh-session");
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(new UiReadyWebCommand());
        await controller.HandleAsync(
            new PromptWebCommand("first", ExecutionProfile.NativeProtected));
        Assert.Null(recoveryStore.Marker);
        await controller.HandleAsync(
            new PromptWebCommand("second", ExecutionProfile.NativeProtected));

        Assert.Equal(
            "fresh-session",
            Assert.IsType<CrashRecoveryMarker>(recoveryStore.Marker).SessionId.Value);
        Assert.Equal(["second"], freshEngine.PromptTexts);
        Assert.Contains("new-session:C:\\workspace", freshEngine.Calls);
        Assert.DoesNotContain(
            freshEngine.Calls,
            call => call.StartsWith("load-session:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoveryUpdateFailure_RetainsTheLastMarkerForTheSameSession()
    {
        var recoveryStore = new RecordingCrashRecoveryStore();
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = RecoveryProvider;
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.InitializeCapabilities = Capabilities(
            sessionModes: [SessionMode.Default, SessionMode.Plan]);
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("default", ExecutionProfile.NativeProtected));
        var previous = Assert.IsType<CrashRecoveryMarker>(recoveryStore.Marker);
        recoveryStore.Operations.Clear();
        recoveryStore.SaveException = new IOException("update failed");

        await controller.HandleAsync(new PromptWebCommand(
            "plan",
            ExecutionProfile.NativeProtected,
            SessionMode: SessionMode.Plan));

        Assert.Equal(previous, recoveryStore.Marker);
        Assert.Equal(["save"], recoveryStore.Operations);
    }

    [Fact]
    public async Task RecoveryReplacement_InvalidatesTheOldMarkerBeforeSavingTheNewTarget()
    {
        var recoveryStore = new RecordingCrashRecoveryStore();
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = RecoveryProvider;
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "native-session");
        _ = fixture.Factory.EnqueueEngine("/mnt/c/workspace", "wsl-session");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("native", ExecutionProfile.NativeProtected));
        recoveryStore.Operations.Clear();
        recoveryStore.SaveException = new IOException("replacement failed");

        await controller.HandleAsync(
            new PromptWebCommand("wsl", ExecutionProfile.WslStrict));

        Assert.Null(recoveryStore.Marker);
        Assert.Equal(["clear", "save"], recoveryStore.Operations);
    }

    [Fact]
    public async Task SessionOpenFailure_RestoresThePreviousRecoveryMarker()
    {
        var recoveryStore = new RecordingCrashRecoveryStore();
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = RecoveryProvider;
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "active-session");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("active", ExecutionProfile.NativeProtected));
        var previous = Assert.IsType<CrashRecoveryMarker>(recoveryStore.Marker);
        recoveryStore.Operations.Clear();
        engine.LoadSessionHandler = (_, _, _) =>
            Task.FromException(new InvalidDataException("selected session is corrupt"));

        await controller.HandleAsync(
            new SessionOpenWebCommand(
                "selected-session",
                "C:\\workspace",
                ExecutionProfile.NativeProtected));

        var restored = Assert.IsType<CrashRecoveryMarker>(recoveryStore.Marker);
        Assert.Equal(previous.SessionId, restored.SessionId);
        Assert.Equal(previous.WorkspacePath, restored.WorkspacePath);
        Assert.Equal(previous.ExecutionProfile, restored.ExecutionProfile);
        Assert.Equal(previous.SessionMode, restored.SessionMode);
        Assert.Equal(previous.ProviderIdentity, restored.ProviderIdentity);
        Assert.Equal(["clear", "save"], recoveryStore.Operations);
        await controller.HandleAsync(
            new PromptWebCommand("continue active", ExecutionProfile.NativeProtected));
        Assert.Contains("prompt:active-session", engine.Calls);
    }

    [Fact]
    public async Task SessionOpenRecoveryRestoreFailure_LeavesTheMarkerEmpty()
    {
        var recoveryStore = new RecordingCrashRecoveryStore();
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = RecoveryProvider;
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "active-session");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("active", ExecutionProfile.NativeProtected));
        recoveryStore.Operations.Clear();
        recoveryStore.SaveException = new IOException("recovery restore failed");
        engine.LoadSessionHandler = (_, _, _) =>
            Task.FromException(new InvalidDataException("selected session is corrupt"));

        await controller.HandleAsync(
            new SessionOpenWebCommand(
                "selected-session",
                "C:\\workspace",
                ExecutionProfile.NativeProtected));

        Assert.Null(recoveryStore.Marker);
        Assert.Equal(["clear", "save"], recoveryStore.Operations);
        recoveryStore.SaveException = null;
    }

    [Fact]
    public async Task SessionOpenClearFailure_PreservesTheActiveSessionAndMarker()
    {
        var recoveryStore = new RecordingCrashRecoveryStore();
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = RecoveryProvider;
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "active-session");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("active", ExecutionProfile.NativeProtected));
        var previous = Assert.IsType<CrashRecoveryMarker>(recoveryStore.Marker);
        recoveryStore.Operations.Clear();
        recoveryStore.ClearException = new IOException("marker is locked");

        await controller.HandleAsync(
            new SessionOpenWebCommand(
                "selected-session",
                "C:\\workspace",
                ExecutionProfile.NativeProtected));
        recoveryStore.ClearException = null;

        Assert.Equal(previous, recoveryStore.Marker);
        Assert.Equal(["clear"], recoveryStore.Operations);
        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("load-session:selected-session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MaintenanceImportFailure_RestoresThePreviousRecoveryMarker()
    {
        var recoveryStore = new RecordingCrashRecoveryStore();
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = RecoveryProvider;
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "active-session");
        engine.ImportSessionHandler = (_, _, _) =>
            Task.FromException<SessionId>(new InvalidDataException("import is corrupt"));
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("active", ExecutionProfile.NativeProtected));
        var previous = Assert.IsType<CrashRecoveryMarker>(recoveryStore.Marker);
        recoveryStore.Operations.Clear();
        var document = EngineSessionDocument.FromJson(
            "{\"schemaVersion\":1,\"sessionId\":\"imported-session\"}");

        await using (var lease = await controller.BeginMaintenanceAsync())
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => lease.ImportSessionAsync(document));
        }

        var restored = Assert.IsType<CrashRecoveryMarker>(recoveryStore.Marker);
        Assert.Equal(previous.SessionId, restored.SessionId);
        Assert.Equal(previous.WorkspacePath, restored.WorkspacePath);
        Assert.Equal(previous.ExecutionProfile, restored.ExecutionProfile);
        Assert.Equal(previous.SessionMode, restored.SessionMode);
        Assert.Equal(previous.ProviderIdentity, restored.ProviderIdentity);
        Assert.Equal(["clear", "save"], recoveryStore.Operations);
    }

    [Fact]
    public async Task MaintenanceImportClearFailure_PreservesTheActiveSessionAndMarker()
    {
        var recoveryStore = new RecordingCrashRecoveryStore();
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = RecoveryProvider;
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "active-session");
        engine.ImportSessionHandler = (_, _, _) =>
            Task.FromResult(new SessionId("imported-session"));
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("active", ExecutionProfile.NativeProtected));
        var previous = Assert.IsType<CrashRecoveryMarker>(recoveryStore.Marker);
        recoveryStore.Operations.Clear();
        recoveryStore.ClearException = new IOException("marker is locked");
        var document = EngineSessionDocument.FromJson(
            "{\"schemaVersion\":1,\"sessionId\":\"imported-session\"}");

        Exception? importError;
        await using (var lease = await controller.BeginMaintenanceAsync())
        {
            importError = await Record.ExceptionAsync(() => lease.ImportSessionAsync(document));
        }
        recoveryStore.ClearException = null;

        Assert.IsType<IOException>(importError);
        Assert.Equal(previous, recoveryStore.Marker);
        Assert.Equal(["clear"], recoveryStore.Operations);
        Assert.DoesNotContain("import-session:C:\\workspace", engine.Calls);
    }

    [Fact]
    public async Task CloudActivationClearFailure_PreservesTheActiveSessionAndMarker()
    {
        var recoveryStore = new RecordingCrashRecoveryStore();
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = RecoveryProvider;
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "active-session");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("active", ExecutionProfile.NativeProtected));
        var previous = Assert.IsType<CrashRecoveryMarker>(recoveryStore.Marker);
        recoveryStore.Operations.Clear();
        recoveryStore.ClearException = new IOException("marker is locked");

        Exception? activationError;
        await using (var lease = await controller.BeginCloudEngineOperationAsync())
        {
            activationError = await Record.ExceptionAsync(
                () => lease.ActivateSessionAsync(new SessionId("cloud-session")));
        }
        recoveryStore.ClearException = null;

        Assert.IsType<IOException>(activationError);
        Assert.Equal(previous, recoveryStore.Marker);
        Assert.Equal(["clear"], recoveryStore.Operations);
        await controller.HandleAsync(
            new PromptWebCommand("continue active", ExecutionProfile.NativeProtected));
        Assert.Contains("prompt:active-session", engine.Calls);
    }

    [Fact]
    public async Task EngineRestartClearFailure_DoesNotStopOrReplaceTheActiveSession()
    {
        var recoveryStore = new RecordingCrashRecoveryStore();
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = RecoveryProvider;
        var nativeEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "native-session");
        var wslEngine = fixture.Factory.EnqueueEngine("/mnt/c/workspace", "wsl-session");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("native", ExecutionProfile.NativeProtected));
        var previous = Assert.IsType<CrashRecoveryMarker>(recoveryStore.Marker);
        var nativeHost = Assert.Single(fixture.Factory.Hosts);
        recoveryStore.Operations.Clear();
        recoveryStore.ClearException = new IOException("marker is locked");

        await controller.HandleAsync(
            new PromptWebCommand("wsl", ExecutionProfile.WslStrict));
        recoveryStore.ClearException = null;

        Assert.Equal(previous, recoveryStore.Marker);
        Assert.Equal(["clear"], recoveryStore.Operations);
        Assert.Equal(0, nativeHost.StopCount);
        Assert.Single(fixture.Factory.Hosts);
        Assert.Empty(wslEngine.Calls);
        Assert.Equal(["native"], nativeEngine.PromptTexts);
    }

    [Fact]
    public async Task DisposeAsync_ClearsTheRecoveryMarkerAfterAHostStopsCleanly()
    {
        var recoveryStore = new RecordingCrashRecoveryStore();
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = RecoveryProvider;
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("done", ExecutionProfile.NativeProtected));
        Assert.NotNull(recoveryStore.Marker);
        var clearsBeforeDispose = recoveryStore.ClearCount;

        await controller.DisposeAsync();

        Assert.Null(recoveryStore.Marker);
        Assert.Equal(clearsBeforeDispose + 1, recoveryStore.ClearCount);
    }

    [Fact]
    public async Task ActivatedSession_IsPersistedEvenWhenThePromptRequestIsThenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var recoveryStore = new RecordingCrashRecoveryStore();
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = RecoveryProvider;
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.NewSessionHandler = (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new SessionId("session-42"));
        };
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand("cancelled", ExecutionProfile.NativeProtected),
            cancellation.Token);

        var persisted = Assert.IsType<CrashRecoveryMarker>(recoveryStore.Marker);
        Assert.Equal("session-42", persisted.SessionId.Value);
    }

    [Fact]
    public async Task StartupRecoveryReadFailure_DoesNotBlockStartingANewSession()
    {
        var recoveryStore = new RecordingCrashRecoveryStore
        {
            LoadException = new IOException("recovery file unavailable"),
        };
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = RecoveryProvider;
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "new-session");
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(new UiReadyWebCommand());
        await controller.HandleAsync(
            new PromptWebCommand("continue", ExecutionProfile.NativeProtected));

        Assert.Contains("new-session:C:\\workspace", engine.Calls);
        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("load-session:", StringComparison.Ordinal));
        Assert.Equal(["continue"], engine.PromptTexts);
    }

    [Fact]
    public async Task RecoveryWriteFailure_ClearsAnOlderMarkerWithoutBlockingTheNewSession()
    {
        var recoveryStore = new RecordingCrashRecoveryStore
        {
            LoadException = new IOException("old marker cannot be trusted"),
            SaveException = new IOException("new marker cannot be written"),
        };
        recoveryStore.Seed(new CrashRecoveryMarker(
            new SessionId("stale-session"),
            "C:\\workspace",
            ExecutionProfile.NativeProtected,
            SessionMode.Default,
            DateTimeOffset.UtcNow,
            CrashRecoveryProviderIdentity.Create(RecoveryProvider)));
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = RecoveryProvider;
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "new-session");
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(new UiReadyWebCommand());
        await controller.HandleAsync(
            new PromptWebCommand("continue", ExecutionProfile.NativeProtected));

        Assert.Equal(["continue"], engine.PromptTexts);
        Assert.Null(recoveryStore.Marker);
        Assert.True(recoveryStore.ClearCount >= 1);
    }

    [Fact]
    public async Task ChangingWorkspace_ClearsTheExplicitlyClosedSessionMarker()
    {
        var recoveryStore = new RecordingCrashRecoveryStore();
        var fixture = new ControllerFixture(recoveryStore);
        fixture.ProviderSettings.Profile = RecoveryProvider;
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("done", ExecutionProfile.NativeProtected));
        Assert.NotNull(recoveryStore.Marker);
        var clearsBeforeWorkspaceChange = recoveryStore.ClearCount;

        Assert.True(await controller.UpdateWorkspaceAsync("C:\\other"));

        Assert.Null(recoveryStore.Marker);
        Assert.Equal(clearsBeforeWorkspaceChange + 1, recoveryStore.ClearCount);
    }

    [Fact]
    public async Task ConcurrentPrompt_IsRejectedWhileTheActivePromptKeepsRunning()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        var promptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptCompletion = new TaskCompletionSource<PromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PromptHandler = (_, _, _) =>
        {
            promptStarted.TrySetResult();
            return promptCompletion.Task;
        };
        await using var controller = fixture.CreateController();

        var firstPrompt = controller.HandleAsync(
            new PromptWebCommand("first", ExecutionProfile.NativeProtected));
        await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await controller.HandleAsync(
            new PromptWebCommand("second", ExecutionProfile.NativeProtected));

        Assert.Single(engine.PromptTexts);
        Assert.Equal("first", engine.PromptTexts[0]);
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "running", SessionId: "session-1" });

        promptCompletion.SetResult(new PromptResult(EngineStopReason.EndTurn, "end_turn"));
        await firstPrompt;
    }

    [Fact]
    public async Task Cancel_CancelsOnlyTheActiveSessionAndCompletesThePromptAsCancelled()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        var promptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PromptHandler = async (_, _, cancellationToken) =>
        {
            promptStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new PromptResult(EngineStopReason.EndTurn, "end_turn");
        };
        await using var controller = fixture.CreateController();

        var prompt = controller.HandleAsync(
            new PromptWebCommand("long task", ExecutionProfile.NativeProtected));
        await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await controller.HandleAsync(new CancelWebCommand("different-session"));
        Assert.Empty(engine.CancelledSessions);

        await controller.HandleAsync(new CancelWebCommand("session-1"));
        await prompt;

        Assert.Equal(["session-1"], engine.CancelledSessions);
        Assert.Contains(
            new PromptCompletedWebEvent("session-1", "cancelled"),
            fixture.Events.Snapshot());
    }

    [Fact]
    public async Task Cancel_CancelsTheLocalPromptBeforeRemoteAcknowledgement()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        var promptStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemoteCancel = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PromptHandler = async (_, _, cancellationToken) =>
        {
            promptStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new PromptResult(EngineStopReason.EndTurn, "end_turn");
        };
        engine.CancelHandler = (_, _) => releaseRemoteCancel.Task;
        await using var controller = fixture.CreateController();
        var prompt = controller.HandleAsync(
            new PromptWebCommand("long task", ExecutionProfile.NativeProtected));
        await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancel = controller.HandleAsync(new CancelWebCommand("session-1"));
        try
        {
            var completed = await Task.WhenAny(prompt, Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.Same(prompt, completed);
        }
        finally
        {
            releaseRemoteCancel.TrySetResult();
            await cancel;
            await prompt;
        }
    }

    [Fact]
    public async Task ProfileChange_RestartsTheSidecarAndCreatesANewSession()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var native = fixture.Factory.EnqueueEngine("C:\\workspace", "native-session");
        var wsl = fixture.Factory.EnqueueEngine("/mnt/c/workspace", "wsl-session");
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(new PromptWebCommand("native", ExecutionProfile.NativeProtected));
        await controller.HandleAsync(new PromptWebCommand("wsl", ExecutionProfile.WslStrict));

        Assert.Equal(2, fixture.Factory.Launches.Count);
        Assert.Equal(Options.NativeEnginePath, fixture.Factory.Launches[0].EnginePath);
        Assert.Equal(Options.WslEnginePath, fixture.Factory.Launches[1].EnginePath);
        Assert.Equal(1, fixture.Factory.Hosts[0].StopCount);
        Assert.Equal(1, fixture.Factory.Hosts[0].DisposeCount);
        Assert.Contains("new-session:C:\\workspace", native.Calls);
        Assert.Contains("new-session:/mnt/c/workspace", wsl.Calls);
        Assert.Contains("prompt:wsl-session", wsl.Calls);
    }

    [Fact]
    public async Task ProfileChange_WhenOldHostCleanupFails_RetriesItBeforeStartingReplacement()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "native-session");
        var wsl = fixture.Factory.EnqueueEngine("/mnt/c/workspace", "wsl-session");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("native", ExecutionProfile.NativeProtected));
        var oldHost = fixture.Factory.Hosts[0];
        oldHost.StopFailuresRemaining = 1;
        oldHost.DisposeFailuresRemaining = 1;

        await controller.HandleAsync(
            new PromptWebCommand("first wsl attempt", ExecutionProfile.WslStrict));

        Assert.Single(fixture.Factory.Hosts);
        Assert.Empty(wsl.PromptTexts);

        await controller.HandleAsync(
            new PromptWebCommand("second wsl attempt", ExecutionProfile.WslStrict));

        Assert.Equal(2, oldHost.StopCount);
        Assert.Equal(2, oldHost.DisposeCount);
        Assert.Equal(["second wsl attempt"], wsl.PromptTexts);
    }

    [Fact]
    public async Task UpdateWorkspace_StopsTheCurrentSidecarAndRequiresNewTrustDecision()
    {
        const string newWorkspace = "D:\\next-workspace";
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "first-session");
        var nextEngine = fixture.Factory.EnqueueEngine(newWorkspace, "next-session");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("first", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        var updated = await controller.UpdateWorkspaceAsync(newWorkspace);
        await controller.HandleAsync(
            new PromptWebCommand(
                "must be confirmed",
                ExecutionProfile.NativeProtected,
                WorkspaceGeneration: 2));
        await controller.HandleAsync(
            new PromptWebCommand(
                "stale acknowledgement",
                ExecutionProfile.NativeProtected,
                NativeRiskAcknowledged: true,
                WorkspaceGeneration: 1));

        Assert.True(updated);
        Assert.Equal(1, fixture.Factory.Hosts[0].StopCount);
        Assert.Equal(1, fixture.Factory.Hosts[0].DisposeCount);
        Assert.Single(fixture.Factory.Launches);
        Assert.Empty(nextEngine.Calls);
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "error", Message: not null } status &&
                status.Message.Contains("确认", StringComparison.Ordinal));

        await controller.HandleAsync(
            new PromptWebCommand(
                "next",
                ExecutionProfile.NativeProtected,
                NativeRiskAcknowledged: true,
                WorkspaceGeneration: 2));

        Assert.Equal(newWorkspace, fixture.Factory.Launches[1].WorkspacePath);
        Assert.Contains($"new-session:{newWorkspace}", nextEngine.Calls);
        Assert.Contains(new WorkspaceSelectedWebEvent(newWorkspace, 2), fixture.Events.Snapshot());
    }

    [Fact]
    public async Task SelectingTheFirstWorkspace_DoesNotApplyTrustWithoutAnInitialPath()
    {
        const string selectedWorkspace = "D:\\selected-workspace";
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine(selectedWorkspace, "session-1");
        await using var controller = fixture.CreateController(
            new AgentDeskHostOptions { IsTrustedWorkspace = true });

        Assert.True(await controller.UpdateWorkspaceAsync(selectedWorkspace));
        await controller.HandleAsync(
            new PromptWebCommand(
                "must be confirmed",
                ExecutionProfile.NativeProtected,
                WorkspaceGeneration: 1));
        await controller.HandleAsync(
            new PromptWebCommand(
                "stale acknowledgement",
                ExecutionProfile.NativeProtected,
                NativeRiskAcknowledged: true,
                WorkspaceGeneration: 0));

        Assert.Empty(fixture.Factory.Hosts);
        Assert.Empty(engine.Calls);
    }

    [Fact]
    public async Task ReselectingTheSameWorkspace_RequiresANewTrustDecision()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        await using var controller = fixture.CreateController();

        Assert.True(await controller.UpdateWorkspaceAsync("c:\\WORKSPACE"));
        await controller.HandleAsync(
            new PromptWebCommand(
                "stale acknowledgement",
                ExecutionProfile.NativeProtected,
                NativeRiskAcknowledged: true,
                WorkspaceGeneration: 1));

        Assert.Empty(fixture.Factory.Hosts);
        Assert.Empty(engine.Calls);

        await controller.HandleAsync(
            new PromptWebCommand(
                "confirmed current generation",
                ExecutionProfile.NativeProtected,
                NativeRiskAcknowledged: true,
                WorkspaceGeneration: 2));

        Assert.Single(fixture.Factory.Hosts);
        Assert.Equal(["confirmed current generation"], engine.PromptTexts);
    }

    [Fact]
    public async Task UpdateWorkspace_IsRejectedWhileAPromptIsRunning()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        var promptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptCompletion = new TaskCompletionSource<PromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PromptHandler = (_, _, _) =>
        {
            promptStarted.TrySetResult();
            return promptCompletion.Task;
        };
        await using var controller = fixture.CreateController();
        var prompt = controller.HandleAsync(
            new PromptWebCommand("running", ExecutionProfile.NativeProtected));
        await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var updated = await controller.UpdateWorkspaceAsync("D:\\not-applied");

        Assert.False(updated);
        await controller.HandleAsync(new UiReadyWebCommand());
        Assert.Contains(
            new WorkspaceSelectedWebEvent(Options.WorkspacePath!, 1),
            fixture.Events.Snapshot());

        promptCompletion.SetResult(new PromptResult(EngineStopReason.EndTurn, "end_turn"));
        await prompt;
    }

    [Fact]
    public async Task UnexpectedSidecarExit_ProjectsStoppedState()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        fixture.UiPreferences.Preferences = UiPreferences.Default with { Language = "en-US" };
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("done", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        fixture.Factory.Hosts[0].RaiseExited(exitCode: 17, wasExpected: false);

        var stopped = await fixture.Events.WaitForAsync<EngineStatusWebEvent>(
            item => item.Status == "stopped",
            TimeSpan.FromSeconds(5));
        Assert.Equal("session-1", stopped.SessionId);
        Assert.Equal("The engine process exited unexpectedly (code 17).", stopped.Message);
    }

    [Fact]
    public async Task UnexpectedSidecarExit_PublishesGenericFailureNotificationWhenEnabled()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        fixture.UiPreferences.Preferences = UiPreferences.Default with
        {
            NotificationsEnabled = true,
        };
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("done", ExecutionProfile.NativeProtected));
        fixture.Notifications.Clear();

        fixture.Factory.Hosts[0].RaiseExited(exitCode: 17, wasExpected: false);

        var failed = await fixture.Notifications.WaitForAsync(
            AgentDeskNotificationKind.TaskFailed,
            TimeSpan.FromSeconds(5));
        Assert.Equal("session-1", failed.Notification.SessionId);
    }

    [Fact]
    public async Task SidecarExit_DoesNotDependOnTheForwardedEventSender()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("done", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        fixture.Factory.Hosts[0].RaiseExited(
            exitCode: 23,
            wasExpected: false,
            sender: new object());

        var stopped = await fixture.Events.WaitForAsync<EngineStatusWebEvent>(
            item => item.Status == "stopped",
            TimeSpan.FromSeconds(1));
        Assert.Contains("23", stopped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SidecarCrashDuringPrompt_RestoresActiveSessionAndDropsTheOldLateResult()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var crashedEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "crashed-session");
        var promptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateResult = new TaskCompletionSource<PromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        crashedEngine.PromptHandler = (_, _, _) =>
        {
            promptStarted.TrySetResult();
            return lateResult.Task;
        };
        var recoveredEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "new-session-would-be");
        await using var controller = fixture.CreateController();
        var crashedPrompt = controller.HandleAsync(
            new PromptWebCommand("will crash", ExecutionProfile.NativeProtected));
        await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Factory.Hosts[0].RaiseExited(exitCode: 9, wasExpected: false);
        _ = await fixture.Events.WaitForAsync<EngineStatusWebEvent>(
            item => item.Status == "stopped",
            TimeSpan.FromSeconds(5));
        fixture.Events.Clear();

        await controller.HandleAsync(
            new PromptWebCommand("recover", ExecutionProfile.NativeProtected));
        var completionsBeforeLateResult = fixture.Events.Snapshot()
            .OfType<PromptCompletedWebEvent>()
            .Count();
        lateResult.SetResult(new PromptResult(EngineStopReason.EndTurn, "late_end_turn"));
        await crashedPrompt;

        Assert.Equal(["recover"], recoveredEngine.PromptTexts);
        Assert.Contains(
            "load-session:crashed-session:C:\\workspace",
            recoveredEngine.Calls);
        Assert.DoesNotContain(
            recoveredEngine.Calls,
            call => call.StartsWith("new-session:", StringComparison.Ordinal));
        Assert.Equal(
            completionsBeforeLateResult,
            fixture.Events.Snapshot().OfType<PromptCompletedWebEvent>().Count());
        var ready = Assert.IsType<EngineStatusWebEvent>(fixture.Events.Snapshot().Last());
        Assert.Equal("ready", ready.Status);
        Assert.Equal("crashed-session", ready.SessionId);
        Assert.True(ready.EngineEpoch > 0);
    }

    [Fact]
    public async Task EngineFaultDuringPrompt_StopsTheHostAndRebuildsForTheNextPrompt()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var faultedEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "faulted-session");
        var promptStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        faultedEngine.PromptHandler = async (_, _, cancellationToken) =>
        {
            promptStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new PromptResult(EngineStopReason.EndTurn, "end_turn");
        };
        var recoveredEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "recovered-session");
        await using var controller = fixture.CreateController();
        var faultedPrompt = controller.HandleAsync(
            new PromptWebCommand("will fault", ExecutionProfile.NativeProtected));
        await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        faultedEngine.RaiseFault(new InvalidDataException("malformed engine frame"));

        var stopped = await fixture.Events.WaitForAsync<EngineStatusWebEvent>(
            item => item.Status == "stopped",
            TimeSpan.FromSeconds(5));
        Assert.Equal("faulted-session", stopped.SessionId);
        Assert.Equal(1, fixture.Factory.Hosts[0].StopCount);
        Assert.Equal(1, fixture.Factory.Hosts[0].DisposeCount);
        await faultedPrompt;

        await controller.HandleAsync(
            new PromptWebCommand("recover", ExecutionProfile.NativeProtected));

        Assert.Equal(["recover"], recoveredEngine.PromptTexts);
        Assert.Contains(
            "load-session:faulted-session:C:\\workspace",
            recoveredEngine.Calls);
        Assert.Single(
            fixture.Events.Snapshot().OfType<PromptCompletedWebEvent>(),
            item => item.SessionId == "faulted-session");
    }

    [Fact]
    public async Task SidecarCrashThenExplicitSessionOpen_LoadsOnlyTheRequestedSession()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "crashed-session");
        var reopenedEngine = fixture.Factory.EnqueueEngine(
            "C:\\workspace",
            "new-session-would-be");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("before crash", ExecutionProfile.NativeProtected));

        fixture.Factory.Hosts[0].RaiseExited(exitCode: 9, wasExpected: false);
        _ = await fixture.Events.WaitForAsync<EngineStatusWebEvent>(
            item => item.Status == "stopped",
            TimeSpan.FromSeconds(5));

        await controller.HandleAsync(
            new SessionOpenWebCommand(
                "selected-session",
                "C:\\workspace",
                ExecutionProfile.NativeProtected));

        Assert.Contains(
            "load-session:selected-session:C:\\workspace",
            reopenedEngine.Calls);
        Assert.DoesNotContain(
            "load-session:crashed-session:C:\\workspace",
            reopenedEngine.Calls);
        Assert.DoesNotContain(
            reopenedEngine.Calls,
            call => call.StartsWith("new-session:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MalformedAcpFrame_StopsTheRealClientAndRebuildsForTheNextPrompt()
    {
        var faultingHost = new FaultingAcpSidecarHost();
        var recoveredEngine = new RecordingEngineClient("recovered-session");
        var recoveredHost = new RecordingSidecarHost(
            recoveredEngine,
            "C:\\workspace",
            []);
        var factory = new SequenceSidecarHostFactory(faultingHost, recoveredHost);
        var events = new EventCollector();
        await using var controller = new AgentDeskHostController(
            Options,
            new RecordingCredentialStore(),
            factory);
        controller.EventProduced += (_, webEvent) => events.Add(webEvent);

        var faultedPrompt = controller.HandleAsync(
            new PromptWebCommand("will fault", ExecutionProfile.NativeProtected));
        using var initialize = await faultingHost.ReadRequestAsync();
        Assert.Equal("initialize", initialize.RootElement.GetProperty("method").GetString());
        await faultingHost.RespondAsync(
            initialize,
            """
            {
              "protocolVersion": 1,
              "agentCapabilities": {},
              "authMethods": [
                { "type": "agent", "id": "xai.api_key", "name": "xai.api_key" }
              ]
            }
            """);
        using var extensionInitialize = await faultingHost.ReadRequestAsync();
        Assert.Equal(
            "_agentdesk/v1/initialize",
            extensionInitialize.RootElement.GetProperty("method").GetString());
        await faultingHost.RespondWithErrorAsync(extensionInitialize, -32601, "Method not found");
        using var authenticate = await faultingHost.ReadRequestAsync();
        Assert.Equal("authenticate", authenticate.RootElement.GetProperty("method").GetString());
        await faultingHost.RespondAsync(authenticate, "{}");
        using var newSession = await faultingHost.ReadRequestAsync();
        Assert.Equal("session/new", newSession.RootElement.GetProperty("method").GetString());
        await faultingHost.RespondAsync(newSession, "{ \"sessionId\": \"faulted-session\" }");
        using var prompt = await faultingHost.ReadRequestAsync();
        Assert.Equal("session/prompt", prompt.RootElement.GetProperty("method").GetString());

        await faultingHost.WriteMalformedFrameAsync();

        var stopped = await events.WaitForAsync<EngineStatusWebEvent>(
            item => item.Status == "stopped",
            TimeSpan.FromSeconds(5));
        Assert.Equal("faulted-session", stopped.SessionId);
        Assert.Equal(1, faultingHost.StopCount);
        Assert.Equal(1, faultingHost.DisposeCount);
        await faultedPrompt.WaitAsync(TimeSpan.FromSeconds(5));

        await controller.HandleAsync(
            new PromptWebCommand("recover", ExecutionProfile.NativeProtected));

        Assert.Equal(["recover"], recoveredEngine.PromptTexts);
    }

    [Fact]
    public async Task StartFailure_ProjectsASanitizedError()
    {
        const string secret = "xai-secret-from-sidecar-error";
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", secret);
        fixture.Factory.EnqueueFailure(new InvalidOperationException(secret));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(new PromptWebCommand("run", ExecutionProfile.WslStrict));

        var status = Assert.Single(
            fixture.Events.Snapshot().OfType<EngineStatusWebEvent>(),
            item => item.Status == "error");
        Assert.DoesNotContain(secret, status.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupCancellation_ReleasesThePromptStateForTheNextRequest()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var cancelledEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "cancelled-session");
        var initializeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        cancelledEngine.InitializeHandler = async cancellationToken =>
        {
            initializeStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return EngineCapabilities.Uninitialized;
        };
        var nextEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "next-session");
        await using var controller = fixture.CreateController();
        using var cancellation = new CancellationTokenSource();

        var cancelledPrompt = controller.HandleAsync(
            new PromptWebCommand("cancel during startup", ExecutionProfile.NativeProtected),
            cancellation.Token);
        await initializeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledPrompt);

        await controller.HandleAsync(
            new PromptWebCommand("next request", ExecutionProfile.NativeProtected));

        Assert.Equal(["next request"], nextEngine.PromptTexts);
    }

    [Fact]
    public async Task StartupHandshakeTimeout_ReleasesTheHostForTheNextRequest()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var timedOutEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "timed-out-session");
        var releaseInitialize = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        timedOutEngine.InitializeHandler = async cancellationToken =>
        {
            await releaseInitialize.Task.WaitAsync(cancellationToken);
            return EngineCapabilities.Uninitialized;
        };
        var nextEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "next-session");
        await using var controller = fixture.CreateController(
            Options with { AcpHandshakeTimeout = TimeSpan.FromMilliseconds(50) });

        var timedOutPrompt = controller.HandleAsync(
            new PromptWebCommand("timeout during startup", ExecutionProfile.NativeProtected));
        try
        {
            var completed = await Task.WhenAny(
                timedOutPrompt,
                Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.Same(timedOutPrompt, completed);
            await timedOutPrompt;
        }
        finally
        {
            releaseInitialize.TrySetResult();
            await timedOutPrompt;
        }

        await controller.HandleAsync(
            new PromptWebCommand("next request", ExecutionProfile.NativeProtected));

        Assert.Empty(timedOutEngine.PromptTexts);
        Assert.Equal(["next request"], nextEngine.PromptTexts);
        Assert.Equal(1, fixture.Factory.Hosts[0].DisposeCount);
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "error" });
    }

    [Theory]
    [InlineData("authenticate")]
    [InlineData("new-session")]
    public async Task StartupHandshakeTimeout_CoversEveryPostInitializeStage(string stage)
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var timedOutEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "timed-out-session");
        if (stage == "authenticate")
        {
            timedOutEngine.AuthenticateHandler = cancellationToken =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        else
        {
            timedOutEngine.NewSessionHandler = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new SessionId("unreachable");
            };
        }
        await using var controller = fixture.CreateController(
            Options with { AcpHandshakeTimeout = TimeSpan.FromMilliseconds(50) });

        await controller.HandleAsync(
            new PromptWebCommand("timeout during startup", ExecutionProfile.NativeProtected));

        Assert.Empty(timedOutEngine.PromptTexts);
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "error" });
    }

    [Fact]
    public async Task StartupHandshake_UsesOneCancellationBudgetAcrossStages()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "timed-out-session");
        var authenticateSawCancelledToken = false;
        engine.InitializeHandler = async cancellationToken =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Yield();
            }

            return EngineCapabilities.Uninitialized;
        };
        engine.AuthenticateHandler = cancellationToken =>
        {
            authenticateSawCancelledToken = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        await using var controller = fixture.CreateController(
            Options with { AcpHandshakeTimeout = TimeSpan.FromMilliseconds(50) });

        await controller.HandleAsync(
            new PromptWebCommand("timeout across stages", ExecutionProfile.NativeProtected));

        Assert.True(authenticateSawCancelledToken);
        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("new-session:", StringComparison.Ordinal));
        Assert.Empty(engine.PromptTexts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task WslStrict_RejectsMissingOrInactiveSandboxAttestation(
        bool agentDeskHealth,
        bool strictSandboxActive)
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("/mnt/c/workspace", "wsl-session");
        engine.InitializeHandler = _ => Task.FromResult(
            Capabilities(
                agentDeskHealth: agentDeskHealth,
                strictSandboxActive: strictSandboxActive));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand("must stay sandboxed", ExecutionProfile.WslStrict));

        Assert.DoesNotContain("authenticate", engine.Calls);
        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("new-session:", StringComparison.Ordinal));
        Assert.Empty(engine.PromptTexts);
        Assert.Equal(1, fixture.Factory.Hosts[0].DisposeCount);
        Assert.Contains(
            fixture.Events.Snapshot(),
            item => item is EngineStatusWebEvent { Status: "error" });
    }

    [Fact]
    public async Task NativeProtected_AllowsAnEngineWithoutSandboxAttestation()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "native-session");
        engine.InitializeHandler = _ => Task.FromResult(
            Capabilities(agentDeskHealth: false, strictSandboxActive: false));
        await using var controller = fixture.CreateController();

        await controller.HandleAsync(
            new PromptWebCommand("native request", ExecutionProfile.NativeProtected));

        Assert.Contains("authenticate", engine.Calls);
        Assert.Contains("new-session:C:\\workspace", engine.Calls);
        Assert.Equal(["native request"], engine.PromptTexts);
    }

    [Fact]
    public async Task Dispose_WhenHostCleanupFails_CanBeRetried()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        var controller = fixture.CreateController();
        await controller.HandleAsync(
            new PromptWebCommand("done", ExecutionProfile.NativeProtected));
        var host = fixture.Factory.Hosts[0];
        host.StopFailuresRemaining = 1;
        host.DisposeFailuresRemaining = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.DisposeAsync().AsTask());
        await controller.DisposeAsync();

        Assert.Equal(2, host.StopCount);
        Assert.Equal(2, host.DisposeCount);
    }

    [Fact]
    public async Task Prompt_IsRejectedBeforeSidecarStartWhenCloudPolicyDisallowsProfile()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var gate = new AgentDeskCloudPolicyGate();
        gate.ApplyRemoteProfile();
        gate.ApplyPolicy(new AgentDeskCloudPolicySnapshot(
            AllowedExecutionProfiles: [ExecutionProfile.WslStrict],
            RemoteRunnerEnabled: false,
            UiAutomationEnabled: false,
            MaximumConcurrentJobs: 1,
            AllowedPluginPublishers: []));
        await using var controller = fixture.CreateController(
            Options with { CloudPolicyGate = gate });

        await controller.HandleAsync(
            new PromptWebCommand("blocked", ExecutionProfile.NativeProtected));

        Assert.Empty(fixture.Factory.Hosts);
        Assert.Contains(
            fixture.Events.Snapshot().OfType<EngineStatusWebEvent>(),
            item => item.Status == "error" &&
                item.Message is not null &&
                item.Message.Contains("策略", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WindowsAutomationAvailabilityUsesTheLocalToggleAndCurrentTeamPolicy()
    {
        var fixture = new ControllerFixture();
        fixture.UiPreferences.Preferences = UiPreferences.Default with
        {
            WindowsAutomationEnabled = true,
        };
        var gate = new AgentDeskCloudPolicyGate();
        await using var controller = fixture.CreateController(
            Options with { CloudPolicyGate = gate });

        Assert.True(await controller.IsWindowsAutomationEnabledAsync());

        gate.ApplyRemoteProfile();
        Assert.False(await controller.IsWindowsAutomationEnabledAsync());

        gate.ApplyPolicy(new AgentDeskCloudPolicySnapshot(
            AllowedExecutionProfiles: [ExecutionProfile.NativeProtected],
            RemoteRunnerEnabled: false,
            UiAutomationEnabled: false,
            MaximumConcurrentJobs: 1,
            AllowedPluginPublishers: []));
        Assert.False(await controller.IsWindowsAutomationEnabledAsync());

        gate.ApplyPolicy(new AgentDeskCloudPolicySnapshot(
            AllowedExecutionProfiles: [ExecutionProfile.NativeProtected],
            RemoteRunnerEnabled: false,
            UiAutomationEnabled: true,
            MaximumConcurrentJobs: 1,
            AllowedPluginPublishers: []));
        Assert.True(await controller.IsWindowsAutomationEnabledAsync());
    }

    [Fact]
    public async Task Dispose_StopsTheSidecarAndSuppressesLaterEngineEvents()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("done", ExecutionProfile.NativeProtected));
        var eventCount = fixture.Events.Snapshot().Count;

        await controller.DisposeAsync();
        engine.EmitText(new SessionId("session-1"), "ignored");

        Assert.Equal(1, fixture.Factory.Hosts[0].StopCount);
        Assert.Equal(1, fixture.Factory.Hosts[0].DisposeCount);
        Assert.Equal(eventCount, fixture.Events.Snapshot().Count);
    }

    [Fact]
    public async Task Dispose_CancelsOutsideStateGateAndCleansSidecarWhenCallbackFails()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        var workspaceContext = new ReentrantCancellationWorkspaceContextService();
        var controller = fixture.CreateController(
            Options with { WorkspaceContextService = workspaceContext });
        workspaceContext.ReenterAsync = () => controller.IsWindowsAutomationEnabledAsync();
        await controller.HandleAsync(new PromptWebCommand("done", ExecutionProfile.NativeProtected));
        var search = controller.HandleAsync(new WorkspaceFileSearchWebCommand(
            "00000000-0000-4000-8000-000000000001",
            1,
            "pending"));
        await workspaceContext.SearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await search.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(workspaceContext.ReentryBlocked);
        Assert.Equal(1, fixture.Factory.Hosts[0].StopCount);
        Assert.Equal(1, fixture.Factory.Hosts[0].DisposeCount);
    }

    [Fact]
    public async Task Dispose_CancelsRuntimeOperationsOutsideStateGate()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task>? reenterAsync = null;
        var reentryBlocked = false;
        engine.ListWorktreesHandler = (_, cancellationToken) =>
        {
            var cancelled = new TaskCompletionSource<IReadOnlyList<WorktreeRecord>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() =>
            {
                cancelled.TrySetCanceled(cancellationToken);
                var reentry = reenterAsync?.Invoke();
                if (reentry is not null)
                {
                    try
                    {
                        reentryBlocked = !reentry.Wait(TimeSpan.FromMilliseconds(250));
                    }
                    catch (AggregateException exception)
                        when (exception.InnerException is ObjectDisposedException)
                    {
                    }
                }
                throw new InvalidOperationException("runtime cancellation callback failed");
            });
            operationStarted.TrySetResult();
            return cancelled.Task;
        };
        var controller = fixture.CreateController();
        reenterAsync = () => controller.IsWindowsAutomationEnabledAsync();
        await controller.HandleAsync(new PromptWebCommand("done", ExecutionProfile.NativeProtected));
        var operation = controller.HandleAsync(
            new WorktreeListWebCommand(1, IncludeAll: false, Types: []));
        await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await operation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(reentryBlocked);
        Assert.Equal(1, fixture.Factory.Hosts[0].StopCount);
        Assert.Equal(1, fixture.Factory.Hosts[0].DisposeCount);
    }

    [Fact]
    public async Task WorkspaceChange_CancelsRuntimeOperationsOutsideStateGate()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        AgentDeskHostController? controller = null;
        var probe = ConfigureReentrantWorktreeCancellation(
            engine,
            () => controller!.IsWindowsAutomationEnabledAsync());
        controller = fixture.CreateController();
        await using var disposable = controller;
        await controller.HandleAsync(new PromptWebCommand("done", ExecutionProfile.NativeProtected));
        var operation = controller.HandleAsync(
            new WorktreeListWebCommand(1, IncludeAll: false, Types: []));
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(await controller.UpdateWorkspaceAsync("C:\\other"));
        await probe.ReentryCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await operation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(probe.ReentryBlocked);
    }

    [Fact]
    public async Task BeginMaintenance_CancelsRuntimeOperationsOutsideStateGate()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        AgentDeskHostController? controller = null;
        var probe = ConfigureReentrantWorktreeCancellation(
            engine,
            () => controller!.IsWindowsAutomationEnabledAsync());
        controller = fixture.CreateController();
        await using var disposable = controller;
        await controller.HandleAsync(new PromptWebCommand("done", ExecutionProfile.NativeProtected));
        var operation = controller.HandleAsync(
            new WorktreeListWebCommand(1, IncludeAll: false, Types: []));
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using (await controller.BeginMaintenanceAsync())
        {
            await probe.ReentryCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await operation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(probe.ReentryBlocked);
    }

    [Fact]
    public async Task EngineRestart_CancelsRuntimeOperationsOutsideStateGate()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var oldEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        _ = fixture.Factory.EnqueueEngine("/mnt/c/workspace", "session-2");
        AgentDeskHostController? controller = null;
        var probe = ConfigureReentrantWorktreeCancellation(
            oldEngine,
            () => controller!.IsWindowsAutomationEnabledAsync());
        controller = fixture.CreateController();
        await using var disposable = controller;
        await controller.HandleAsync(new PromptWebCommand("first", ExecutionProfile.NativeProtected));
        var operation = controller.HandleAsync(
            new WorktreeListWebCommand(1, IncludeAll: false, Types: []));
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await controller.HandleAsync(new PromptWebCommand("restart", ExecutionProfile.WslStrict));
        await probe.ReentryCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await operation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(probe.ReentryBlocked);
    }

    [Fact]
    public async Task EngineFault_CancelsPromptOutsideStateGate()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        AgentDeskHostController? controller = null;
        var probe = ConfigureReentrantPromptCancellation(
            engine,
            () => controller!.IsWindowsAutomationEnabledAsync());
        controller = fixture.CreateController();
        await using var disposable = controller;
        var prompt = controller.HandleAsync(
            new PromptWebCommand("running", ExecutionProfile.NativeProtected));
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        engine.RaiseFault(new InvalidDataException("fault"));
        await probe.ReentryCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await prompt.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(probe.ReentryBlocked);
    }

    [Fact]
    public async Task SidecarExit_CancelsPromptOutsideStateGate()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        AgentDeskHostController? controller = null;
        var probe = ConfigureReentrantPromptCancellation(
            engine,
            () => controller!.IsWindowsAutomationEnabledAsync());
        controller = fixture.CreateController();
        await using var disposable = controller;
        var prompt = controller.HandleAsync(
            new PromptWebCommand("running", ExecutionProfile.NativeProtected));
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Factory.Hosts[0].RaiseExited(exitCode: 17, wasExpected: false);
        await probe.ReentryCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await prompt.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(probe.ReentryBlocked);
    }

    private static ReentrantCancellationProbe ConfigureReentrantWorktreeCancellation(
        RecordingEngineClient engine,
        Func<Task> reenterAsync)
    {
        var probe = new ReentrantCancellationProbe(reenterAsync);
        engine.ListWorktreesHandler = (_, cancellationToken) =>
        {
            var cancelled = new TaskCompletionSource<IReadOnlyList<WorktreeRecord>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            probe.Register(
                cancellationToken,
                () => cancelled.TrySetCanceled(cancellationToken));
            return cancelled.Task;
        };
        return probe;
    }

    private static ReentrantCancellationProbe ConfigureReentrantPromptCancellation(
        RecordingEngineClient engine,
        Func<Task> reenterAsync)
    {
        var probe = new ReentrantCancellationProbe(reenterAsync);
        engine.PromptHandler = (_, _, cancellationToken) =>
        {
            var cancelled = new TaskCompletionSource<PromptResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            probe.Register(
                cancellationToken,
                () => cancelled.TrySetCanceled(cancellationToken));
            return cancelled.Task;
        };
        return probe;
    }

    private sealed class ReentrantCancellationProbe(Func<Task> reenterAsync)
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReentryCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ReentryBlocked { get; private set; }

        public void Register(CancellationToken cancellationToken, Action cancelOperation)
        {
            cancellationToken.Register(() =>
            {
                cancelOperation();
                try
                {
                    ReentryBlocked = !reenterAsync().Wait(TimeSpan.FromMilliseconds(250));
                }
                catch (AggregateException exception)
                    when (exception.InnerException is ObjectDisposedException)
                {
                }
                finally
                {
                    ReentryCompleted.TrySetResult();
                }
                throw new InvalidOperationException("cancellation callback failed");
            });
            Started.TrySetResult();
        }
    }

    [Fact]
    public async Task PermissionRequest_IsProjectedWithOnlyTheAdvertisedOptions()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("done", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();
        var request = new PermissionRequest(
            "permission-1",
            new SessionId("session-1"),
            "tool-7",
            "运行测试",
            [
                new PermissionOption("allow-once", "允许一次", PermissionOptionKind.AllowOnce),
                new PermissionOption("reject-once", "拒绝", PermissionOptionKind.RejectOnce),
            ],
            ["C:\\workspace\\test.ps1"],
            toolKind: "execute",
            rawInput: Json("{ \"command\": \"pwsh -File test.ps1\" }"));

        engine.EmitPermission(request);

        Assert.Equal(
            new PermissionRequestedWebEvent(
                request.RequestId,
                request.SessionId.Value,
                request.ToolCallId,
                request.Title,
                request.Options,
                request.Locations,
                request.ToolKind,
                request.RawInput),
            Assert.Single(fixture.Events.Snapshot()));
    }

    [Fact]
    public async Task ExtensionsList_ProjectsAllCatalogsWithoutSecretFields()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.ListMcpServersHandler = (_, _, _) => Task.FromResult<IReadOnlyList<McpServerCatalogItem>>([
            new("github", "GitHub", McpServerSource.Local, "config", McpServerTransportKind.Stdio,
                null, null, null, null, "npx", ["server"], ["GITHUB_TOKEN"], null),
        ]);
        engine.ListSkillsHandler = (_, _) => Task.FromResult<IReadOnlyList<SkillDescriptor>>([
            new("review", null, "Review", false, [], null, null, null, null, null, null,
                new Dictionary<string, string> { ["secret"] = "do-not-project" },
                "C:\\workspace\\SKILL.md", SkillScope.Repo, null, null, [], null, null, true, false, true),
        ]);
        engine.GetSkillsConfigurationHandler = (_, _) => Task.FromResult(
            new SkillsConfiguration([], [], 1, "ignored", []));
        engine.ListHooksHandler = (_, _) => Task.FromResult(
            new HookCatalog([], true, []));
        engine.ListPluginsHandler = (_, _) => Task.FromResult<IReadOnlyList<PluginDescriptor>>([]);
        engine.ListMarketplaceHandler = (_, _) => Task.FromResult(new MarketplaceCatalog([]));
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(new ExtensionsListWebCommand(
            "b5a5ef2f-1d3d-4ba8-a8c7-33c5c3f5d0c2",
            1,
            "session-42",
            UseCache: false));

        var catalog = Assert.Single(fixture.Events.Snapshot().OfType<ExtensionsCatalogWebEvent>());
        Assert.Equal("session-42", catalog.SessionId);
        Assert.Equal("GITHUB_TOKEN", Assert.Single(catalog.McpServers).EnvironmentVariableNames.Single());
        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => WebMessageProtocol.SerializeEvent(item).Contains("do-not-project", StringComparison.Ordinal));
        Assert.Contains("list-mcp:session-42:False", engine.Calls);
    }

    [Fact]
    public async Task ExtensionsAction_UpsertStdioMapsOnlyEnvironmentReferences()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        McpServerUpsertRequest? captured = null;
        engine.UpsertMcpServerHandler = (request, _) =>
        {
            captured = request;
            return Task.FromResult(true);
        };
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        using var payloadDocument = JsonDocument.Parse(
            "{\"serverName\":\"github\",\"command\":\"npx\",\"args\":[\"server\"],\"environment\":[{\"name\":\"GITHUB_TOKEN\",\"sourceVariable\":\"AGENTDESK_GITHUB_TOKEN\"}]}");
        await controller.HandleAsync(new ExtensionsActionWebCommand(
            "b5a5ef2f-1d3d-4ba8-a8c7-33c5c3f5d0c2",
            1,
            "session-42",
            ExtensionScope.Mcp,
            "upsert_stdio",
            Confirmed: true,
            payloadDocument.RootElement.Clone()));

        Assert.NotNull(captured);
        var configuration = Assert.IsType<McpStdioServerConfiguration>(captured.Configuration);
        Assert.Equal("AGENTDESK_GITHUB_TOKEN", Assert.Single(configuration.Environment!).SourceVariable);
        var completed = Assert.Single(fixture.Events.Snapshot().OfType<ExtensionsActionCompletedWebEvent>());
        Assert.Equal(ExtensionActionStatus.Success, completed.Outcome.Status);
        Assert.DoesNotContain("AGENTDESK_GITHUB_TOKEN", WebMessageProtocol.SerializeEvent(completed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtensionsAction_ConfirmedFlagCannotBypassMissingNativeApproval()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        await using var controller = new AgentDeskHostController(
            Options,
            fixture.Credentials,
            fixture.Factory,
            fixture.ProviderSettings,
            fixture.SessionIndex,
            fixture.UiPreferences,
            fixture.Notifications);
        controller.EventProduced += (_, webEvent) => fixture.Events.Add(webEvent);
        await controller.HandleAsync(new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(new ExtensionsActionWebCommand(
            Guid.NewGuid().ToString(),
            1,
            "session-42",
            ExtensionScope.Plugins,
            "reload",
            Confirmed: true,
            Json("{}")));

        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("plugin-action:", StringComparison.Ordinal));
        var completed = Assert.Single(
            fixture.Events.Snapshot().OfType<ExtensionsActionCompletedWebEvent>());
        Assert.Equal(ExtensionActionStatus.ConfirmationRequired, completed.Outcome.Status);
    }

    [Fact]
    public async Task ExtensionApproval_McpRequestIncludesOnlySanitizedEndpointIdentity()
    {
        const string executablePath = "C:\\Tools\\github-mcp.exe";
        const string environmentSecret = "SECRET_TOKEN_VARIABLE";
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var approvals = new List<ExtensionApprovalRequest>();
        fixture.ExtensionApprovalHandler = (request, _) =>
        {
            approvals.Add(request);
            return Task.FromResult(false);
        };
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));

        await controller.HandleAsync(new ExtensionsActionWebCommand(
            Guid.NewGuid().ToString(),
            1,
            "session-42",
            ExtensionScope.Mcp,
            "upsert_stdio",
            Confirmed: true,
            Json($$"""
                {
                  "serverName": "github",
                  "command": "{{executablePath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "args": ["--token", "literal-secret"],
                  "environment": [
                    {
                      "name": "TOKEN",
                      "sourceVariable": "{{environmentSecret}}"
                    }
                  ]
                }
                """)));

        await controller.HandleAsync(new ExtensionsActionWebCommand(
            Guid.NewGuid().ToString(),
            1,
            "session-42",
            ExtensionScope.Mcp,
            "upsert_http",
            Confirmed: true,
            Json("""
                {
                  "serverName": "gateway",
                  "url": "https://mcp.example.test/private?token=hidden",
                  "bearerTokenEnvironmentVariable": "MCP_BEARER_TOKEN"
                }
                """)));

        var longServerName = new string('s', 120);
        await controller.HandleAsync(new ExtensionsActionWebCommand(
            Guid.NewGuid().ToString(),
            1,
            "session-42",
            ExtensionScope.Mcp,
            "upsert_stdio",
            Confirmed: true,
            Json(JsonSerializer.Serialize(new
            {
                serverName = longServerName,
                command = "C:\\Tools\\long-name-mcp.exe",
            }))));

        Assert.Equal(3, approvals.Count);
        Assert.Equal("github : github-mcp.exe", approvals[0].Target);
        Assert.Equal("gateway : https://mcp.example.test", approvals[1].Target);
        Assert.Contains("long-name-mcp.exe", approvals[2].Target, StringComparison.Ordinal);
        Assert.True(approvals[2].Target.Length <= 128);
        var serialized = JsonSerializer.Serialize(approvals);
        Assert.DoesNotContain("C:\\Tools", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(environmentSecret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("literal-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("MCP_BEARER_TOKEN", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtensionApproval_RemoteTargetsIncludeCanonicalSourceIdentity()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var approvals = new List<ExtensionApprovalRequest>();
        fixture.ExtensionApprovalHandler = (request, _) =>
        {
            approvals.Add(request);
            return Task.FromResult(false);
        };
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));

        await controller.HandleAsync(new ExtensionsActionWebCommand(
            Guid.NewGuid().ToString(),
            1,
            "session-42",
            ExtensionScope.Plugins,
            "install",
            Confirmed: true,
            Json("{\"source\":\"https://github.com/example/review-plugin.git?token=hidden\"}")));
        await controller.HandleAsync(new ExtensionsActionWebCommand(
            Guid.NewGuid().ToString(),
            1,
            "session-42",
            ExtensionScope.Marketplace,
            "install",
            Confirmed: true,
            Json("""
                {
                  "source": "https://example.test/catalog.git?token=hidden",
                  "relativePath": "plugins/review"
                }
                """)));

        Assert.Equal(2, approvals.Count);
        Assert.Equal("github.com/example/review-plugin", approvals[0].Target);
        Assert.Equal("example.test/catalog : plugins/review", approvals[1].Target);
        Assert.All(approvals, approval =>
            Assert.DoesNotContain("hidden", approval.Target, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtensionApproval_NormalizesAlternateRemoteAndLocalPluginSources()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        _ = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var approvals = new List<ExtensionApprovalRequest>();
        fixture.ExtensionApprovalHandler = (request, _) =>
        {
            approvals.Add(request);
            return Task.FromResult(false);
        };
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));

        foreach (var source in new[]
                 {
                     "example/review-plugin",
                     "git@github.com:example/review-plugin.git",
                     "C:\\plugins\\review-plugin",
                 })
        {
            await controller.HandleAsync(new ExtensionsActionWebCommand(
                Guid.NewGuid().ToString(),
                1,
                "session-42",
                ExtensionScope.Plugins,
                "install",
                Confirmed: true,
                Json(JsonSerializer.Serialize(new { source }))));
        }

        Assert.Equal(
            [
                "github.com/example/review-plugin",
                "github.com/example/review-plugin",
                "C:/plugins/review-plugin",
            ],
            approvals.Select(approval => approval.Target).ToArray());
    }

    [Fact]
    public async Task ExtensionApproval_IsRequestedOncePerPluginInvocation()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var decisions = new Queue<bool>([true, false]);
        var approvals = new List<ExtensionApprovalRequest>();
        fixture.ExtensionApprovalHandler = (request, _) =>
        {
            approvals.Add(request);
            return Task.FromResult(decisions.Dequeue());
        };
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        foreach (var requestId in new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() })
        {
            await controller.HandleAsync(new ExtensionsActionWebCommand(
                requestId,
                1,
                "session-42",
                ExtensionScope.Plugins,
                "reload",
                Confirmed: true,
                Json("{}")));
        }

        Assert.Equal(2, approvals.Count);
        Assert.All(approvals, approval => Assert.Equal("plugins", approval.Target));
        Assert.Single(
            engine.Calls,
            call => call.StartsWith("plugin-action:", StringComparison.Ordinal));
        Assert.Equal(
            [ExtensionActionStatus.Success, ExtensionActionStatus.ConfirmationRequired],
            fixture.Events.Snapshot()
                .OfType<ExtensionsActionCompletedWebEvent>()
                .Select(webEvent => webEvent.Outcome.Status)
                .ToArray());
    }

    [Fact]
    public async Task ExtensionApproval_PolicyChangeWhilePendingRejectsBeforeEngineCall()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var approvalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalDecision = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ExtensionApprovalHandler = async (_, cancellationToken) =>
        {
            approvalStarted.TrySetResult();
            return await approvalDecision.Task.WaitAsync(cancellationToken);
        };
        var policyGate = new AgentDeskCloudPolicyGate();
        await using var controller = fixture.CreateController(
            Options with { CloudPolicyGate = policyGate });
        await controller.HandleAsync(new PromptWebCommand(
            "initialize",
            ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        var operation = controller.HandleAsync(new ExtensionsActionWebCommand(
            Guid.NewGuid().ToString(),
            1,
            "session-42",
            ExtensionScope.Plugins,
            "reload",
            Confirmed: true,
            Json("{}")));
        await approvalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        policyGate.ApplyRemoteProfile();
        approvalDecision.SetResult(true);
        await operation;

        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("plugin-action:", StringComparison.Ordinal));
        var completed = Assert.Single(
            fixture.Events.Snapshot().OfType<ExtensionsActionCompletedWebEvent>());
        Assert.Equal(ExtensionActionStatus.ValidationError, completed.Outcome.Status);
    }

    [Fact]
    public async Task ExtensionApproval_PolicyUpdateWaitsForActivePluginEngineCall()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var engineCallStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEngineCall = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PluginActionHandler = async (_, _, cancellationToken) =>
        {
            engineCallStarted.TrySetResult();
            await releaseEngineCall.Task.WaitAsync(cancellationToken);
            return new ExtensionActionOutcome(
                ExtensionActionStatus.Success,
                "",
                RequiresReload: false,
                RequiresRestart: false);
        };
        fixture.ExtensionApprovalHandler = (_, _) => Task.FromResult(true);
        var policyGate = new AgentDeskCloudPolicyGate();
        await using var controller = fixture.CreateController(
            Options with { CloudPolicyGate = policyGate });
        await controller.HandleAsync(new PromptWebCommand(
            "initialize",
            ExecutionProfile.NativeProtected));

        var operation = controller.HandleAsync(new ExtensionsActionWebCommand(
            Guid.NewGuid().ToString(),
            1,
            "session-42",
            ExtensionScope.Plugins,
            "reload",
            Confirmed: true,
            Json("{}")));
        await engineCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var policyUpdate = Task.Run(policyGate.ApplyRemoteProfile);
        var prematureCompletion = await Task.WhenAny(
            policyUpdate,
            Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(policyUpdate, prematureCompletion);

        releaseEngineCall.TrySetResult();
        await Task.WhenAll(operation, policyUpdate);
        Assert.Equal(AgentDeskCloudPolicyMode.RemoteUnverified, policyGate.Mode);
    }

    [Fact]
    public async Task MarketplaceAction_RequiresConfirmationWithoutCallingEngine()
    {
        var fixture = new ControllerFixture();
        fixture.ExtensionApprovalHandler = (_, _) => Task.FromResult(false);
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        using var payloadDocument = JsonDocument.Parse(
            "{\"source\":\"https://example.test/catalog.git\",\"relativePath\":\"plugins/review\"}");
        await controller.HandleAsync(new ExtensionsActionWebCommand(
            "b5a5ef2f-1d3d-4ba8-a8c7-33c5c3f5d0c2",
            1,
            "session-42",
            ExtensionScope.Marketplace,
            "install",
            Confirmed: false,
            payloadDocument.RootElement.Clone()));

        var completed = Assert.Single(fixture.Events.Snapshot().OfType<ExtensionsActionCompletedWebEvent>());
        Assert.Equal(ExtensionActionStatus.ConfirmationRequired, completed.Outcome.Status);
        Assert.DoesNotContain(engine.Calls, call => call.StartsWith("marketplace-action:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PluginInstall_RequiresConfirmationWithoutCallingEngine()
    {
        var fixture = new ControllerFixture();
        fixture.ExtensionApprovalHandler = (_, _) => Task.FromResult(false);
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        using var payloadDocument = JsonDocument.Parse(
            "{\"source\":\"https://github.com/example/review-plugin\"}");
        await controller.HandleAsync(new ExtensionsActionWebCommand(
            Guid.NewGuid().ToString(),
            1,
            "session-42",
            ExtensionScope.Plugins,
            "install",
            Confirmed: false,
            payloadDocument.RootElement.Clone()));

        var completed = Assert.Single(fixture.Events.Snapshot().OfType<ExtensionsActionCompletedWebEvent>());
        Assert.Equal(ExtensionActionStatus.ConfirmationRequired, completed.Outcome.Status);
        Assert.DoesNotContain(engine.Calls, call => call.StartsWith("plugin-action:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PluginInstall_DoesNotTrustClientClaimedPublisherUnderRemotePolicy()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        PluginAction? captured = null;
        engine.PluginActionHandler = (_, action, _) =>
        {
            captured = action;
            return Task.FromResult(new ExtensionActionOutcome(
                ExtensionActionStatus.Success, "", false, false));
        };
        var gate = new AgentDeskCloudPolicyGate();
        gate.ApplyRemoteProfile();
        gate.ApplyPolicy(new AgentDeskCloudPolicySnapshot(
            [ExecutionProfile.NativeProtected],
            RemoteRunnerEnabled: false,
            UiAutomationEnabled: false,
            MaximumConcurrentJobs: 1,
            AllowedPluginPublishers: ["publisher-1"]));
        await using var controller = fixture.CreateController(
            Options with { CloudPolicyGate = gate });
        await controller.HandleAsync(new PromptWebCommand(
            "initialize",
            ExecutionProfile.NativeProtected));

        await controller.HandleAsync(new ExtensionsActionWebCommand(
            Guid.NewGuid().ToString(),
            1,
            "session-42",
            ExtensionScope.Plugins,
            "install",
            Confirmed: true,
            Json("""
                {
                  "source": "https://github.com/example/review-plugin",
                  "publisherKeyId": "publisher-1"
                }
                """)));

        Assert.Null(captured);
        var completed = Assert.Single(
            fixture.Events.Snapshot().OfType<ExtensionsActionCompletedWebEvent>());
        Assert.Equal(ExtensionActionStatus.ValidationError, completed.Outcome.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("publisher-2")]
    public async Task PluginInstall_FailsClosedForMissingOrDisallowedRemotePublisher(
        string? publisherKeyId)
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var gate = new AgentDeskCloudPolicyGate();
        gate.ApplyRemoteProfile();
        gate.ApplyPolicy(new AgentDeskCloudPolicySnapshot(
            [ExecutionProfile.NativeProtected],
            RemoteRunnerEnabled: false,
            UiAutomationEnabled: false,
            MaximumConcurrentJobs: 1,
            AllowedPluginPublishers: ["publisher-1"]));
        await using var controller = fixture.CreateController(
            Options with { CloudPolicyGate = gate });
        await controller.HandleAsync(new PromptWebCommand(
            "initialize",
            ExecutionProfile.NativeProtected));
        fixture.Events.Clear();
        var payload = publisherKeyId is null
            ? Json("{\"source\":\"https://github.com/example/review-plugin\"}")
            : Json($$"""
                {
                  "source": "https://github.com/example/review-plugin",
                  "publisherKeyId": "{{publisherKeyId}}"
                }
                """);

        await controller.HandleAsync(new ExtensionsActionWebCommand(
            Guid.NewGuid().ToString(),
            1,
            "session-42",
            ExtensionScope.Plugins,
            "install",
            Confirmed: true,
            payload));

        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("plugin-action:", StringComparison.Ordinal));
        var completed = Assert.Single(
            fixture.Events.Snapshot().OfType<ExtensionsActionCompletedWebEvent>());
        Assert.Equal(ExtensionActionStatus.ValidationError, completed.Outcome.Status);
    }

    [Fact]
    public async Task RemotePolicyBlocksEveryPluginActionThatCanLoadCode()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var gate = new AgentDeskCloudPolicyGate();
        gate.ApplyRemoteProfile();
        gate.ApplyPolicy(new AgentDeskCloudPolicySnapshot(
            [ExecutionProfile.NativeProtected],
            RemoteRunnerEnabled: false,
            UiAutomationEnabled: false,
            MaximumConcurrentJobs: 1,
            AllowedPluginPublishers: ["publisher-1"]));
        await using var controller = fixture.CreateController(
            Options with { CloudPolicyGate = gate });
        await controller.HandleAsync(new PromptWebCommand(
            "initialize",
            ExecutionProfile.NativeProtected));

        (string Action, string Payload)[] actions =
        [
            ("reload", "{}"),
            ("enable", "{\"pluginId\":\"user/example/review\"}"),
            ("add", "{\"path\":\".agentdesk/plugins/review\"}"),
            ("remove", "{\"path\":\".agentdesk/plugins/review\"}"),
            ("install", "{\"source\":\"https://github.com/example/review-plugin\"}"),
            ("update", "{\"pluginId\":\"user/example/review\"}"),
            ("disable", "{\"pluginId\":\"user/example/review\"}"),
        ];
        foreach (var action in actions)
        {
            fixture.Events.Clear();
            await controller.HandleAsync(new ExtensionsActionWebCommand(
                Guid.NewGuid().ToString(),
                1,
                "session-42",
                ExtensionScope.Plugins,
                action.Action,
                Confirmed: true,
                Json(action.Payload)));

            var completed = Assert.Single(
                fixture.Events.Snapshot().OfType<ExtensionsActionCompletedWebEvent>());
            Assert.Equal(ExtensionActionStatus.ValidationError, completed.Outcome.Status);
            Assert.Contains("策略", completed.Outcome.Message, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("plugin-action:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("update")]
    [InlineData("uninstall")]
    public async Task RemotePolicyBlocksMarketplaceActionsThatCanLoadCode(string action)
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var gate = new AgentDeskCloudPolicyGate();
        gate.ApplyRemoteProfile();
        gate.ApplyPolicy(new AgentDeskCloudPolicySnapshot(
            [ExecutionProfile.NativeProtected],
            RemoteRunnerEnabled: false,
            UiAutomationEnabled: false,
            MaximumConcurrentJobs: 1,
            AllowedPluginPublishers: ["publisher-1"]));
        await using var controller = fixture.CreateController(
            Options with { CloudPolicyGate = gate });
        await controller.HandleAsync(new PromptWebCommand(
            "initialize",
            ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(new ExtensionsActionWebCommand(
            Guid.NewGuid().ToString(),
            1,
            "session-42",
            ExtensionScope.Marketplace,
            action,
            Confirmed: true,
            Json("""
                {
                  "source": "https://example.test/catalog.git",
                  "relativePath": "plugins/review"
                }
                """)));

        Assert.DoesNotContain(
            engine.Calls,
            call => call.StartsWith("marketplace-action:", StringComparison.Ordinal));
        var completed = Assert.Single(
            fixture.Events.Snapshot().OfType<ExtensionsActionCompletedWebEvent>());
        Assert.Equal(ExtensionActionStatus.ValidationError, completed.Outcome.Status);
    }

    [Fact]
    public async Task MarketplaceRefresh_DoesNotRequireMutationConfirmation()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        MarketplaceAction? captured = null;
        engine.MarketplaceActionHandler = (_, action, _) =>
        {
            captured = action;
            return Task.FromResult(new ExtensionActionOutcome(
                ExtensionActionStatus.Success, "", false, false));
        };
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        using var payloadDocument = JsonDocument.Parse("{}");
        await controller.HandleAsync(new ExtensionsActionWebCommand(
            Guid.NewGuid().ToString(),
            1,
            "session-42",
            ExtensionScope.Marketplace,
            "refresh",
            Confirmed: false,
            payloadDocument.RootElement.Clone()));

        Assert.IsType<MarketplaceAction.Refresh>(captured);
        var completed = Assert.Single(fixture.Events.Snapshot().OfType<ExtensionsActionCompletedWebEvent>());
        Assert.Equal(ExtensionActionStatus.Success, completed.Outcome.Status);
    }

    [Fact]
    public async Task ExtensionActions_MapAllSupportedDesktopMutationsToTypedEngineActions()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var hooks = new List<HookAction>();
        var plugins = new List<PluginAction>();
        var marketplace = new List<MarketplaceAction>();
        var success = new ExtensionActionOutcome(ExtensionActionStatus.Success, "", false, false);
        engine.HookActionHandler = (_, action, _) =>
        {
            hooks.Add(action);
            return Task.FromResult(success);
        };
        engine.PluginActionHandler = (_, action, _) =>
        {
            plugins.Add(action);
            return Task.FromResult(success);
        };
        engine.MarketplaceActionHandler = (_, action, _) =>
        {
            marketplace.Add(action);
            return Task.FromResult(success);
        };
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));

        await SendExtensionActionAsync("hooks", "add", "{\"path\":\"C:\\\\Users\\\\tester\\\\.grok\\\\hooks.json\"}");
        await SendExtensionActionAsync("hooks", "remove", "{\"path\":\"C:\\\\Users\\\\tester\\\\.grok\\\\hooks.json\"}");
        await SendExtensionActionAsync("hooks", "toggle_source", "{\"hookNames\":[\"safety:pre[0]\",\"safety:post[0]\"],\"disableSource\":true}");
        await SendExtensionActionAsync("plugins", "add", "{\"path\":\".agentdesk/plugins/review\"}");
        await SendExtensionActionAsync("plugins", "remove", "{\"path\":\".agentdesk/plugins/review\"}");
        await SendExtensionActionAsync("plugins", "install", "{\"source\":\"https://github.com/example/review-plugin\"}");
        await SendExtensionActionAsync("plugins", "update", "{\"pluginId\":\"user/12345678/review\"}");
        await SendExtensionActionAsync("marketplace", "refresh", "{\"source\":\"https://example.test/catalog.git\"}");

        Assert.Equal("C:\\Users\\tester\\.grok\\hooks.json", Assert.IsType<HookAction.Add>(hooks[0]).Path);
        Assert.Equal("C:\\Users\\tester\\.grok\\hooks.json", Assert.IsType<HookAction.Remove>(hooks[1]).Path);
        var sourceToggle = Assert.IsType<HookAction.ToggleSource>(hooks[2]);
        Assert.Equal(["safety:pre[0]", "safety:post[0]"], sourceToggle.HookNames);
        Assert.True(sourceToggle.DisableSource);
        Assert.Equal(".agentdesk/plugins/review", Assert.IsType<PluginAction.Add>(plugins[0]).Path);
        Assert.Equal(".agentdesk/plugins/review", Assert.IsType<PluginAction.Remove>(plugins[1]).Path);
        Assert.Equal("https://github.com/example/review-plugin", Assert.IsType<PluginAction.Install>(plugins[2]).Source);
        Assert.Equal("user/12345678/review", Assert.IsType<PluginAction.Update>(plugins[3]).PluginId);
        Assert.Equal("https://example.test/catalog.git", Assert.IsType<MarketplaceAction.Refresh>(marketplace[0]).Source);

        async Task SendExtensionActionAsync(string scope, string action, string payload)
        {
            var command = Assert.IsType<ExtensionsActionWebCommand>(WebMessageProtocol.ParseCommand($$"""
            {
              "schemaVersion": 1,
              "type": "extensions/action",
              "requestId": "{{Guid.NewGuid()}}",
              "workspaceGeneration": 1,
              "sessionId": "session-42",
              "scope": "{{scope}}",
              "action": "{{action}}",
              "confirmed": true,
              "payload": {{payload}}
            }
            """));
            await controller.HandleAsync(command);
        }
    }

    [Fact]
    public async Task ExtensionsFailure_ProjectsFixedMessageWithoutSidecarDetails()
    {
        const string secret = "extension-sidecar-secret";
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        engine.ListMcpServersHandler = (_, _, _) => throw new InvalidOperationException(secret);
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("initialize", ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        await controller.HandleAsync(new ExtensionsListWebCommand(
            "b5a5ef2f-1d3d-4ba8-a8c7-33c5c3f5d0c2",
            1,
            "session-42"));

        var error = Assert.Single(fixture.Events.Snapshot().OfType<ExtensionsErrorWebEvent>());
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtensionsList_WorkspaceInvalidationPublishesACorrelatedTerminalError()
    {
        const string requestId = "b5a5ef2f-1d3d-4ba8-a8c7-33c5c3f5d0c2";
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-42");
        var listStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ListMcpServersHandler = async (_, _, cancellationToken) =>
        {
            listStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        };
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand(
            "initialize",
            ExecutionProfile.NativeProtected));
        fixture.Events.Clear();

        var listing = controller.HandleAsync(new ExtensionsListWebCommand(
            requestId,
            1,
            "session-42",
            UseCache: false));
        await listStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await controller.UpdateWorkspaceAsync("C:\\other"));
        await listing.WaitAsync(TimeSpan.FromSeconds(5));

        var error = Assert.Single(fixture.Events.Snapshot().OfType<ExtensionsErrorWebEvent>());
        Assert.Equal(requestId, error.RequestId);
        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            item => item is ExtensionsCatalogWebEvent { RequestId: requestId });
    }

    [Fact]
    public async Task PermissionResponse_IsForwardedToTheCurrentEngine()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("done", ExecutionProfile.NativeProtected));

        await controller.HandleAsync(
            new PermissionRespondWebCommand(
                "permission-1",
                PermissionDecision.Selected("allow-once")));
        await controller.HandleAsync(
            new PermissionRespondWebCommand(
                "permission-2",
                PermissionDecision.Cancelled));

        Assert.Equal(
            [
                ("permission-1", PermissionDecision.Selected("allow-once")),
                ("permission-2", PermissionDecision.Cancelled),
            ],
            engine.PermissionResponses);
    }

    [Fact]
    public async Task PermissionRequest_ForAMismatchedSessionIsCancelledInsteadOfLeftPending()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var engine = fixture.Factory.EnqueueEngine("C:\\workspace", "session-1");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("done", ExecutionProfile.NativeProtected));

        engine.EmitPermission(CreatePermissionRequest("permission-stale", "other-session"));
        await engine.WaitForPermissionResponseAsync("permission-stale");

        Assert.Contains(
            ("permission-stale", PermissionDecision.Cancelled),
            engine.PermissionResponses);
        Assert.DoesNotContain(
            fixture.Events.Snapshot(),
            webEvent => webEvent is PermissionRequestedWebEvent);
    }

    [Fact]
    public async Task PermissionRequest_FromAStaleClientSnapshotIsCancelled()
    {
        var fixture = new ControllerFixture();
        fixture.Credentials.Save("xai", "xai-test-key");
        var oldEngine = fixture.Factory.EnqueueEngine("C:\\workspace", "old-session");
        _ = fixture.Factory.EnqueueEngine("/mnt/c/workspace", "new-session");
        await using var controller = fixture.CreateController();
        await controller.HandleAsync(new PromptWebCommand("native", ExecutionProfile.NativeProtected));
        var emitAfterDetach = oldEngine.CapturePermissionEmitter();
        await controller.HandleAsync(new PromptWebCommand("wsl", ExecutionProfile.WslStrict));

        emitAfterDetach(CreatePermissionRequest("permission-old", "old-session"));
        await oldEngine.WaitForPermissionResponseAsync("permission-old");

        Assert.Contains(
            ("permission-old", PermissionDecision.Cancelled),
            oldEngine.PermissionResponses);
    }

    private static PermissionRequest CreatePermissionRequest(
        string requestId,
        string sessionId) =>
        new(
            requestId,
            new SessionId(sessionId),
            "tool-1",
            "运行命令",
            [new PermissionOption("allow-once", "允许一次", PermissionOptionKind.AllowOnce)],
            [],
            toolKind: "execute",
            rawInput: Json("{ \"command\": \"echo test\" }"));

    private static SessionSummary Session(string id, string title, string workspacePath) =>
        new(
            new SessionId(id),
            title,
            workspacePath,
            DateTimeOffset.Parse("2026-07-16T08:00:00Z"),
            DateTimeOffset.Parse("2026-07-16T09:00:00Z"),
            4,
            ModelId: "grok-4.5");

    private static WorktreeRecord Worktree(string id, string path, string? sessionId) =>
        new(
            id,
            path,
            "C:\\workspace",
            "workspace",
            WorktreeKind.Session,
            WorktreeCreationType.Linked,
            GitReference: null,
            HeadCommit: "abc123",
            sessionId is null ? null : new SessionId(sessionId),
            CreatorProcessId: 42,
            DateTimeOffset.Parse("2026-07-17T08:00:00Z"),
            DateTimeOffset.Parse("2026-07-17T09:00:00Z"),
            WorktreeRecordStatus.Alive,
            new WorktreeMetadata("feature", UserProvided: true));

    private static BackgroundTaskSnapshot BackgroundTask(string id, string ownerSessionId) =>
        new(
            id,
            "dotnet test",
            null,
            "C:\\workspace",
            DateTimeOffset.Parse("2026-07-17T08:00:00Z"),
            null,
            "running",
            "C:\\temp\\task.log",
            Truncated: false,
            ExitCode: null,
            Signal: null,
            Completed: false,
            BackgroundTaskKind.Bash,
            ExplicitlyKilled: false,
            OwnerSessionId: ownerSessionId);

    private static SubagentSnapshot RunningSubagent(string id, string parentSessionId) =>
        new(
            id,
            parentSessionId,
            "child-session",
            "worker",
            "Run tests",
            DateTimeOffset.Parse("2026-07-17T08:00:00Z"),
            TimeSpan.FromSeconds(2),
            SubagentStatus.Running,
            TurnCount: 2,
            ToolCallCount: 4,
            TokensUsed: 8192,
            ContextWindowTokens: 131072,
            ContextUsagePercent: 6,
            ToolsUsed: ["shell_command"],
            ErrorCount: 0);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static MemoryWriteWebCommand ParseMemoryWriteCommand(
        string requestId,
        string content,
        bool confirmed,
        string fileId = "workspace",
        string? confirmationToken = null,
        int workspaceGeneration = 1,
        string sessionId = "session-42")
    {
        var tokenField = confirmationToken is null
            ? string.Empty
            : $",\"confirmationToken\":{JsonSerializer.Serialize(confirmationToken)}";
        return Assert.IsType<MemoryWriteWebCommand>(WebMessageProtocol.ParseCommand(
            $$"""
            {
              "schemaVersion": 1,
              "type": "memory/write",
              "requestId": "{{requestId}}",
              "workspaceGeneration": {{workspaceGeneration}},
              "sessionId": "{{sessionId}}",
              "fileId": "{{fileId}}",
              "content": {{JsonSerializer.Serialize(content)}},
              "confirmed": {{JsonSerializer.Serialize(confirmed)}}{{tokenField}}
            }
            """));
    }

    private static MemoryDeleteWebCommand ParseMemoryDeleteCommand(
        string requestId,
        bool confirmed,
        string fileId = "workspace",
        string? confirmationToken = null,
        int workspaceGeneration = 1)
    {
        var tokenField = confirmationToken is null
            ? string.Empty
            : $",\"confirmationToken\":{JsonSerializer.Serialize(confirmationToken)}";
        return Assert.IsType<MemoryDeleteWebCommand>(WebMessageProtocol.ParseCommand(
            $$"""
            {
              "schemaVersion": 1,
              "type": "memory/delete",
              "requestId": "{{requestId}}",
              "workspaceGeneration": {{workspaceGeneration}},
              "sessionId": "session-42",
              "fileId": "{{fileId}}",
              "confirmed": {{JsonSerializer.Serialize(confirmed)}}{{tokenField}}
            }
            """));
    }

    private static string MemoryConfirmationToken(MemoryMutationWebEvent mutation)
    {
        using var document = JsonDocument.Parse(WebMessageProtocol.SerializeEvent(mutation));
        var token = document.RootElement.GetProperty("confirmationToken").GetString();
        return Assert.IsType<string>(token);
    }

    private static EngineCapabilities Capabilities(
        bool agentDeskHealth = true,
        bool strictSandboxActive = true,
        bool imagePrompts = false,
        IReadOnlyCollection<SessionMode>? sessionModes = null) =>
        new(
            1,
            LoadSession: true,
            ImagePrompts: imagePrompts,
            AudioPrompts: false,
            EmbeddedContextPrompts: false,
            AgentDeskExtensions: agentDeskHealth,
            AgentDeskHealth: agentDeskHealth,
            StrictSandboxActive: strictSandboxActive)
        {
            SessionModes = sessionModes ?? [SessionMode.Default],
        };

    private static EngineCapabilities MemoryCapabilities() => Capabilities() with
    {
        Memory = new MemoryManagementCapabilities(
            1,
            List: true,
            Read: true,
            Write: true,
            Delete: true,
            MutationConfirmationRequired: true),
    };

    private sealed class ControllerFixture
    {
        public ControllerFixture(ICrashRecoveryStore? crashRecoveryStore = null)
        {
            CrashRecovery = crashRecoveryStore ?? new RecordingCrashRecoveryStore();
        }

        public ExtensionApprovalHandler? ExtensionApprovalHandler { get; set; } =
            (_, _) => Task.FromResult(true);

        public RecordingCredentialStore Credentials { get; } = new();

        public RecordingSidecarHostFactory Factory { get; } = new();

        public RecordingProviderSettingsStore ProviderSettings { get; } = new();

        public RecordingSessionIndexStore SessionIndex { get; } = new();

        public RecordingUiPreferencesStore UiPreferences { get; } = new();

        public ICrashRecoveryStore CrashRecovery { get; }

        public RecordingUserNotificationService Notifications { get; } = new();

        public EventCollector Events { get; } = new();

        public AgentDeskHostController CreateController(AgentDeskHostOptions? options = null)
        {
            var controller = new AgentDeskHostController(
                options ?? Options,
                Credentials,
                Factory,
                ProviderSettings,
                SessionIndex,
                UiPreferences,
                Notifications,
                ExtensionApprovalHandler,
                CrashRecovery);
            controller.EventProduced += (_, webEvent) => Events.Add(webEvent);
            return controller;
        }
    }

    private sealed class RecordingCrashRecoveryStore : ICrashRecoveryStore
    {
        public List<string> Operations { get; } = [];

        public CrashRecoveryMarker? Marker { get; private set; }

        public Exception? LoadException { get; set; }

        public Exception? SaveException { get; set; }

        public Exception? ClearException { get; set; }

        public int ClearCount { get; private set; }

        public Task<CrashRecoveryMarker?> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return LoadException is null
                ? Task.FromResult(Marker)
                : Task.FromException<CrashRecoveryMarker?>(LoadException);
        }

        public Task SaveAsync(
            CrashRecoveryMarker marker,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add("save");
            if (SaveException is not null)
            {
                throw SaveException;
            }
            Marker = marker;
            return Task.CompletedTask;
        }

        public void Seed(CrashRecoveryMarker marker)
        {
            Marker = marker;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add("clear");
            ClearCount++;
            if (ClearException is not null)
            {
                throw ClearException;
            }
            Marker = null;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingUserNotificationService : IUserNotificationService
    {
        private readonly ConcurrentQueue<(AgentDeskUserNotification Notification, string Language)>
            _notifications = new();

        public Exception? Failure { get; set; }

        public Task ShowAsync(
            AgentDeskUserNotification notification,
            string language,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _notifications.Enqueue((notification, language));
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }

        public IReadOnlyList<(AgentDeskUserNotification Notification, string Language)> Snapshot() =>
            _notifications.ToArray();

        public void Clear()
        {
            while (_notifications.TryDequeue(out _))
            {
            }
        }

        public async Task<(AgentDeskUserNotification Notification, string Language)> WaitForAsync(
            AgentDeskNotificationKind kind,
            TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (!cancellation.IsCancellationRequested)
            {
                var match = _notifications.FirstOrDefault(item => item.Notification.Kind == kind);
                if (match.Notification is not null)
                {
                    return match;
                }
                await Task.Delay(10, cancellation.Token);
            }
            throw new TimeoutException($"Notification {kind} was not published.");
        }
    }

    private sealed class ReentrantCancellationWorkspaceContextService : IWorkspaceContextService
    {
        public Func<Task>? ReenterAsync { get; set; }

        public TaskCompletionSource SearchStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ReentryBlocked { get; private set; }

        public Task<IReadOnlyList<WorkspaceContextFile>> ListInstructionFilesAsync(
            string workspacePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkspaceContextFile>>([]);

        public Task<string> ReadTextFileAsync(
            string workspacePath,
            string relativePath,
            CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

        public Task WriteInstructionFileAsync(
            string workspacePath,
            string relativePath,
            string content,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<IReadOnlyList<WorkspaceContextFile>> SearchFilesAsync(
            string workspacePath,
            string query,
            int limit = 50,
            CancellationToken cancellationToken = default)
        {
            var cancelled = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() =>
            {
                cancelled.TrySetCanceled(cancellationToken);
                var reentry = ReenterAsync?.Invoke();
                if (reentry is not null)
                {
                    try
                    {
                        ReentryBlocked = !reentry.Wait(TimeSpan.FromMilliseconds(250));
                    }
                    catch (AggregateException exception)
                        when (exception.InnerException is ObjectDisposedException)
                    {
                    }
                }
                throw new InvalidOperationException("cancellation callback failed");
            });
            SearchStarted.TrySetResult();
            await cancelled.Task;
            return [];
        }
    }

    private sealed class RecordingUiPreferencesStore : IUiPreferencesStore
    {
        public UiPreferences Preferences { get; set; } = UiPreferences.Default;

        public Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Preferences);

        public Task SaveAsync(
            UiPreferences preferences,
            CancellationToken cancellationToken = default)
        {
            Preferences = preferences;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProviderSettingsStore : IProviderSettingsStore
    {
        public ProviderProfile? Profile { get; set; }

        public Exception? SaveException { get; set; }

        public Task<ProviderProfile?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Profile);

        public Task SaveAsync(
            ProviderProfile profile,
            CancellationToken cancellationToken = default)
        {
            if (SaveException is not null)
            {
                throw SaveException;
            }
            Profile = profile;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSessionIndexStore : ISessionIndexStore
    {
        private readonly Dictionary<string, SessionSummary> _sessions = new(StringComparer.Ordinal);
        private readonly HashSet<string> _archived = new(StringComparer.Ordinal);

        public Exception? SetArchivedException { get; set; }

        public Task UpsertAsync(
            IReadOnlyCollection<SessionSummary> sessions,
            CancellationToken cancellationToken = default)
        {
            foreach (var session in sessions)
            {
                _sessions[session.SessionId.Value] = session;
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SessionSummary>> SearchAsync(
            string? workspacePath,
            string? query,
            bool archived,
            int limit,
            int offset,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SessionSummary> results = _sessions.Values
                .Where(session => _archived.Contains(session.SessionId.Value) == archived)
                .Where(
                    session => workspacePath is null || string.Equals(
                        session.WorkspacePath,
                        workspacePath,
                        StringComparison.OrdinalIgnoreCase))
                .Where(
                    session => string.IsNullOrEmpty(query) || session.Title.Contains(
                        query,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(session => session.UpdatedAt)
                .Skip(offset)
                .Take(limit)
                .ToArray();
            return Task.FromResult(results);
        }

        public Task<bool> SetArchivedAsync(
            SessionId sessionId,
            bool archived,
            CancellationToken cancellationToken = default)
        {
            if (SetArchivedException is not null)
            {
                throw SetArchivedException;
            }
            var exists = _sessions.ContainsKey(sessionId.Value);
            if (exists)
            {
                if (archived)
                {
                    _ = _archived.Add(sessionId.Value);
                }
                else
                {
                    _ = _archived.Remove(sessionId.Value);
                }
            }
            return Task.FromResult(exists);
        }

        public Task<IReadOnlySet<string>> GetArchivedIdsAsync(
            IReadOnlyCollection<SessionId> sessionIds,
            CancellationToken cancellationToken = default)
        {
            IReadOnlySet<string> matches = sessionIds
                .Select(sessionId => sessionId.Value)
                .Where(_archived.Contains)
                .ToHashSet(StringComparer.Ordinal);
            return Task.FromResult(matches);
        }
    }

    private sealed class RecordingCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Exception? SaveException { get; set; }

        public Exception? ReadException { get; set; }

        public List<string> SaveCalls { get; } = [];

        public List<string> DeleteCalls { get; } = [];

        public void Save(string name, string secret)
        {
            SaveCalls.Add(name);
            if (SaveException is not null)
            {
                throw SaveException;
            }

            _values[name] = secret;
        }

        public string? Read(string name)
        {
            if (ReadException is not null)
            {
                throw ReadException;
            }
            return _values.GetValueOrDefault(name);
        }

        public string? Peek(string name) => _values.GetValueOrDefault(name);

        public bool Delete(string name)
        {
            DeleteCalls.Add(name);
            return _values.Remove(name);
        }
    }

    private sealed class RecordingSidecarHostFactory : IAgentDeskSidecarHostFactory
    {
        private readonly Queue<RecordingSidecarHost> _pending = new();

        public List<RecordingSidecarHost> Hosts { get; } = [];

        public List<SidecarLaunchOptions> Launches { get; } = [];

        public RecordingEngineClient EnqueueEngine(string engineWorkspacePath, string sessionId)
        {
            var engine = new RecordingEngineClient(sessionId);
            _pending.Enqueue(new RecordingSidecarHost(engine, engineWorkspacePath, Launches));
            return engine;
        }

        public void EnqueueFailure(Exception exception)
        {
            _pending.Enqueue(new RecordingSidecarHost(exception, Launches));
        }

        public IAgentDeskSidecarHost Create()
        {
            var host = _pending.Dequeue();
            Hosts.Add(host);
            return host;
        }
    }

    private sealed class SequenceSidecarHostFactory(
        params IAgentDeskSidecarHost[] hosts) : IAgentDeskSidecarHostFactory
    {
        private readonly Queue<IAgentDeskSidecarHost> _hosts = new(hosts);

        public IAgentDeskSidecarHost Create() => _hosts.Dequeue();
    }

    private sealed class FaultingAcpSidecarHost : IAgentDeskSidecarHost
    {
        private readonly Pipe _serverToClient = new();
        private readonly Pipe _clientToServer = new();
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(5));
        private AcpEngineClient? _client;
        private StreamReader? _requestReader;
        private Task? _clientDisposeTask;

        public event EventHandler<SidecarExitedEventArgs>? Exited
        {
            add { }
            remove { }
        }

        public string? EngineWorkspacePath => "C:\\workspace";

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Task<IEngineClient> StartAsync(
            SidecarLaunchOptions options,
            CancellationToken cancellationToken = default)
        {
            _client = new AcpEngineClient(
                _serverToClient.Reader.AsStream(),
                _clientToServer.Writer.AsStream(),
                options.ApiKey);
            _requestReader = new StreamReader(
                _clientToServer.Reader.AsStream(),
                Encoding.UTF8,
                leaveOpen: true);
            return Task.FromResult<IEngineClient>(_client);
        }

        public async Task<JsonDocument> ReadRequestAsync()
        {
            var line = await _requestReader!.ReadLineAsync(_timeout.Token);
            Assert.NotNull(line);
            return JsonDocument.Parse(line);
        }

        public Task RespondAsync(JsonDocument request, string resultJson) => WriteAsync(
            $"{{\"jsonrpc\":\"2.0\",\"id\":{RequestId(request)},\"result\":{CompactJson(resultJson)}}}\n");

        public Task RespondWithErrorAsync(JsonDocument request, int code, string message) =>
            WriteAsync(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = RequestId(request),
                    error = new { code, message },
                }) + "\n");

        public Task WriteMalformedFrameAsync() => WriteAsync("{\"jsonrpc\":}\n");

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            await DisposeClientAsync().WaitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            await DisposeClientAsync();
            _requestReader?.Dispose();
            _timeout.Dispose();
        }

        private Task DisposeClientAsync() =>
            _clientDisposeTask ??= _client?.DisposeAsync().AsTask() ?? Task.CompletedTask;

        private async Task WriteAsync(string line)
        {
            await _serverToClient.Writer.WriteAsync(
                Encoding.UTF8.GetBytes(line),
                _timeout.Token);
        }

        private static long RequestId(JsonDocument request) =>
            request.RootElement.GetProperty("id").GetInt64();

        private static string CompactJson(string json)
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement);
        }
    }

    private sealed class RecordingSidecarHost : IAgentDeskSidecarHost
    {
        private readonly RecordingEngineClient? _engine;
        private readonly Exception? _startException;
        private readonly List<SidecarLaunchOptions> _launches;

        public RecordingSidecarHost(
            RecordingEngineClient engine,
            string engineWorkspacePath,
            List<SidecarLaunchOptions> launches)
        {
            _engine = engine;
            EngineWorkspacePath = engineWorkspacePath;
            _launches = launches;
        }

        public RecordingSidecarHost(Exception startException, List<SidecarLaunchOptions> launches)
        {
            _startException = startException;
            _launches = launches;
        }

        public event EventHandler<SidecarExitedEventArgs>? Exited;

        public string? EngineWorkspacePath { get; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int StopFailuresRemaining { get; set; }

        public int DisposeFailuresRemaining { get; set; }

        public Task<IEngineClient> StartAsync(
            SidecarLaunchOptions options,
            CancellationToken cancellationToken = default)
        {
            _launches.Add(options);
            return _startException is null
                ? Task.FromResult<IEngineClient>(_engine!)
                : Task.FromException<IEngineClient>(_startException);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            if (StopFailuresRemaining-- > 0)
            {
                throw new InvalidOperationException("stop failed");
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (DisposeFailuresRemaining-- > 0)
            {
                throw new InvalidOperationException("dispose failed");
            }
            return ValueTask.CompletedTask;
        }

        public void RaiseExited(int exitCode, bool wasExpected, object? sender = null) =>
            Exited?.Invoke(sender ?? this, new SidecarExitedEventArgs(exitCode, wasExpected));
    }

    private sealed class RecordingEngineClient(string sessionId) : IEngineClient
    {
        private EventHandler<EngineEvent>? _eventReceived;

        public event EventHandler<EngineEvent>? EventReceived
        {
            add
            {
                EventReceivedAddCount++;
                _eventReceived += value;
            }
            remove
            {
                EventReceivedRemoveCount++;
                _eventReceived -= value;
            }
        }

        public event EventHandler<PermissionRequest>? PermissionRequested;

        public event EventHandler<EngineFaultedEventArgs>? Faulted;

        public EngineCapabilities Capabilities { get; private set; } = EngineCapabilities.Uninitialized;

        public int EventReceivedAddCount { get; private set; }

        public int EventReceivedRemoveCount { get; private set; }

        public int EventReceivedSubscriberCount => _eventReceived?.GetInvocationList().Length ?? 0;

        public List<string> Calls { get; } = [];

        public List<string> PromptTexts { get; } = [];

        public List<(string Text, IReadOnlyList<PromptAttachment> Attachments)> AttachmentPrompts
        {
            get;
        } = [];

        public List<string> CancelledSessions { get; } = [];

        public List<(string RequestId, PermissionDecision Decision)> PermissionResponses { get; } = [];

        private readonly SemaphoreSlim _permissionResponseAdded = new(0);

        public JsonElement LastUpdate { get; private set; }

        public Func<SessionId, string, CancellationToken, Task<PromptResult>>? PromptHandler { get; set; }

        public Func<string?, CancellationToken, Task<IReadOnlyList<RuntimeCommand>>>?
            ListRuntimeCommandsHandler
        { get; set; }

        public Func<SessionId, CancellationToken, Task>? FlushMemoryHandler { get; set; }

        public Func<SessionId, CancellationToken, Task<MemoryFileListing>>?
            ListMemoryFilesHandler
        { get; set; }

        public Func<SessionId, MemoryFileId, CancellationToken, Task<MemoryFileDocument>>?
            ReadMemoryFileHandler
        { get; set; }

        public Func<SessionId, MemoryFileId, string, bool, CancellationToken, Task<MemoryMutationResult>>?
            WriteMemoryFileHandler
        { get; set; }

        public Func<SessionId, MemoryFileId, bool, CancellationToken, Task<MemoryMutationResult>>?
            DeleteMemoryFileHandler
        { get; set; }

        public Func<SessionId, CancellationToken, Task<IReadOnlyList<BackgroundTaskSnapshot>>>?
            ListBackgroundTasksHandler
        { get; set; }

        public Func<SessionId, string, CancellationToken, Task<BackgroundTaskKillOutcome>>?
            KillBackgroundTaskHandler
        { get; set; }

        public Func<SessionId, CancellationToken, Task<IReadOnlyList<SubagentSnapshot>>>?
            ListRunningSubagentsHandler
        { get; set; }

        public Func<SessionId, string, bool, TimeSpan?, CancellationToken, Task<SubagentSnapshot?>>?
            GetSubagentHandler
        { get; set; }

        public Func<SessionId, string, CancellationToken, Task<SubagentCancelResult>>?
            CancelSubagentHandler
        { get; set; }

        public Func<CancellationToken, Task<EngineCapabilities>>? InitializeHandler { get; set; }

        public EngineCapabilities? InitializeCapabilities { get; set; }

        public Func<CancellationToken, Task>? AuthenticateHandler { get; set; }

        public Func<string, CancellationToken, Task<SessionId>>? NewSessionHandler { get; set; }

        public Func<SessionId, CancellationToken, Task<EngineSessionDocument>>?
            ExportSessionHandler
        { get; set; }

        public Func<EngineSessionDocument, string, CancellationToken, Task<SessionId>>?
            ImportSessionHandler
        { get; set; }

        public Func<SessionId, string, CancellationToken, Task>? LoadSessionHandler { get; set; }

        public Func<string?, string?, string?, int, CancellationToken, Task<SessionPage>>?
            ListSessionsHandler
        { get; set; }

        public Func<SessionId, string, string?, CancellationToken, Task>?
            RenameSessionHandler
        { get; set; }

        public Func<
            SessionId,
            string,
            string,
            int?,
            string?,
            string?,
            string?,
            CancellationToken,
            Task<SessionForkResult>>? ForkSessionHandler
        { get; set; }

        public Func<SessionId, string?, CancellationToken, Task>? CompactSessionHandler { get; set; }

        public Func<SessionId, CancellationToken, Task<IReadOnlyList<SessionRewindPoint>>>?
            RewindPointsHandler
        { get; set; }

        public Func<SessionId, int, SessionRewindMode, bool, CancellationToken, Task<SessionRewindResult>>?
            RewindSessionHandler
        { get; set; }

        public Func<WorktreeCreateRequest, CancellationToken, Task<WorktreeCreateResult>>?
            CreateWorktreeHandler
        { get; set; }

        public Func<WorktreeListRequest, CancellationToken, Task<IReadOnlyList<WorktreeRecord>>>?
            ListWorktreesHandler
        { get; set; }

        public Func<WorktreeShowRequest, CancellationToken, Task<WorktreeRecord?>>?
            ShowWorktreeHandler
        { get; set; }

        public Func<WorktreeApplyRequest, CancellationToken, Task<WorktreeApplyResult>>?
            ApplyWorktreeHandler
        { get; set; }

        public Func<WorktreeRemoveRequest, CancellationToken, Task<WorktreeRemoveResult>>?
            RemoveWorktreeHandler
        { get; set; }

        public Func<WorktreeGcRequest, CancellationToken, Task<WorktreeGcResult>>?
            GcWorktreesHandler
        { get; set; }

        public Func<SessionId?, bool, CancellationToken, Task<IReadOnlyList<McpServerCatalogItem>>>?
            ListMcpServersHandler
        { get; set; }

        public Func<SessionId, string, bool, CancellationToken, Task<bool>>?
            ToggleMcpServerHandler
        { get; set; }

        public Func<McpServerUpsertRequest, CancellationToken, Task<bool>>?
            UpsertMcpServerHandler
        { get; set; }

        public Func<SessionId, string, CancellationToken, Task<bool>>?
            DeleteMcpServerHandler
        { get; set; }

        public Func<string, CancellationToken, Task<IReadOnlyList<SkillDescriptor>>>?
            ListSkillsHandler
        { get; set; }

        public Func<string, string?, CancellationToken, Task<SkillPathMutationResult>>?
            AddSkillPathHandler
        { get; set; }

        public Func<string, string?, CancellationToken, Task<SkillPathMutationResult>>?
            RemoveSkillPathHandler
        { get; set; }

        public Func<string?, CancellationToken, Task<SkillPathMutationResult>>?
            ResetSkillsHandler
        { get; set; }

        public Func<string?, CancellationToken, Task<SkillsConfiguration>>?
            GetSkillsConfigurationHandler
        { get; set; }

        public Func<SessionId, CancellationToken, Task<HookCatalog>>?
            ListHooksHandler
        { get; set; }

        public Func<SessionId, HookAction, CancellationToken, Task<ExtensionActionOutcome>>?
            HookActionHandler
        { get; set; }

        public Func<SessionId, CancellationToken, Task<IReadOnlyList<PluginDescriptor>>>?
            ListPluginsHandler
        { get; set; }

        public Func<SessionId, PluginAction, CancellationToken, Task<ExtensionActionOutcome>>?
            PluginActionHandler
        { get; set; }

        public Func<SessionId, CancellationToken, Task<MarketplaceCatalog>>?
            ListMarketplaceHandler
        { get; set; }

        public Func<SessionId, MarketplaceAction, CancellationToken, Task<ExtensionActionOutcome>>?
            MarketplaceActionHandler
        { get; set; }

        public Func<SessionId, CancellationToken, Task>? CancelHandler { get; set; }

        public Func<SessionId, SessionMode, CancellationToken, Task>? SetSessionModeHandler
        {
            get;
            set;
        }

        public Task<EngineCapabilities> InitializeAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("initialize");
            if (InitializeHandler is not null)
            {
                return InitializeHandler(cancellationToken);
            }

            Capabilities = InitializeCapabilities ?? AgentDeskHostControllerTests.Capabilities();
            return Task.FromResult(Capabilities);
        }

        public Task AuthenticateAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("authenticate");
            return AuthenticateHandler?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task<SessionId> NewSessionAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"new-session:{workingDirectory}");
            return NewSessionHandler?.Invoke(workingDirectory, cancellationToken)
                ?? Task.FromResult(new SessionId(sessionId));
        }

        public Task<EngineSessionDocument> ExportSessionAsync(
            SessionId activeSessionId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"export-session:{activeSessionId.Value}");
            return ExportSessionHandler?.Invoke(activeSessionId, cancellationToken)
                ?? Task.FromException<EngineSessionDocument>(new NotSupportedException());
        }

        public Task<SessionId> ImportSessionAsync(
            EngineSessionDocument document,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"import-session:{workingDirectory}");
            return ImportSessionHandler?.Invoke(document, workingDirectory, cancellationToken)
                ?? Task.FromException<SessionId>(new NotSupportedException());
        }

        public Task<WorktreeCreateResult> CreateWorktreeAsync(
            WorktreeCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"create-worktree:{request.SessionId.Value}:{request.SourcePath}");
            return CreateWorktreeHandler?.Invoke(request, cancellationToken)
                ?? Task.FromException<WorktreeCreateResult>(
                    new NotSupportedException(
                        "This engine client does not support worktree creation."));
        }

        public Task<IReadOnlyList<WorktreeRecord>> ListWorktreesAsync(
            WorktreeListRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"list-worktrees:{request.Repository}:{request.IncludeAll}");
            return ListWorktreesHandler?.Invoke(request, cancellationToken)
                ?? Task.FromException<IReadOnlyList<WorktreeRecord>>(
                    new NotSupportedException(
                        "This engine client does not support worktree listing."));
        }

        public Task<WorktreeRecord?> ShowWorktreeAsync(
            WorktreeShowRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"show-worktree:{request.IdOrPath}");
            return ShowWorktreeHandler?.Invoke(request, cancellationToken)
                ?? Task.FromException<WorktreeRecord?>(
                    new NotSupportedException(
                        "This engine client does not support worktree inspection."));
        }

        public Task<WorktreeApplyResult> ApplyWorktreeAsync(
            WorktreeApplyRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"apply-worktree:{request.SessionId.Value}:{request.WorktreePath}");
            return ApplyWorktreeHandler?.Invoke(request, cancellationToken)
                ?? Task.FromException<WorktreeApplyResult>(
                    new NotSupportedException(
                        "This engine client does not support worktree application."));
        }

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(
            WorktreeRemoveRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"remove-worktree:{request.IdOrPath}:{request.Force}:{request.DryRun}");
            return RemoveWorktreeHandler?.Invoke(request, cancellationToken)
                ?? Task.FromException<WorktreeRemoveResult>(
                    new NotSupportedException(
                        "This engine client does not support worktree removal."));
        }

        public Task<WorktreeGcResult> GcWorktreesAsync(
            WorktreeGcRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"gc-worktrees:{request.DryRun}:{request.MaximumAge}:{request.Force}");
            return GcWorktreesHandler?.Invoke(request, cancellationToken)
                ?? Task.FromException<WorktreeGcResult>(
                    new NotSupportedException(
                        "This engine client does not support worktree garbage collection."));
        }

        public Task<IReadOnlyList<McpServerCatalogItem>> ListMcpServersAsync(
            SessionId? activeSessionId,
            bool useCache = true,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"list-mcp:{activeSessionId?.Value}:{useCache}");
            return ListMcpServersHandler?.Invoke(activeSessionId, useCache, cancellationToken)
                ?? Task.FromResult<IReadOnlyList<McpServerCatalogItem>>([]);
        }

        public Task<bool> ToggleMcpServerAsync(
            SessionId activeSessionId,
            string serverName,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"toggle-mcp:{serverName}:{enabled}");
            return ToggleMcpServerHandler?.Invoke(activeSessionId, serverName, enabled, cancellationToken)
                ?? Task.FromResult(false);
        }

        public Task<bool> UpsertMcpServerAsync(
            McpServerUpsertRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"upsert-mcp:{request.ServerName}");
            return UpsertMcpServerHandler?.Invoke(request, cancellationToken)
                ?? Task.FromResult(false);
        }

        public Task<bool> DeleteMcpServerAsync(
            SessionId activeSessionId,
            string serverName,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"delete-mcp:{serverName}");
            return DeleteMcpServerHandler?.Invoke(activeSessionId, serverName, cancellationToken)
                ?? Task.FromResult(false);
        }

        public Task<IReadOnlyList<SkillDescriptor>> ListSkillsAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"list-skills:{workingDirectory}");
            return ListSkillsHandler?.Invoke(workingDirectory, cancellationToken)
                ?? Task.FromResult<IReadOnlyList<SkillDescriptor>>([]);
        }

        public Task<SkillPathMutationResult> AddSkillPathAsync(
            string path,
            string? workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"add-skill-path:{path}");
            return AddSkillPathHandler?.Invoke(path, workingDirectory, cancellationToken)
                ?? Task.FromResult(new SkillPathMutationResult(path, null, null, [], ""));
        }

        public Task<SkillPathMutationResult> RemoveSkillPathAsync(
            string path,
            string? workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"remove-skill-path:{path}");
            return RemoveSkillPathHandler?.Invoke(path, workingDirectory, cancellationToken)
                ?? Task.FromResult(new SkillPathMutationResult(path, null, null, [], ""));
        }

        public Task<SkillPathMutationResult> ResetSkillsAsync(
            string? workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("reset-skills");
            return ResetSkillsHandler?.Invoke(workingDirectory, cancellationToken)
                ?? Task.FromResult(new SkillPathMutationResult(null, null, null, [], ""));
        }

        public Task<SkillsConfiguration> GetSkillsConfigurationAsync(
            string? workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("get-skills-config");
            return GetSkillsConfigurationHandler?.Invoke(workingDirectory, cancellationToken)
                ?? Task.FromResult(new SkillsConfiguration([], [], 0, "", []));
        }

        public Task<HookCatalog> ListHooksAsync(
            SessionId activeSessionId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("list-hooks");
            return ListHooksHandler?.Invoke(activeSessionId, cancellationToken)
                ?? Task.FromResult(new HookCatalog([], false, []));
        }

        public Task<ExtensionActionOutcome> ExecuteHookActionAsync(
            SessionId activeSessionId,
            HookAction action,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"hook-action:{action.GetType().Name}");
            return HookActionHandler?.Invoke(activeSessionId, action, cancellationToken)
                ?? Task.FromResult(new ExtensionActionOutcome(ExtensionActionStatus.Success, "", false, false));
        }

        public Task<IReadOnlyList<PluginDescriptor>> ListPluginsAsync(
            SessionId activeSessionId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("list-plugins");
            return ListPluginsHandler?.Invoke(activeSessionId, cancellationToken)
                ?? Task.FromResult<IReadOnlyList<PluginDescriptor>>([]);
        }

        public Task<ExtensionActionOutcome> ExecutePluginActionAsync(
            SessionId activeSessionId,
            PluginAction action,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"plugin-action:{action.GetType().Name}");
            return PluginActionHandler?.Invoke(activeSessionId, action, cancellationToken)
                ?? Task.FromResult(new ExtensionActionOutcome(ExtensionActionStatus.Success, "", false, false));
        }

        public Task<MarketplaceCatalog> ListMarketplaceAsync(
            SessionId activeSessionId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("list-marketplace");
            return ListMarketplaceHandler?.Invoke(activeSessionId, cancellationToken)
                ?? Task.FromResult(new MarketplaceCatalog([]));
        }

        public Task<ExtensionActionOutcome> ExecuteMarketplaceActionAsync(
            SessionId activeSessionId,
            MarketplaceAction action,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"marketplace-action:{action.GetType().Name}");
            return MarketplaceActionHandler?.Invoke(activeSessionId, action, cancellationToken)
                ?? Task.FromResult(new ExtensionActionOutcome(ExtensionActionStatus.Success, "", false, false));
        }

        public Task LoadSessionAsync(
            SessionId sessionId,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"load-session:{sessionId.Value}:{workingDirectory}");
            return LoadSessionHandler?.Invoke(sessionId, workingDirectory, cancellationToken)
                ?? Task.CompletedTask;
        }

        public Task<IReadOnlyList<RuntimeCommand>> ListRuntimeCommandsAsync(
            string? workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"list-runtime-commands:{workingDirectory}");
            return ListRuntimeCommandsHandler?.Invoke(workingDirectory, cancellationToken)
                ?? Task.FromResult<IReadOnlyList<RuntimeCommand>>([]);
        }

        public Task<IReadOnlyList<BackgroundTaskSnapshot>> ListBackgroundTasksAsync(
            SessionId activeSessionId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"list-background-tasks:{activeSessionId.Value}");
            return ListBackgroundTasksHandler?.Invoke(activeSessionId, cancellationToken)
                ?? Task.FromResult<IReadOnlyList<BackgroundTaskSnapshot>>([]);
        }

        public Task<BackgroundTaskKillOutcome> KillBackgroundTaskAsync(
            SessionId activeSessionId,
            string taskId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"kill-background-task:{activeSessionId.Value}:{taskId}");
            return KillBackgroundTaskHandler?.Invoke(activeSessionId, taskId, cancellationToken)
                ?? Task.FromResult(BackgroundTaskKillOutcome.NotFound);
        }

        public Task<IReadOnlyList<SubagentSnapshot>> ListRunningSubagentsAsync(
            SessionId activeSessionId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"list-running-subagents:{activeSessionId.Value}");
            return ListRunningSubagentsHandler?.Invoke(activeSessionId, cancellationToken)
                ?? Task.FromResult<IReadOnlyList<SubagentSnapshot>>([]);
        }

        public Task<SubagentSnapshot?> GetSubagentAsync(
            SessionId activeSessionId,
            string subagentId,
            bool block = false,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"get-subagent:{activeSessionId.Value}:{subagentId}:{block}:{timeout}");
            return GetSubagentHandler?.Invoke(
                    activeSessionId,
                    subagentId,
                    block,
                    timeout,
                    cancellationToken)
                ?? Task.FromResult<SubagentSnapshot?>(null);
        }

        public Task<SubagentCancelResult> CancelSubagentAsync(
            SessionId activeSessionId,
            string subagentId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"cancel-subagent:{activeSessionId.Value}:{subagentId}");
            return CancelSubagentHandler?.Invoke(activeSessionId, subagentId, cancellationToken)
                ?? Task.FromResult(new SubagentCancelResult(SubagentCancelOutcome.NotFound));
        }

        public Task<SessionPage> ListSessionsAsync(
            string? workingDirectory,
            string? query,
            string? cursor,
            int limit,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"list-sessions:{workingDirectory}:{query}:{cursor}:{limit}");
            return ListSessionsHandler?.Invoke(
                    workingDirectory,
                    query,
                    cursor,
                    limit,
                    cancellationToken)
                ?? Task.FromResult(new SessionPage([]));
        }

        public Task RenameSessionAsync(
            SessionId sessionId,
            string title,
            string? workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"rename-session:{sessionId.Value}:{title}:{workingDirectory}");
            return RenameSessionHandler?.Invoke(
                    sessionId,
                    title,
                    workingDirectory,
                    cancellationToken)
                ?? Task.CompletedTask;
        }

        public Task<SessionForkResult> ForkSessionAsync(
            SessionId sourceSessionId,
            string sourceWorkingDirectory,
            string targetWorkingDirectory,
            int? targetPromptIndex = null,
            string? modelId = null,
            string? sessionKind = null,
            string? sourceWorkspacePath = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(
                $"fork-session:{sourceSessionId.Value}:{sourceWorkingDirectory}:{targetWorkingDirectory}:{targetPromptIndex}:{sessionKind}");
            return ForkSessionHandler?.Invoke(
                    sourceSessionId,
                    sourceWorkingDirectory,
                    targetWorkingDirectory,
                    targetPromptIndex,
                    modelId,
                    sessionKind,
                    sourceWorkspacePath,
                    cancellationToken)
                ?? Task.FromException<SessionForkResult>(new NotSupportedException());
        }

        public Task CompactSessionAsync(
            SessionId sessionId,
            string? userContext = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"compact-session:{sessionId.Value}:{userContext}");
            return CompactSessionHandler?.Invoke(sessionId, userContext, cancellationToken)
                ?? Task.CompletedTask;
        }

        public Task FlushMemoryAsync(
            SessionId activeSessionId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"flush-memory:{activeSessionId.Value}");
            return FlushMemoryHandler?.Invoke(activeSessionId, cancellationToken)
                ?? Task.CompletedTask;
        }

        public Task<MemoryFileListing> ListMemoryFilesAsync(
            SessionId activeSessionId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"list-memory:{activeSessionId.Value}");
            return ListMemoryFilesHandler?.Invoke(activeSessionId, cancellationToken)
                ?? Task.FromException<MemoryFileListing>(new NotSupportedException());
        }

        public Task<MemoryFileDocument> ReadMemoryFileAsync(
            SessionId activeSessionId,
            MemoryFileId fileId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"read-memory:{activeSessionId.Value}:{fileId.Value}");
            return ReadMemoryFileHandler?.Invoke(activeSessionId, fileId, cancellationToken)
                ?? Task.FromException<MemoryFileDocument>(new NotSupportedException());
        }

        public Task<MemoryMutationResult> WriteMemoryFileAsync(
            SessionId activeSessionId,
            MemoryFileId fileId,
            string content,
            bool confirmed = false,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"write-memory:{activeSessionId.Value}:{fileId.Value}:{confirmed}");
            return WriteMemoryFileHandler?.Invoke(
                    activeSessionId,
                    fileId,
                    content,
                    confirmed,
                    cancellationToken)
                ?? Task.FromException<MemoryMutationResult>(new NotSupportedException());
        }

        public Task<MemoryMutationResult> DeleteMemoryFileAsync(
            SessionId activeSessionId,
            MemoryFileId fileId,
            bool confirmed = false,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"delete-memory:{activeSessionId.Value}:{fileId.Value}:{confirmed}");
            return DeleteMemoryFileHandler?.Invoke(
                    activeSessionId,
                    fileId,
                    confirmed,
                    cancellationToken)
                ?? Task.FromException<MemoryMutationResult>(new NotSupportedException());
        }

        public Task<IReadOnlyList<SessionRewindPoint>> GetRewindPointsAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"rewind-points:{sessionId.Value}");
            return RewindPointsHandler?.Invoke(sessionId, cancellationToken)
                ?? Task.FromResult<IReadOnlyList<SessionRewindPoint>>([]);
        }

        public Task<SessionRewindResult> RewindSessionAsync(
            SessionId sessionId,
            int targetPromptIndex,
            SessionRewindMode mode,
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(
                $"rewind-session:{sessionId.Value}:{targetPromptIndex}:{mode.ToString().ToLowerInvariant()}:{force}");
            return RewindSessionHandler?.Invoke(
                    sessionId,
                    targetPromptIndex,
                    mode,
                    force,
                    cancellationToken)
                ?? Task.FromException<SessionRewindResult>(new NotSupportedException());
        }

        public Task SetSessionModeAsync(
            SessionId activeSessionId,
            SessionMode mode,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"set-mode:{mode.ToString().ToLowerInvariant()}");
            return SetSessionModeHandler?.Invoke(activeSessionId, mode, cancellationToken)
                ?? Task.CompletedTask;
        }

        public Task<PromptResult> PromptAsync(
            SessionId activeSessionId,
            string text,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"prompt:{activeSessionId.Value}");
            PromptTexts.Add(text);
            return PromptHandler?.Invoke(activeSessionId, text, cancellationToken)
                ?? Task.FromResult(new PromptResult(EngineStopReason.EndTurn, "end_turn"));
        }

        public Task<PromptResult> PromptWithAttachmentsAsync(
            SessionId activeSessionId,
            string text,
            IReadOnlyList<PromptAttachment> attachments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"prompt-with-attachments:{activeSessionId.Value}");
            AttachmentPrompts.Add((text, attachments));
            return PromptHandler?.Invoke(activeSessionId, text, cancellationToken)
                ?? Task.FromResult(new PromptResult(EngineStopReason.EndTurn, "end_turn"));
        }

        public Task CancelAsync(
            SessionId activeSessionId,
            CancellationToken cancellationToken = default)
        {
            CancelledSessions.Add(activeSessionId.Value);
            return CancelHandler?.Invoke(activeSessionId, cancellationToken) ?? Task.CompletedTask;
        }

        public Task<bool> RespondToPermissionAsync(
            string requestId,
            PermissionDecision decision,
            CancellationToken cancellationToken = default)
        {
            PermissionResponses.Add((requestId, decision));
            _permissionResponseAdded.Release();
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void EmitText(SessionId activeSessionId, string text)
        {
            using var document = JsonDocument.Parse(
                $$"""
                {
                  "sessionUpdate": "agent_message_chunk",
                  "content": { "type": "text", "text": "{{text}}" }
                }
                """);
            LastUpdate = document.RootElement.Clone();
            _eventReceived?.Invoke(
                this,
                new EngineEvent(activeSessionId, "agent_message_chunk", LastUpdate, metadata: null));
        }

        public void EmitUpdate(SessionId activeSessionId, string updateKind, string json)
        {
            using var document = JsonDocument.Parse(json);
            LastUpdate = document.RootElement.Clone();
            _eventReceived?.Invoke(
                this,
                new EngineEvent(activeSessionId, updateKind, LastUpdate, metadata: null));
        }

        public Action<EngineEvent> CaptureEventEmitter()
        {
            var handlers = _eventReceived;
            return update => handlers?.Invoke(this, update);
        }

        public void EmitPermission(PermissionRequest request) =>
            PermissionRequested?.Invoke(this, request);

        public void RaiseFault(Exception exception) =>
            Faulted?.Invoke(this, new EngineFaultedEventArgs(exception));

        public Action<PermissionRequest> CapturePermissionEmitter()
        {
            var handlers = PermissionRequested;
            return request => handlers?.Invoke(this, request);
        }

        public async Task WaitForPermissionResponseAsync(string requestId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!PermissionResponses.Any(response => response.RequestId == requestId))
            {
                await _permissionResponseAdded.WaitAsync(timeout.Token);
            }
        }
    }

    private sealed class EventCollector
    {
        private readonly ConcurrentQueue<WebEvent> _events = new();
        private readonly SemaphoreSlim _added = new(0);

        public void Add(WebEvent webEvent)
        {
            _events.Enqueue(webEvent);
            _added.Release();
        }

        public IReadOnlyList<WebEvent> Snapshot() => _events.ToArray();

        public void Clear()
        {
            while (_events.TryDequeue(out _))
            {
            }

            while (_added.Wait(0))
            {
            }
        }

        public async Task<T> WaitForAsync<T>(Func<T, bool> predicate, TimeSpan timeout)
            where T : WebEvent
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                var match = _events.OfType<T>().FirstOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }

                await _added.WaitAsync(cancellation.Token);
            }
        }
    }
}
