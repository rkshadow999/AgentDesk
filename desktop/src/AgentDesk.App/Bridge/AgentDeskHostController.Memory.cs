using System.Security.Cryptography;
using System.Text;
using AgentDesk.Core.Engine;

namespace AgentDesk.App.Bridge;

public sealed partial class AgentDeskHostController
{
    private const int MaximumMemoryConfirmationChallenges = 256;

    private readonly Dictionary<string, MemoryConfirmationChallenge>
        _memoryConfirmationChallenges = new(StringComparer.Ordinal);

    private string MemoryBrowserErrorMessage => Message(
        "\u65e0\u6cd5\u8bfb\u53d6\u6216\u66f4\u65b0\u8bb0\u5fc6\u6587\u4ef6\u3002",
        "Memory files could not be read or updated.");

    private string MemoryConfirmationRequiredMessage => Message(
        "\u6b64\u64cd\u4f5c\u9700\u8981\u786e\u8ba4\u3002",
        "This operation requires confirmation.");

    private string MemoryConfirmationInvalidMessage => Message(
        "\u786e\u8ba4\u5df2\u5931\u6548\u6216\u4e0e\u5f53\u524d\u64cd\u4f5c\u4e0d\u5339\u914d\uff0c\u8bf7\u91cd\u65b0\u64cd\u4f5c\u3002",
        "The confirmation expired or does not match this operation. Try again.");

    private string MemoryWriteSucceededMessage => Message(
        "\u8bb0\u5fc6\u6587\u4ef6\u5df2\u66f4\u65b0\u3002",
        "The memory file was updated.");

    private string MemoryDeleteSucceededMessage => Message(
        "\u8bb0\u5fc6\u6587\u4ef6\u5df2\u5220\u9664\u3002",
        "The memory file was deleted.");

    private string MemoryNotFoundMessage => Message(
        "\u672a\u627e\u5230\u8bb0\u5fc6\u6587\u4ef6\u3002",
        "The memory file was not found.");

    private Task HandleMemoryListAsync(
        MemoryListWebCommand command,
        CancellationToken cancellationToken) =>
        ExecuteMemoryOperationAsync(
            command,
            MemoryOperation.List,
            fileId: null,
            requiresIdle: false,
            (context, token) => context.Client.ListMemoryFilesAsync(context.SessionId, token),
            listing => new MemoryListedWebEvent(
                command.RequestId,
                command.WorkspaceGeneration,
                command.SessionId,
                listing),
            cancellationToken);

    private Task HandleMemoryReadAsync(
        MemoryReadWebCommand command,
        CancellationToken cancellationToken) =>
        ExecuteMemoryOperationAsync(
            command,
            MemoryOperation.Read,
            command.FileId,
            requiresIdle: false,
            (context, token) => context.Client.ReadMemoryFileAsync(
                context.SessionId,
                command.FileId,
                token),
            document => new MemoryDocumentWebEvent(
                command.RequestId,
                command.WorkspaceGeneration,
                command.SessionId,
                document),
            cancellationToken);

    private Task HandleMemoryWriteAsync(
        MemoryWriteWebCommand command,
        CancellationToken cancellationToken) =>
        ExecuteMemoryMutationAsync(
            command,
            MemoryOperation.Write,
            command.FileId,
            command.Content,
            command.Confirmed,
            command.ConfirmationToken,
            (context, token) => context.Client.WriteMemoryFileAsync(
                context.SessionId,
                command.FileId,
                command.Content,
                confirmed: true,
                cancellationToken: token),
            cancellationToken);

    private Task HandleMemoryDeleteAsync(
        MemoryDeleteWebCommand command,
        CancellationToken cancellationToken) =>
        ExecuteMemoryMutationAsync(
            command,
            MemoryOperation.Delete,
            command.FileId,
            content: null,
            command.Confirmed,
            command.ConfirmationToken,
            (context, token) => context.Client.DeleteMemoryFileAsync(
                context.SessionId,
                command.FileId,
                confirmed: true,
                cancellationToken: token),
            cancellationToken);

