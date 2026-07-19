namespace AgentDesk.App.Notifications;

public sealed class WindowsNotificationActivationCoordinator : IDisposable
{
    private readonly IWindowsNotificationActivationSource _source;
    private readonly Func<CancellationToken, Task> _activateWindowAsync;
    private readonly Func<string, CancellationToken, Task<bool>> _openSessionAsync;
    private readonly Action<Exception>? _reportFailure;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly SemaphoreSlim _activationGate = new(1, 1);
    private bool _started;
    private bool _disposed;

    public WindowsNotificationActivationCoordinator(
        IWindowsNotificationActivationSource source,
        Func<CancellationToken, Task> activateWindowAsync,
        Func<string, CancellationToken, Task<bool>> openSessionAsync,
        Action<Exception>? reportFailure = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _activateWindowAsync = activateWindowAsync ??
            throw new ArgumentNullException(nameof(activateWindowAsync));
        _openSessionAsync = openSessionAsync ??
            throw new ArgumentNullException(nameof(openSessionAsync));
        _reportFailure = reportFailure;
        _shutdownToken = _shutdown.Token;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _source.NotificationInvoked += Source_NotificationInvoked;
        _started = true;
        try
        {
            _source.Initialize();
        }
        catch (Exception exception)
        {
            _reportFailure?.Invoke(exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            _source.NotificationInvoked -= Source_NotificationInvoked;
        }
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private async void Source_NotificationInvoked(
        object? sender,
        WindowsNotificationInvokedEventArgs eventArgs)
    {
        var cancellationToken = _shutdownToken;
        try
        {
            await _activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _activateWindowAsync(cancellationToken).ConfigureAwait(false);
                _ = await _openSessionAsync(eventArgs.SessionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _activationGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _reportFailure?.Invoke(exception);
        }
    }
}
