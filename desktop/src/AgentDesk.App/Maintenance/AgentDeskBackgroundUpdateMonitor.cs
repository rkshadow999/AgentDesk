using AgentDesk.App.Bridge;

namespace AgentDesk.App.Maintenance;

public sealed record AgentDeskBackgroundUpdateMonitorOptions
{
    public static readonly TimeSpan MaximumInitialDelay = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MinimumCheckInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumCheckInterval = TimeSpan.FromHours(24);

    public AgentDeskBackgroundUpdateMonitorOptions(
        AgentDeskPackageMode packageMode,
        TimeSpan initialDelay,
        TimeSpan checkInterval)
    {
        if (!Enum.IsDefined(packageMode))
        {
            throw new ArgumentOutOfRangeException(nameof(packageMode));
        }
        if (initialDelay < TimeSpan.Zero || initialDelay > MaximumInitialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
        }
        if (checkInterval < MinimumCheckInterval || checkInterval > MaximumCheckInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(checkInterval));
        }

        PackageMode = packageMode;
        InitialDelay = initialDelay;
        CheckInterval = checkInterval;
    }

    public AgentDeskPackageMode PackageMode { get; }

    public TimeSpan InitialDelay { get; }

    public TimeSpan CheckInterval { get; }

    public static AgentDeskBackgroundUpdateMonitorOptions CreateDefault(
        AgentDeskPackageMode packageMode) =>
        new(packageMode, TimeSpan.FromSeconds(30), TimeSpan.FromHours(6));
}

public interface IAgentDeskBackgroundUpdateCheck
{
    Task CheckAsync(CancellationToken cancellationToken = default);
}

public sealed class AgentDeskBackgroundUpdateCheckCoordinator
    : IAgentDeskBackgroundUpdateCheck, IDisposable
{
    private readonly IAgentDeskUpdateChecker _updates;
    private readonly Func<WebEvent, Task> _publish;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastPublishedVersion;
    private bool _disposed;

    public AgentDeskBackgroundUpdateCheckCoordinator(
        IAgentDeskUpdateChecker updates,
        Func<WebEvent, Task> publish)
    {
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
    }

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            AgentDeskUpdateAvailability? available;
            try
            {
                available = await _updates.CheckAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return;
            }

            var version = available?.Version.ToString();
            if (version is null || string.Equals(
                    version,
                    _lastPublishedVersion,
                    StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                await _publish(new BackgroundUpdateAvailableWebEvent(version))
                    .ConfigureAwait(false);
                _lastPublishedVersion = version;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Background failures stay local; manual update checks remain available.
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _gate.Dispose();
    }
}

public sealed class AgentDeskBackgroundUpdateMonitor : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly IAgentDeskBackgroundUpdateCheck _check;
    private readonly AgentDeskBackgroundUpdateMonitorOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task? _runTask;
    private Task? _stopTask;
    private bool _started;
    private bool _disposed;

    public AgentDeskBackgroundUpdateMonitor(
        IAgentDeskBackgroundUpdateCheck check,
        AgentDeskBackgroundUpdateMonitorOptions options,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public bool Start(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_options.PackageMode is AgentDeskPackageMode.Msix || _started)
            {
                return false;
            }

            _started = true;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runTask = RunAsync(_lifetime.Token);
            return true;
        }
    }

    public async ValueTask SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            if (enabled)
            {
                _ = Start(cancellationToken);
                return;
            }

            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public ValueTask StopAsync()
    {
        lock (_sync)
        {
            if (_runTask is null)
            {
                return ValueTask.CompletedTask;
            }

            try
            {
                _lifetime!.Cancel();
            }
            catch (AggregateException)
            {
                // The monitor still observes cancellation if an external callback fails.
            }
            _stopTask ??= AwaitStoppedAsync(_runTask, _lifetime!);
            return new ValueTask(_stopTask);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
        lock (_sync)
        {
            _lifetime?.Dispose();
            _lifetime = null;
            if (_check is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _delayAsync(_options.InitialDelay, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                try
                {
                    await _check.CheckAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // A background check must not disable the explicit update action.
                }

                await _delayAsync(_options.CheckInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Unexpected scheduler failures terminate this optional monitor silently.
        }
    }

    private async Task AwaitStoppedAsync(
        Task runTask,
        CancellationTokenSource lifetime)
    {
        try
        {
            await runTask.ConfigureAwait(false);
        }
        finally
        {
            var reset = false;
            lock (_sync)
            {
                if (ReferenceEquals(_runTask, runTask))
                {
                    _runTask = null;
                    _stopTask = null;
                    _lifetime = null;
                    _started = false;
                    reset = true;
                }
            }
            if (reset)
            {
                lifetime.Dispose();
            }
        }
    }
}