    private async Task ExecuteMemoryOperationAsync<TResult>(
        MemoryWebCommand command,
        MemoryOperation operation,
        MemoryFileId? fileId,
        bool requiresIdle,
        Func<WorkspaceOperationContext, CancellationToken, Task<TResult>> execute,
        Func<TResult, WebEvent> successEvent,
        CancellationToken cancellationToken)
    {
        var requestIdentity = await CaptureMemoryRequestIdentityAsync(command, cancellationToken)
            .ConfigureAwait(false);
        WorkspaceOperationContext? context = null;
        WebEvent? producedEvent = null;
        var callerCancelled = false;
        var staleRequest = false;
        try
        {
            context = await BeginWorkspaceOperationAsync(
                    command.WorkspaceGeneration,
                    cancellationToken,
                    command.SessionId,
                    requiresIdle)
                .ConfigureAwait(false);
            var result = await execute(context, context.Cancellation.Token).ConfigureAwait(false);
            producedEvent = successEvent(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            callerCancelled = true;
        }
        catch (StaleWorkspaceOperationException)
        {
            staleRequest = true;
        }
        catch (Exception)
        {
            // Sidecar failures may contain local paths or credentials.
        }

        var isCurrent = context is not null
            ? await CompleteWorkspaceOperationAsync(context).ConfigureAwait(false)
            : !staleRequest && await IsCurrentMemoryRequestAsync(requestIdentity)
                .ConfigureAwait(false);
        if (callerCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (!isCurrent)
        {
            return;
        }

        Publish(producedEvent ?? new MemoryErrorWebEvent(
            command.RequestId,
            command.WorkspaceGeneration,
            command.SessionId,
            operation,
            MemoryBrowserErrorMessage,
            fileId));
    }

    private async Task ExecuteMemoryMutationAsync(
        MemoryWebCommand command,
        MemoryOperation operation,
        MemoryFileId fileId,
        string? content,
        bool confirmed,
        string? confirmationToken,
        Func<WorkspaceOperationContext, CancellationToken, Task<MemoryMutationResult>> execute,
        CancellationToken cancellationToken)
    {
        var requestIdentity = await CaptureMemoryRequestIdentityAsync(command, cancellationToken)
            .ConfigureAwait(false);
        WorkspaceOperationContext? context = null;
        WebEvent? producedEvent = null;
        var callerCancelled = false;
        var staleRequest = false;
        try
        {
            context = await BeginWorkspaceOperationAsync(
                    command.WorkspaceGeneration,
                    cancellationToken,
                    command.SessionId,
                    requiresIdle: true)
                .ConfigureAwait(false);
            if (confirmationToken is null)
            {
                var issuedToken = await IssueMemoryConfirmationAsync(
                        context,
                        operation,
                        fileId,
                        content)
                    .ConfigureAwait(false);
                if (issuedToken is not null)
                {
                    producedEvent = new MemoryMutationWebEvent(
                        command.RequestId,
                        command.WorkspaceGeneration,
                        command.SessionId,
                        operation,
                        fileId,
                        new MemoryMutationResult(
                            MemoryMutationStatus.ConfirmationRequired,
                            MemoryConfirmationRequiredMessage,
                            File: null),
                        issuedToken);
                }
            }
            else if (!await ConsumeMemoryConfirmationAsync(
                             context,
                             operation,
                             fileId,
                             content,
                             confirmed,
                             confirmationToken)
                         .ConfigureAwait(false))
            {
                producedEvent = new MemoryErrorWebEvent(
                    command.RequestId,
                    command.WorkspaceGeneration,
                    command.SessionId,
                    operation,
                    MemoryConfirmationInvalidMessage,
                    fileId);
            }
            else
            {
                var result = await execute(context, context.Cancellation.Token).ConfigureAwait(false);
                if (result.Status is MemoryMutationStatus.ConfirmationRequired)
                {
                    throw new InvalidDataException(
                        "The engine requested confirmation after the host challenge was consumed.");
                }
                producedEvent = new MemoryMutationWebEvent(
                    command.RequestId,
                    command.WorkspaceGeneration,
                    command.SessionId,
                    operation,
                    fileId,
                    SanitizeMemoryMutationResult(operation, result));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            callerCancelled = true;
        }
        catch (StaleWorkspaceOperationException)
        {
            staleRequest = true;
            if (confirmationToken is not null)
            {
                await DiscardMemoryConfirmationAsync(confirmationToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Sidecar failures may contain local paths or credentials.
        }

        var isCurrent = context is not null
            ? await CompleteWorkspaceOperationAsync(context).ConfigureAwait(false)
            : !staleRequest && await IsCurrentMemoryRequestAsync(requestIdentity)
                .ConfigureAwait(false);
        if (callerCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (!isCurrent)
        {
            return;
        }

        Publish(producedEvent ?? new MemoryErrorWebEvent(
            command.RequestId,
            command.WorkspaceGeneration,
            command.SessionId,
            operation,
            MemoryBrowserErrorMessage,
            fileId));
    }

    private async Task<string?> IssueMemoryConfirmationAsync(
        WorkspaceOperationContext context,
        MemoryOperation operation,
        MemoryFileId fileId,
        string? content)
    {
        var challenge = CreateMemoryConfirmationChallenge(context, operation, fileId, content);
        await _stateGate.WaitAsync(context.Cancellation.Token).ConfigureAwait(false);
        try
        {
            if (!IsWorkspaceOperationCurrentUnsafe(context))
            {
                return null;
            }

            foreach (var previousToken in _memoryConfirmationChallenges
                         .Where(item => item.Value == challenge)
                         .Select(item => item.Key)
                         .ToArray())
            {
                _memoryConfirmationChallenges.Remove(previousToken);
            }
            if (_memoryConfirmationChallenges.Count >= MaximumMemoryConfirmationChallenges)
            {
                _memoryConfirmationChallenges.Clear();
            }

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _memoryConfirmationChallenges.Add(token, challenge);
            return token;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task<bool> ConsumeMemoryConfirmationAsync(
        WorkspaceOperationContext context,
        MemoryOperation operation,
        MemoryFileId fileId,
        string? content,
        bool confirmed,
        string confirmationToken)
    {
        var expected = CreateMemoryConfirmationChallenge(context, operation, fileId, content);
        await _stateGate.WaitAsync(context.Cancellation.Token).ConfigureAwait(false);
        try
        {
            if (!_memoryConfirmationChallenges.Remove(
                    confirmationToken,
                    out var challenge))
            {
                return false;
            }
            return confirmed &&
                IsWorkspaceOperationCurrentUnsafe(context) &&
                challenge == expected;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task DiscardMemoryConfirmationAsync(string confirmationToken)
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _memoryConfirmationChallenges.Remove(confirmationToken);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task<MemoryRequestIdentity?> CaptureMemoryRequestIdentityAsync(
        MemoryWebCommand command,
        CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return _workspacePath is not null &&
                   _workspaceGeneration == command.WorkspaceGeneration &&
                   _client is not null &&
                   _sessionId is not null &&
                   string.Equals(_sessionId.Value, command.SessionId, StringComparison.Ordinal)
                ? new MemoryRequestIdentity(
                    _client,
                    _sessionId.Value,
                    _workspacePath,
                    _workspaceGeneration,
                    _engineGeneration)
                : null;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task<bool> IsCurrentMemoryRequestAsync(MemoryRequestIdentity? identity)
    {
        if (identity is null)
        {
            return false;
        }
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return !_disposed &&
                _workspaceGeneration == identity.WorkspaceGeneration &&
                _engineGeneration == identity.EngineGeneration &&
                ReferenceEquals(_client, identity.Client) &&
                string.Equals(_sessionId?.Value, identity.SessionId, StringComparison.Ordinal) &&
                string.Equals(_workspacePath, identity.WorkspacePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private static MemoryConfirmationChallenge CreateMemoryConfirmationChallenge(
        WorkspaceOperationContext context,
        MemoryOperation operation,
        MemoryFileId fileId,
        string? content) =>
        new(
            context.Client,
            context.SessionId.Value,
            context.WorkspacePath,
            context.WorkspaceGeneration,
            context.EngineGeneration,
            operation,
            fileId,
            content is null
                ? null
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))));

    private void InvalidateMemoryConfirmationsUnsafe() =>
        _memoryConfirmationChallenges.Clear();

    private MemoryMutationResult SanitizeMemoryMutationResult(
        MemoryOperation operation,
        MemoryMutationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result with
        {
            Message = result.Status switch
            {
                MemoryMutationStatus.ConfirmationRequired =>
                    MemoryConfirmationRequiredMessage,
                MemoryMutationStatus.Success when operation is MemoryOperation.Write =>
                    MemoryWriteSucceededMessage,
                MemoryMutationStatus.Success => MemoryDeleteSucceededMessage,
                MemoryMutationStatus.NotFound => MemoryNotFoundMessage,
                _ => throw new InvalidDataException("The memory mutation status is invalid."),
            },
        };
    }

    private sealed record MemoryConfirmationChallenge(
        IEngineClient Client,
        string SessionId,
        string WorkspacePath,
        int WorkspaceGeneration,
        int EngineGeneration,
        MemoryOperation Operation,
        MemoryFileId FileId,
        string? ContentDigest);

    private sealed record MemoryRequestIdentity(
        IEngineClient Client,
        string SessionId,
        string WorkspacePath,
        int WorkspaceGeneration,
        int EngineGeneration);
}
