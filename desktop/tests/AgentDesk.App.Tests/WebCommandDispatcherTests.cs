using AgentDesk.App.Attachments;
using AgentDesk.App.Bridge;
using AgentDesk.Core.Execution;
using AgentDesk.Core.Providers;

namespace AgentDesk.App.Tests;

public sealed class WebCommandDispatcherTests
{
    [Fact]
    public async Task SelectWorkspace_UpdatesTheControllerWhenThePickerReturnsAPath()
    {
        string? updatedPath = null;
        var forwarded = new List<WebCommand>();
        var dispatcher = new WebCommandDispatcher(
            (command, _) =>
            {
                forwarded.Add(command);
                return Task.CompletedTask;
            },
            _ => Task.FromResult<string?>("C:\\workspace"),
            (path, _) =>
            {
                updatedPath = path;
                return Task.FromResult(true);
            },
            (_, _) => Task.CompletedTask);

        await dispatcher.DispatchAsync(new SelectWorkspaceWebCommand());

        Assert.Equal("C:\\workspace", updatedPath);
        Assert.Empty(forwarded);
    }

    [Fact]
    public async Task SelectWorkspace_DoesNothingWhenThePickerIsCancelled()
    {
        var updateCount = 0;
        var dispatcher = new WebCommandDispatcher(
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult<string?>(null),
            (_, _) =>
            {
                updateCount++;
                return Task.FromResult(true);
            },
            (_, _) => Task.CompletedTask);

        await dispatcher.DispatchAsync(new SelectWorkspaceWebCommand());

        Assert.Equal(0, updateCount);
    }

    [Fact]
    public async Task NonWorkspaceCommand_IsForwardedUnchanged()
    {
        WebCommand? forwarded = null;
        var dispatcher = new WebCommandDispatcher(
            (command, _) =>
            {
                forwarded = command;
                return Task.CompletedTask;
            },
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult(true),
            (_, _) => Task.CompletedTask);
        var command = new UiReadyWebCommand();

        await dispatcher.DispatchAsync(command);

        Assert.Same(command, forwarded);
    }

    [Fact]
    public async Task ModalState_IsAppliedNativelyWithoutForwardingToTheController()
    {
        bool? appliedState = null;
        var forwarded = new List<WebCommand>();
        var dispatcher = new WebCommandDispatcher(
            (command, _) =>
            {
                forwarded.Add(command);
                return Task.CompletedTask;
            },
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult(true),
            (isOpen, _) =>
            {
                appliedState = isOpen;
                return Task.CompletedTask;
            });

        await dispatcher.DispatchAsync(new ModalStateWebCommand(IsOpen: true));

        Assert.True(appliedState);
        Assert.Empty(forwarded);
    }

    [Fact]
    public async Task FileMaintenanceCommands_UseNativePickerAndNeverForwardThePathToTheHost()
    {
        const string nativePath = "C:\\private\\maintenance.agentdesk";
        const string requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";
        var forwarded = new List<WebCommand>();
        var dialogRequests = new List<DesktopFileDialogRequest>();
        var maintenanceCalls = new List<(MaintenanceWebCommand Command, string? NativePath)>();
        var dispatcher = new WebCommandDispatcher(
            (command, _) =>
            {
                forwarded.Add(command);
                return Task.CompletedTask;
            },
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            (request, _) =>
            {
                dialogRequests.Add(request);
                return Task.FromResult<string?>(nativePath);
            },
            (command, path, _) =>
            {
                maintenanceCalls.Add((command, path));
                return Task.CompletedTask;
            });
        MaintenanceWebCommand[] commands =
        [
            new SessionExportWebCommand(requestId, "session-42"),
            new SessionImportWebCommand(requestId),
            new BackupCreateWebCommand(requestId),
            new BackupRestoreWebCommand(requestId),
        ];

        foreach (var command in commands)
        {
            await dispatcher.DispatchAsync(command);
        }

        Assert.Equal(4, dialogRequests.Count);
        Assert.Equal(commands, maintenanceCalls.Select(call => call.Command));
        Assert.All(maintenanceCalls, call => Assert.Equal(nativePath, call.NativePath));
        Assert.Empty(forwarded);
    }

