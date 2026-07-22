using AgentDesk.App.Attachments;

namespace AgentDesk.App.Bridge;

public sealed class WebCommandDispatcher
{
    private readonly Func<WebCommand, CancellationToken, Task> _forwardCommand;
    private readonly Func<CancellationToken, Task<string?>> _pickWorkspace;
    private readonly Func<string, CancellationToken, Task<bool>> _updateWorkspace;
    private readonly Func<bool, CancellationToken, Task> _setModalState;
    private readonly Func<DesktopFileDialogRequest, CancellationToken, Task<string?>>
        _pickMaintenanceFile;
    private readonly Func<MaintenanceWebCommand, string?, CancellationToken, Task>
        _handleMaintenance;
    private readonly Func<CancellationToken, Task<string?>> _promptProviderCredential;
    private readonly Func<SaveProviderWebCommand, string?, CancellationToken, Task>
        _handleProviderSave;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>>
        _pickImageAttachments;
    private readonly NativeImageAttachmentStore? _imageAttachmentStore;
    private readonly Func<WebEvent, Task> _publishWebEvent;
    private readonly SemaphoreSlim _imageAttachmentOperationGate = new(1, 1);

    public WebCommandDispatcher(
        Func<WebCommand, CancellationToken, Task> forwardCommand,
        Func<CancellationToken, Task<string?>> pickWorkspace,
        Func<string, CancellationToken, Task<bool>> updateWorkspace,
        Func<bool, CancellationToken, Task> setModalState)
        : this(
            forwardCommand,
            pickWorkspace,
            updateWorkspace,
            setModalState,
            (_, _) => Task.FromResult<string?>(null),
            (command, _, cancellationToken) => forwardCommand(command, cancellationToken),
            _ => Task.FromResult<string?>(null),
            (command, _, cancellationToken) => forwardCommand(command, cancellationToken),
            _ => Task.FromResult<IReadOnlyList<string>>([]),
            null,
            _ => Task.CompletedTask)
    {
    }

    public WebCommandDispatcher(
        Func<WebCommand, CancellationToken, Task> forwardCommand,
        Func<CancellationToken, Task<string?>> pickWorkspace,
        Func<string, CancellationToken, Task<bool>> updateWorkspace,
        Func<bool, CancellationToken, Task> setModalState,
        Func<DesktopFileDialogRequest, CancellationToken, Task<string?>> pickMaintenanceFile,
        Func<MaintenanceWebCommand, string?, CancellationToken, Task> handleMaintenance)
        : this(
            forwardCommand,
            pickWorkspace,
            updateWorkspace,
            setModalState,
            pickMaintenanceFile,
            handleMaintenance,
            _ => Task.FromResult<string?>(null),
            (command, _, cancellationToken) => forwardCommand(command, cancellationToken),
            _ => Task.FromResult<IReadOnlyList<string>>([]),
            null,
            _ => Task.CompletedTask)
    {
    }

    public WebCommandDispatcher(
        Func<WebCommand, CancellationToken, Task> forwardCommand,
        Func<CancellationToken, Task<string?>> pickWorkspace,
        Func<string, CancellationToken, Task<bool>> updateWorkspace,
        Func<bool, CancellationToken, Task> setModalState,
        Func<DesktopFileDialogRequest, CancellationToken, Task<string?>> pickMaintenanceFile,
        Func<MaintenanceWebCommand, string?, CancellationToken, Task> handleMaintenance,
        Func<CancellationToken, Task<string?>> promptProviderCredential,
        Func<SaveProviderWebCommand, string?, CancellationToken, Task> handleProviderSave)
        : this(
            forwardCommand,
            pickWorkspace,
            updateWorkspace,
            setModalState,
            pickMaintenanceFile,
            handleMaintenance,
            promptProviderCredential,
            handleProviderSave,
            _ => Task.FromResult<IReadOnlyList<string>>([]),
            null,
            _ => Task.CompletedTask)
    {
    }

    public WebCommandDispatcher(
        Func<WebCommand, CancellationToken, Task> forwardCommand,
        Func<CancellationToken, Task<string?>> pickWorkspace,
        Func<string, CancellationToken, Task<bool>> updateWorkspace,
        Func<bool, CancellationToken, Task> setModalState,
        Func<DesktopFileDialogRequest, CancellationToken, Task<string?>> pickMaintenanceFile,
        Func<MaintenanceWebCommand, string?, CancellationToken, Task> handleMaintenance,
        Func<CancellationToken, Task<string?>> promptProviderCredential,
        Func<SaveProviderWebCommand, string?, CancellationToken, Task> handleProviderSave,
        Func<CancellationToken, Task<IReadOnlyList<string>>> pickImageAttachments,
        NativeImageAttachmentStore? imageAttachmentStore,
        Func<WebEvent, Task> publishWebEvent)
    {
        ArgumentNullException.ThrowIfNull(forwardCommand);
        ArgumentNullException.ThrowIfNull(pickWorkspace);
        ArgumentNullException.ThrowIfNull(updateWorkspace);
        ArgumentNullException.ThrowIfNull(setModalState);
        ArgumentNullException.ThrowIfNull(pickMaintenanceFile);
        ArgumentNullException.ThrowIfNull(handleMaintenance);
        ArgumentNullException.ThrowIfNull(promptProviderCredential);
        ArgumentNullException.ThrowIfNull(handleProviderSave);
        ArgumentNullException.ThrowIfNull(pickImageAttachments);
        ArgumentNullException.ThrowIfNull(publishWebEvent);

        _forwardCommand = forwardCommand;
        _pickWorkspace = pickWorkspace;
        _updateWorkspace = updateWorkspace;
        _setModalState = setModalState;
        _pickMaintenanceFile = pickMaintenanceFile;
        _handleMaintenance = handleMaintenance;
        _promptProviderCredential = promptProviderCredential;
        _handleProviderSave = handleProviderSave;
        _pickImageAttachments = pickImageAttachments;
        _imageAttachmentStore = imageAttachmentStore;
        _publishWebEvent = publishWebEvent;
    }

