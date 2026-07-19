namespace AgentDesk.App.Windowing;

internal sealed class ContentDialogQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private int _closed;

    public async Task<T> EnqueueAsync<T>(
        Func<CancellationToken, Task<T>> showDialog,
        T shutdownResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(showDialog);
        if (Volatile.Read(ref _closed) != 0)
        {
            return shutdownResult;
        }
        cancellationToken.ThrowIfCancellationRequested();

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        var entered = false;
        try
        {
            await _gate.WaitAsync(linkedCancellation.Token);
            entered = true;
            if (_shutdown.IsCancellationRequested)
            {
                return shutdownResult;
            }

            var result = await showDialog(linkedCancellation.Token);
            if (_shutdown.IsCancellationRequested)
            {
                return shutdownResult;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return shutdownResult;
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _shutdown.Cancel();
        }
    }
}