    [Fact]
    public async Task CancelledMaintenancePicker_IsDeliveredAsANeutralNativeResult()
    {
        const string requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";
        var maintenanceCalls = new List<(MaintenanceWebCommand Command, string? NativePath)>();
        var dispatcher = new WebCommandDispatcher(
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<string?>(null),
            (command, path, _) =>
            {
                maintenanceCalls.Add((command, path));
                return Task.CompletedTask;
            });

        await dispatcher.DispatchAsync(new SessionImportWebCommand(requestId));

        var call = Assert.Single(maintenanceCalls);
        Assert.IsType<SessionImportWebCommand>(call.Command);
        Assert.Null(call.NativePath);
    }

    [Fact]
    public async Task UpdateCommands_DoNotOpenAFilePicker()
    {
        const string requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";
        var pickerCount = 0;
        var maintenanceCalls = new List<(MaintenanceWebCommand Command, string? NativePath)>();
        var dispatcher = new WebCommandDispatcher(
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            (_, _) =>
            {
                pickerCount++;
                return Task.FromResult<string?>(null);
            },
            (command, path, _) =>
            {
                maintenanceCalls.Add((command, path));
                return Task.CompletedTask;
            });

        await dispatcher.DispatchAsync(new UpdateCheckWebCommand(requestId));
        await dispatcher.DispatchAsync(new UpdateApplyWebCommand(requestId));

        Assert.Equal(0, pickerCount);
        Assert.Collection(
            maintenanceCalls,
            call =>
            {
                Assert.IsType<UpdateCheckWebCommand>(call.Command);
                Assert.Null(call.NativePath);
            },
            call =>
            {
                Assert.IsType<UpdateApplyWebCommand>(call.Command);
                Assert.Null(call.NativePath);
            });
    }

    [Fact]
    public async Task NativeImageSelectionAndPromptResolution_NeverForwardPathsOrContentToWebView()
    {
        const string requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var sourceDirectory = new AttachmentTestDirectory("agentdesk-image-source-");
        using var stagingDirectory = new AttachmentTestDirectory("agentdesk-image-stage-");
        var sourcePath = Path.Combine(sourceDirectory.FullName, "pixel.png");
        await File.WriteAllBytesAsync(sourcePath, png);
        await using var store = new NativeImageAttachmentStore(stagingDirectory.FullName);
        var events = new List<WebEvent>();
        var forwarded = new List<WebCommand>();
        var dispatcher = new WebCommandDispatcher(
            (command, _) =>
            {
                forwarded.Add(command);
                return Task.CompletedTask;
            },
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<string?>(null),
            (_, _, _) => Task.CompletedTask,
            _ => Task.FromResult<string?>(null),
            (_, _, _) => Task.CompletedTask,
            _ => Task.FromResult<IReadOnlyList<string>>([sourcePath]),
            store,
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            });

        await dispatcher.DispatchAsync(new SelectImageAttachmentsWebCommand(requestId));

        var changed = Assert.IsType<ImageAttachmentsChangedWebEvent>(Assert.Single(events));
        var reference = Assert.Single(changed.Attachments);
        Assert.DoesNotContain(sourcePath, WebMessageProtocol.SerializeEvent(changed), StringComparison.Ordinal);
        var prompt = new PromptWebCommand(
            "inspect",
            ExecutionProfile.NativeProtected,
            WorkspaceGeneration: 1,
            AttachmentReferenceItems: [reference]);

        await dispatcher.DispatchAsync(prompt);