    public async Task DispatchAsync(
        WebCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command is ModalStateWebCommand modalState)
        {
            await _setModalState(modalState.IsOpen, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (command is SelectImageAttachmentsWebCommand selectImages)
        {
            var store = RequiredImageAttachmentStore();
            if (!await _imageAttachmentOperationGate
                    .WaitAsync(0, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    "Another native image attachment operation is already active.");
            }
            try
            {
                var paths = await _pickImageAttachments(cancellationToken).ConfigureAwait(false);
                var result = await store.StageAsync(paths, cancellationToken).ConfigureAwait(false);
                await _publishWebEvent(new ImageAttachmentsChangedWebEvent(
                        selectImages.RequestId,
                        result.Attachments,
                        Cancelled: paths.Count == 0,
                        result.Error))
                    .ConfigureAwait(false);
            }
            finally
            {
                _imageAttachmentOperationGate.Release();
            }
            return;
        }

        if (command is StageImageAttachmentsWebCommand stageImages)
        {
            var store = RequiredImageAttachmentStore();
            if (!await _imageAttachmentOperationGate
                    .WaitAsync(0, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    "Another native image attachment operation is already active.");
            }
            try
            {
                var result = await store
                    .StagePayloadsAsync(stageImages.Payloads, cancellationToken)
                    .ConfigureAwait(false);
                await _publishWebEvent(new ImageAttachmentsChangedWebEvent(
                        stageImages.RequestId,
                        result.Attachments,
                        Cancelled: false,
                        result.Error))
                    .ConfigureAwait(false);
            }
            finally
            {
                _imageAttachmentOperationGate.Release();
            }
            return;
        }

        if (command is DiscardImageAttachmentsWebCommand discardImages)
        {
            await _imageAttachmentOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RequiredImageAttachmentStore()
                    .DiscardAsync(discardImages.Tokens, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _imageAttachmentOperationGate.Release();
            }
            return;
        }

        if (command is PromptWebCommand prompt && prompt.AttachmentReferences.Count > 0)
        {
            if (prompt.Attachments.Count > 0)
            {
                throw new InvalidDataException(
                    "A prompt cannot contain native and web attachment representations together.");
            }
            await _imageAttachmentOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var resolved = await RequiredImageAttachmentStore()
                    .ResolveAndConsumeAsync(prompt.AttachmentReferences, cancellationToken)
                    .ConfigureAwait(false);
                command = prompt with { ResolvedAttachments = resolved };
            }
            finally
            {
                _imageAttachmentOperationGate.Release();
            }
        }

        if (command is MaintenanceWebCommand maintenance)
        {
            var request = MaintenanceDialogRequest(maintenance);
            var nativePath = request is null
                ? null
                : await _pickMaintenanceFile(request, cancellationToken).ConfigureAwait(false);
            await _handleMaintenance(maintenance, nativePath, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (command is SaveProviderWebCommand provider)
        {
            if (provider.UseExistingCredential == provider.ReplaceCredential)
            {
                throw new InvalidDataException("The provider credential intent is invalid.");
            }

            if (provider.UseExistingCredential)
            {
                await _handleProviderSave(provider, null, cancellationToken).ConfigureAwait(false);
                return;
            }

            var replacementCredential = await _promptProviderCredential(cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(replacementCredential))
            {
                return;
            }
            try
            {
                await _handleProviderSave(provider, replacementCredential, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                replacementCredential = null;
            }
            return;
        }

        if (command is not SelectWorkspaceWebCommand)
        {
            await _forwardCommand(command, cancellationToken).ConfigureAwait(false);
            return;
        }

        var workspacePath = await _pickWorkspace(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            _ = await _updateWorkspace(workspacePath, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ClearImageAttachmentsAsync(CancellationToken cancellationToken = default)
    {
        if (_imageAttachmentStore is null)
        {
            return;
        }

        await _imageAttachmentOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _imageAttachmentStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _imageAttachmentOperationGate.Release();
        }
    }

    public async Task DisposeImageAttachmentsAsync(CancellationToken cancellationToken = default)
    {
        if (_imageAttachmentStore is null)
        {
            return;
        }

        await _imageAttachmentOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _imageAttachmentStore.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _imageAttachmentOperationGate.Release();
        }
    }

    private NativeImageAttachmentStore RequiredImageAttachmentStore() =>
        _imageAttachmentStore ?? throw new InvalidDataException(
            "Native image attachments are unavailable in this host.");

    private static DesktopFileDialogRequest? MaintenanceDialogRequest(
        MaintenanceWebCommand command) => command switch
        {
            SessionExportWebCommand => new(
                DesktopFileDialogKind.Save,
                "session-export",
                "AgentDesk-session.agentdesk-session.json",
                ".json"),
            SessionImportWebCommand => new(
                DesktopFileDialogKind.Open,
                "session-import",
                string.Empty,
                ".json"),
            BackupCreateWebCommand => new(
                DesktopFileDialogKind.Save,
                "backup-create",
                "AgentDesk-backup.zip",
                ".zip"),
            BackupRestoreWebCommand => new(
                DesktopFileDialogKind.Open,
                "backup-restore",
                string.Empty,
                ".zip"),
            UpdateCheckWebCommand or UpdateApplyWebCommand => null,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
}
