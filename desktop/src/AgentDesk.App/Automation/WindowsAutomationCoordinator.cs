using AgentDesk.App.Bridge;

namespace AgentDesk.App.Automation;

public enum WindowsAutomationAction
{
    FocusWindow,
    Invoke,
    SetValue,
}

public sealed record WindowsAutomationRequest
{
    public WindowsAutomationRequest(
        WindowsAutomationAction action,
        int processId,
        string? automationId,
        string? name,
        string? value)
    {
        if (processId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }
        ValidateSelector(automationId, nameof(automationId));
        ValidateSelector(name, nameof(name));
        if (action is not WindowsAutomationAction.FocusWindow &&
            string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A Windows Automation control selector is required.");
        }
        if (action is WindowsAutomationAction.SetValue)
        {
            if (value is null || value.Length > 8 * 1024 || value.Contains('\0'))
            {
                throw new ArgumentException("The Windows Automation value is invalid.", nameof(value));
            }
        }
        else if (value is not null)
        {
            throw new ArgumentException("Only set-value accepts a value.", nameof(value));
        }

        Action = action;
        ProcessId = processId;
        AutomationId = string.IsNullOrWhiteSpace(automationId) ? null : automationId;
        Name = string.IsNullOrWhiteSpace(name) ? null : name;
        Value = value;
    }

    public WindowsAutomationAction Action { get; }

    public int ProcessId { get; }

    public string? AutomationId { get; }

    public string? Name { get; }

    public string? Value { get; }

    private static void ValidateSelector(string? value, string parameterName)
    {
        if (value is not null && (value.Length > 256 || value.Contains('\0')))
        {
            throw new ArgumentException("The Windows Automation selector is invalid.", parameterName);
        }
    }
}

public sealed record WindowsAutomationResult(
    WindowsAutomationAction Action,
    int ProcessId,
    string Target);

public sealed record WindowsAutomationApprovalRequest(
    WindowsAutomationAction Action,
    int ProcessId,
    string Target,
    int ValueCharacters);

public interface IWindowsAutomationExecutor
{
    Task<WindowsAutomationResult> ExecuteAsync(
        WindowsAutomationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record WindowsAutomationWebCommand : WebCommand
{
    public WindowsAutomationWebCommand(
        string requestId,
        WindowsAutomationAction action,
        int processId,
        string? automationId,
        string? name,
        string? value)
    {
        if (!Guid.TryParseExact(requestId, "D", out _))
        {
            throw new ArgumentException("The Windows Automation request ID is invalid.", nameof(requestId));
        }
        RequestId = requestId;
        Request = new WindowsAutomationRequest(action, processId, automationId, name, value);
    }

    public string RequestId { get; }

    public WindowsAutomationRequest Request { get; }
}

public sealed record WindowsAutomationCompletedWebEvent(
    string RequestId,
    WindowsAutomationAction Action,
    int ProcessId,
    string Target) : WebEvent;

public sealed record WindowsAutomationCancelledWebEvent(string RequestId) : WebEvent;

public sealed record WindowsAutomationErrorWebEvent(
    string RequestId,
    string Reason) : WebEvent;

public sealed class WindowsAutomationCoordinator : IDisposable
{
    private readonly IWindowsAutomationExecutor _executor;
    private readonly Func<CancellationToken, Task<bool>> _isEnabled;
    private readonly Func<WindowsAutomationApprovalRequest, CancellationToken, Task<bool>>
        _requestApproval;
    private readonly Func<WebEvent, Task> _publish;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public WindowsAutomationCoordinator(
        IWindowsAutomationExecutor executor,
        Func<CancellationToken, Task<bool>> isEnabled,
        Func<WindowsAutomationApprovalRequest, CancellationToken, Task<bool>> requestApproval,
        Func<WebEvent, Task> publish)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _requestApproval = requestApproval ?? throw new ArgumentNullException(nameof(requestApproval));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
    }

    public bool TryHandle(
        WebCommand command,
        CancellationToken cancellationToken,
        out Task handling)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command)
        {
            case WindowsAutomationWebCommand automation:
                handling = ExecuteAsync(automation, cancellationToken);
                return true;
            default:
                handling = Task.CompletedTask;
                return false;
        }
    }

    public async Task ExecuteAsync(
        WindowsAutomationWebCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _isEnabled(cancellationToken).ConfigureAwait(false))
        {
            await _publish(new WindowsAutomationErrorWebEvent(command.RequestId, "disabled"))
                .ConfigureAwait(false);
            return;
        }
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            await _publish(new WindowsAutomationErrorWebEvent(command.RequestId, "busy"))
                .ConfigureAwait(false);
            return;
        }

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            bool approved;
            try
            {
                approved = await _requestApproval(
                        ApprovalRequest(command.Request),
                        linkedCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                approved = false;
            }
            if (!approved)
            {
                await _publish(new WindowsAutomationCancelledWebEvent(command.RequestId))
                    .ConfigureAwait(false);
                return;
            }

            bool stillEnabled;
            try
            {
                stillEnabled = await _isEnabled(linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                await _publish(new WindowsAutomationCancelledWebEvent(command.RequestId))
                    .ConfigureAwait(false);
                return;
            }
            if (!stillEnabled)
            {
                await _publish(new WindowsAutomationErrorWebEvent(command.RequestId, "disabled"))
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                var result = await _executor
                    .ExecuteAsync(command.Request, linkedCancellation.Token)
                    .ConfigureAwait(false);
                await _publish(new WindowsAutomationCompletedWebEvent(
                        command.RequestId,
                        result.Action,
                        result.ProcessId,
                        result.Target))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                await _publish(new WindowsAutomationCancelledWebEvent(command.RequestId))
                    .ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                await _publish(new WindowsAutomationErrorWebEvent(command.RequestId, "failed"))
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _lifetime.Cancel();
    }

    private static WindowsAutomationApprovalRequest ApprovalRequest(
        WindowsAutomationRequest request) => new(
            request.Action,
            request.ProcessId,
            request.AutomationId ?? request.Name ?? "window",
            request.Value?.Length ?? 0);

    internal static string ActionName(WindowsAutomationAction action) => action switch
    {
        WindowsAutomationAction.FocusWindow => "focus-window",
        WindowsAutomationAction.Invoke => "invoke",
        WindowsAutomationAction.SetValue => "set-value",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

}
