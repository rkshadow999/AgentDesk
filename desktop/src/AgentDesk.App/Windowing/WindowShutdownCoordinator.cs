namespace AgentDesk.App.Windowing;

public sealed record WindowShutdownResult(bool Succeeded, Exception? Error)
{
    public static WindowShutdownResult Success { get; } = new(true, null);

    public static WindowShutdownResult Failure(Exception error) => new(false, error);
}

public sealed class WindowShutdownCoordinator
{
    private readonly object _gate = new();
    private readonly Func<ValueTask> _cleanupAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;
    private Task<WindowShutdownResult>? _inFlight;
    private WindowShutdownResult? _lastResult;
    private ShutdownState _state;

    public WindowShutdownCoordinator(
        Func<ValueTask> cleanupAsync,
        int maxAttempts,
        TimeSpan retryDelay,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(cleanupAsync);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        if (retryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        }

        _cleanupAsync = cleanupAsync;
        _maxAttempts = maxAttempts;
        _retryDelay = retryDelay;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public bool TryAuthorizeClose()
    {
        lock (_gate)
        {
            if (_state != ShutdownState.ReadyToClose)
            {
                return false;
            }

            _state = ShutdownState.CloseAuthorized;
            return true;
        }
    }

    public bool TryConsumeCloseAuthorization()
    {
        lock (_gate)
        {
            if (_state != ShutdownState.CloseAuthorized)
            {
                return false;
            }

            _state = ShutdownState.Closed;
            return true;
        }
    }

    public bool ShouldReportFailure(WindowShutdownResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            return _state == ShutdownState.Active
                && ReferenceEquals(_lastResult, result)
                && !result.Succeeded;
        }
    }

    public Task<WindowShutdownResult> RequestShutdownAsync()
    {
        TaskCompletionSource<WindowShutdownResult> completion;
        lock (_gate)
        {
            if (_state != ShutdownState.Active)
            {
                return _inFlight!;
            }

            completion = new TaskCompletionSource<WindowShutdownResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = completion.Task;
            _lastResult = null;
            _state = ShutdownState.Cleaning;
        }

        _ = RunShutdownAsync(completion);
        return completion.Task;
    }

    private async Task RunShutdownAsync(
        TaskCompletionSource<WindowShutdownResult> completion)
    {
        WindowShutdownResult result;
        try
        {
            result = await CleanupWithRetriesAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = WindowShutdownResult.Failure(exception);
        }

        lock (_gate)
        {
            _lastResult = result;
            _state = result.Succeeded
                ? ShutdownState.ReadyToClose
                : ShutdownState.Active;
            completion.TrySetResult(result);
        }
    }

    private async Task<WindowShutdownResult> CleanupWithRetriesAsync()
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                await _cleanupAsync().ConfigureAwait(false);
                return WindowShutdownResult.Success;
            }
            catch (Exception exception)
            {
                lastError = exception;
            }

            if (attempt < _maxAttempts)
            {
                await _delayAsync(_retryDelay, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return WindowShutdownResult.Failure(lastError!);
    }

    private enum ShutdownState
    {
        Active,
        Cleaning,
        ReadyToClose,
        CloseAuthorized,
        Closed,
    }
}
