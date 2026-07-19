using AgentDesk.App.Workspace;

namespace AgentDesk.App.Bridge;

public sealed partial class AgentDeskHostController
{
    private readonly IWorkspaceContextService _workspaceContextService;
    private CancellationTokenSource? _workspaceFileSearchCancellation;

    private async Task HandleWorkspaceInstructionsListAsync(
        WorkspaceInstructionsListWebCommand command,
        CancellationToken cancellationToken)
    {
        var context = await CaptureWorkspaceContextAsync(
                command.WorkspaceGeneration,
                cancellationToken)
            .ConfigureAwait(false);
        if (context is null)
        {
            return;
        }

        try
        {
            var files = await _workspaceContextService
                .ListInstructionFilesAsync(context.WorkspacePath, cancellationToken)
                .ConfigureAwait(false);
            if (await IsCurrentWorkspaceContextAsync(context).ConfigureAwait(false))
            {
                Publish(new WorkspaceInstructionsListWebEvent(
                    command.RequestId,
                    command.WorkspaceGeneration,
                    files));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await PublishWorkspaceContextErrorIfCurrentAsync(
                    command.RequestId,
                    context,
                    WorkspaceContextOperation.InstructionsList)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleWorkspaceFileReadAsync(
        WorkspaceFileReadWebCommand command,
        CancellationToken cancellationToken)
    {
        var context = await CaptureWorkspaceContextAsync(
                command.WorkspaceGeneration,
                cancellationToken)
            .ConfigureAwait(false);
        if (context is null)
        {
            return;
        }

        try
        {
            var content = await _workspaceContextService
                .ReadTextFileAsync(
                    context.WorkspacePath,
                    command.RelativePath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (await IsCurrentWorkspaceContextAsync(context).ConfigureAwait(false))
            {
                Publish(new WorkspaceFileReadWebEvent(
                    command.RequestId,
                    command.WorkspaceGeneration,
                    command.RelativePath,
                    content));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await PublishWorkspaceContextErrorIfCurrentAsync(
                    command.RequestId,
                    context,
                    WorkspaceContextOperation.FileRead)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleWorkspaceFileSearchAsync(
        WorkspaceFileSearchWebCommand command,
        CancellationToken cancellationToken)
    {
        var context = await BeginWorkspaceFileSearchAsync(
                command.WorkspaceGeneration,
                cancellationToken)
            .ConfigureAwait(false);
        if (context is null)
        {
            return;
        }

        try
        {
            var files = await _workspaceContextService
                .SearchFilesAsync(
                    context.WorkspacePath,
                    command.Query,
                    limit: 100,
                    context.Cancellation.Token)
                .ConfigureAwait(false);
            if (await IsCurrentWorkspaceFileSearchAsync(context).ConfigureAwait(false))
            {
                Publish(new WorkspaceFileSearchWebEvent(
                    command.RequestId,
                    command.WorkspaceGeneration,
                    command.Query,
                    files));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (await IsCurrentWorkspaceFileSearchAsync(context).ConfigureAwait(false))
            {
                Publish(new WorkspaceContextErrorWebEvent(
                    command.RequestId,
                    context.WorkspaceGeneration,
                    WorkspaceContextOperation.FileSearch));
            }
        }
        finally
        {
            await CompleteWorkspaceFileSearchAsync(context).ConfigureAwait(false);
        }
    }

    private async Task HandleWorkspaceInstructionsWriteAsync(
        WorkspaceInstructionsWriteWebCommand command,
        CancellationToken cancellationToken)
    {
        WebEvent? producedEvent = null;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_workspacePath is null ||
                _workspaceGeneration != command.WorkspaceGeneration)
            {
                return;
            }

            try
            {
                await _workspaceContextService
                    .WriteInstructionFileAsync(
                        _workspacePath,
                        command.RelativePath,
                        command.Content,
                        cancellationToken)
                    .ConfigureAwait(false);
                producedEvent = new WorkspaceInstructionsWriteWebEvent(
                    command.RequestId,
                    command.WorkspaceGeneration,
                    command.RelativePath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                producedEvent = new WorkspaceContextErrorWebEvent(
                    command.RequestId,
                    command.WorkspaceGeneration,
                    WorkspaceContextOperation.InstructionsWrite);
            }
        }
        finally
        {
            _stateGate.Release();
        }

        if (producedEvent is not null)
        {
            Publish(producedEvent);
        }
    }

    private async Task<WorkspaceContextSnapshot?> CaptureWorkspaceContextAsync(
        int workspaceGeneration,
        CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return _workspacePath is not null &&
                   _workspaceGeneration == workspaceGeneration
                ? new WorkspaceContextSnapshot(_workspacePath, _workspaceGeneration)
                : null;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task<bool> IsCurrentWorkspaceContextAsync(
        WorkspaceContextSnapshot context)
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return !_disposed &&
                   _workspaceGeneration == context.WorkspaceGeneration &&
                   string.Equals(
                       _workspacePath,
                       context.WorkspacePath,
                       StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task<WorkspaceFileSearchContext?> BeginWorkspaceFileSearchAsync(
        int workspaceGeneration,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? previousCancellation = null;
        WorkspaceFileSearchContext? context = null;

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_workspacePath is null || _workspaceGeneration != workspaceGeneration)
            {
                return null;
            }

            previousCancellation = DetachWorkspaceFileSearchUnsafe();
            var searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            _workspaceFileSearchCancellation = searchCancellation;
            context = new WorkspaceFileSearchContext(
                _workspacePath,
                _workspaceGeneration,
                searchCancellation);
        }
        finally
        {
            _stateGate.Release();
        }

        TryCancel(previousCancellation);
        return context;
    }

    private async Task<bool> IsCurrentWorkspaceFileSearchAsync(
        WorkspaceFileSearchContext context)
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return !_disposed &&
                   ReferenceEquals(
                       _workspaceFileSearchCancellation,
                       context.Cancellation) &&
                   _workspaceGeneration == context.WorkspaceGeneration &&
                   string.Equals(
                       _workspacePath,
                       context.WorkspacePath,
                       StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task CompleteWorkspaceFileSearchAsync(
        WorkspaceFileSearchContext context)
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(
                    _workspaceFileSearchCancellation,
                    context.Cancellation))
            {
                _workspaceFileSearchCancellation = null;
            }
        }
        finally
        {
            _stateGate.Release();
            context.Cancellation.Dispose();
        }
    }

    private CancellationTokenSource? DetachWorkspaceFileSearchUnsafe()
    {
        var cancellation = _workspaceFileSearchCancellation;
        _workspaceFileSearchCancellation = null;
        return cancellation;
    }

    private async Task PublishWorkspaceContextErrorIfCurrentAsync(
        string requestId,
        WorkspaceContextSnapshot context,
        WorkspaceContextOperation operation)
    {
        if (await IsCurrentWorkspaceContextAsync(context).ConfigureAwait(false))
        {
            Publish(new WorkspaceContextErrorWebEvent(
                requestId,
                context.WorkspaceGeneration,
                operation));
        }
    }

    private sealed record WorkspaceContextSnapshot(
        string WorkspacePath,
        int WorkspaceGeneration);

    private sealed record WorkspaceFileSearchContext(
        string WorkspacePath,
        int WorkspaceGeneration,
        CancellationTokenSource Cancellation);
}