        var forwardedPrompt = Assert.IsType<PromptWebCommand>(Assert.Single(forwarded));
        Assert.Empty(forwardedPrompt.Attachments);
        var resolved = Assert.Single(forwardedPrompt.ResolvedAttachments);
        Assert.Equal(Convert.ToBase64String(png), resolved.Base64Data);
        Assert.Empty(Directory.EnumerateFiles(
            stagingDirectory.FullName,
            "*.image",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancelledNativeImageSelection_ProducesANeutralEvent()
    {
        const string requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";
        using var stagingDirectory = new AttachmentTestDirectory("agentdesk-image-stage-");
        await using var store = new NativeImageAttachmentStore(stagingDirectory.FullName);
        var events = new List<WebEvent>();
        var dispatcher = new WebCommandDispatcher(
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<string?>(null),
            (_, _, _) => Task.CompletedTask,
            _ => Task.FromResult<string?>(null),
            (_, _, _) => Task.CompletedTask,
            _ => Task.FromResult<IReadOnlyList<string>>([]),
            store,
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            });

        await dispatcher.DispatchAsync(new SelectImageAttachmentsWebCommand(requestId));

        var changed = Assert.IsType<ImageAttachmentsChangedWebEvent>(Assert.Single(events));
        Assert.True(changed.Cancelled);
        Assert.Empty(changed.Attachments);
        Assert.Null(changed.Error);
    }

    [Fact]
    public async Task ConcurrentNativeImageSelection_FailsClosedBeforeOpeningAnotherPicker()
    {
        const string firstRequestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";
        const string secondRequestId = "d7a4f48a-b04e-4a28-b61c-34576fc8acb9";
        using var stagingDirectory = new AttachmentTestDirectory("agentdesk-image-stage-");
        await using var store = new NativeImageAttachmentStore(stagingDirectory.FullName);
        var firstSelection = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pickerCalls = 0;
        var dispatcher = new WebCommandDispatcher(
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<string?>(null),
            (_, _, _) => Task.CompletedTask,
            _ => Task.FromResult<string?>(null),
            (_, _, _) => Task.CompletedTask,
            _ => ++pickerCalls == 1
                ? firstSelection.Task
                : Task.FromResult<IReadOnlyList<string>>([]),
            store,
            _ => Task.CompletedTask);
        var first = dispatcher.DispatchAsync(new SelectImageAttachmentsWebCommand(firstRequestId));

        await Task.Yield();
        var concurrentError = await Record.ExceptionAsync(() =>
            dispatcher.DispatchAsync(new SelectImageAttachmentsWebCommand(secondRequestId)));
        firstSelection.SetResult([]);
        await first;

        Assert.IsType<InvalidDataException>(concurrentError);
        Assert.Equal(1, pickerCalls);
    }

    [Fact]
    public async Task ContextCleanup_WaitsForActiveSelectionAndDeletesItsStagedResult()
    {
        const string requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var sourceDirectory = new AttachmentTestDirectory("agentdesk-image-source-");
        using var stagingDirectory = new AttachmentTestDirectory("agentdesk-image-stage-");
        var sourcePath = Path.Combine(sourceDirectory.FullName, "pixel.png");
        await File.WriteAllBytesAsync(sourcePath, png);
        await using var store = new NativeImageAttachmentStore(stagingDirectory.FullName);
        var selection = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ImageAttachmentsChangedWebEvent? changed = null;
        var dispatcher = new WebCommandDispatcher(
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<string?>(null),
            (_, _, _) => Task.CompletedTask,
            _ => Task.FromResult<string?>(null),
            (_, _, _) => Task.CompletedTask,
            _ => selection.Task,
            store,
            webEvent =>
            {
                changed = Assert.IsType<ImageAttachmentsChangedWebEvent>(webEvent);
                return Task.CompletedTask;
            });
        var selecting = dispatcher.DispatchAsync(new SelectImageAttachmentsWebCommand(requestId));

        await Task.Yield();
        var clearing = dispatcher.ClearImageAttachmentsAsync();
        Assert.False(clearing.IsCompleted);
        selection.SetResult([sourcePath]);
        await selecting;
        await clearing;

        Assert.NotNull(changed);
        var reference = Assert.Single(changed.Attachments);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ResolveAndConsumeAsync([reference]));
        Assert.Empty(Directory.EnumerateFiles(
            stagingDirectory.FullName,
            "*.image",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AttachmentDisposal_WaitsForActiveSelectionAndReleasesTheStagingLease()
    {
        const string requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var sourceDirectory = new AttachmentTestDirectory("agentdesk-image-source-");
        using var stagingDirectory = new AttachmentTestDirectory("agentdesk-image-stage-");
        var sourcePath = Path.Combine(sourceDirectory.FullName, "pixel.png");
        await File.WriteAllBytesAsync(sourcePath, png);
        await using var store = new NativeImageAttachmentStore(stagingDirectory.FullName);
        var selection = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new WebCommandDispatcher(
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<string?>(null),
            (_, _, _) => Task.CompletedTask,
            _ => Task.FromResult<string?>(null),
            (_, _, _) => Task.CompletedTask,
            _ => selection.Task,
            store,
            _ => Task.CompletedTask);
        var selecting = dispatcher.DispatchAsync(new SelectImageAttachmentsWebCommand(requestId));

        await Task.Yield();
        var disposing = dispatcher.DisposeImageAttachmentsAsync();
        Assert.False(disposing.IsCompleted);
        selection.SetResult([sourcePath]);
        await selecting;
        await disposing;

        Assert.Empty(Directory.EnumerateDirectories(
            stagingDirectory.FullName,
            "window-*",
            SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.EnumerateFiles(
            stagingDirectory.FullName,
            "*.image",
            SearchOption.AllDirectories));
        var movedDirectory = stagingDirectory.FullName + "-moved";
        Directory.Move(stagingDirectory.FullName, movedDirectory);
        Directory.Move(movedDirectory, stagingDirectory.FullName);
    }

    [Fact]
    public async Task PromptResolution_IsFailClosedAndOneShotWhenForwardingFails()
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var sourceDirectory = new AttachmentTestDirectory("agentdesk-image-source-");
        using var stagingDirectory = new AttachmentTestDirectory("agentdesk-image-stage-");
        var sourcePath = Path.Combine(sourceDirectory.FullName, "pixel.png");
        await File.WriteAllBytesAsync(sourcePath, png);
        await using var store = new NativeImageAttachmentStore(stagingDirectory.FullName);
        var reference = Assert.Single((await store.StageAsync([sourcePath])).Attachments);
        var dispatcher = new WebCommandDispatcher(
            (_, _) => throw new InvalidOperationException("synthetic forwarding failure"),
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<string?>(null),
            (_, _, _) => Task.CompletedTask,
            _ => Task.FromResult<string?>(null),
            (_, _, _) => Task.CompletedTask,
            _ => Task.FromResult<IReadOnlyList<string>>([]),
            store,
            _ => Task.CompletedTask);
        var prompt = new PromptWebCommand(
            "inspect",
            ExecutionProfile.NativeProtected,
            AttachmentReferenceItems: [reference]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchAsync(prompt));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ResolveAndConsumeAsync([reference]));
    }

    [Fact]
    public async Task ProviderReuse_SkipsTheNativePromptAndPassesNoCredentialToTheHost()
    {
        var promptCount = 0;
        var providerCalls = new List<(SaveProviderWebCommand Command, string? Credential)>();
        var command = new SaveProviderWebCommand(
            new ProviderProfile("https://example.com/v1", "grok-4.5"),
            UseExistingCredential: true,
            ReplaceCredential: false);
        var dispatcher = CreateProviderDispatcher(
            _ =>
            {
                promptCount++;
                return Task.FromResult<string?>("must-not-be-requested");
            },
            (value, credential, _) =>
            {
                providerCalls.Add((value, credential));
                return Task.CompletedTask;
            });

        await dispatcher.DispatchAsync(command);

        Assert.Equal(0, promptCount);
        var call = Assert.Single(providerCalls);
        Assert.Same(command, call.Command);
        Assert.Null(call.Credential);
    }

    [Fact]
    public async Task ProviderReplacement_UsesTheNativePromptAndPassesItsCredentialToTheHost()
    {
        const string nativeCredential = "synthetic-native-credential";
        var providerCalls = new List<(SaveProviderWebCommand Command, string? Credential)>();
        var command = new SaveProviderWebCommand(
            new ProviderProfile("https://example.com/v1", "grok-4.5"),
            UseExistingCredential: false,
            ReplaceCredential: true);
        var dispatcher = CreateProviderDispatcher(
            _ => Task.FromResult<string?>(nativeCredential),
            (value, credential, _) =>
            {
                providerCalls.Add((value, credential));
                return Task.CompletedTask;
            });

        await dispatcher.DispatchAsync(command);

        var call = Assert.Single(providerCalls);
        Assert.Same(command, call.Command);
        Assert.Equal(nativeCredential, call.Credential);
    }

    [Fact]
    public async Task CancelledProviderReplacement_DoesNotInvokeTheHost()
    {
        var hostCallCount = 0;
        var dispatcher = CreateProviderDispatcher(
            _ => Task.FromResult<string?>(null),
            (_, _, _) =>
            {
                hostCallCount++;
                return Task.CompletedTask;
            });

        await dispatcher.DispatchAsync(
            new SaveProviderWebCommand(
                new ProviderProfile("https://example.com/v1", "grok-4.5"),
                UseExistingCredential: false,
                ReplaceCredential: true));

        Assert.Equal(0, hostCallCount);
    }

    private static WebCommandDispatcher CreateProviderDispatcher(
        Func<CancellationToken, Task<string?>> promptProviderCredential,
        Func<SaveProviderWebCommand, string?, CancellationToken, Task> handleProviderSave) =>
        new(
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<string?>(null),
            (_, _, _) => Task.CompletedTask,
            promptProviderCredential,
            handleProviderSave);
}
