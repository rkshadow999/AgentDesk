using System.ComponentModel;
using System.Text;
using AgentDesk.Core.Engine;
using AgentDesk.Core.Execution;
using AgentDesk.Engine.Acp;

namespace AgentDesk.Engine.Sidecar;

public sealed class SidecarProcessHost : IAsyncDisposable
{
    private readonly SidecarCommandBuilder _commandBuilder;
    private readonly ISidecarProcessFactory _processFactory;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _standardErrorLock = new();
    private readonly StringBuilder _capturedStandardError = new();

    private ISidecarProcess? _process;
    private AcpEngineClient? _client;
    private Task? _clientDisposeTask;
    private Task? _processDisposeTask;
    private Task? _standardErrorTask;
    private CancellationTokenSource? _standardErrorShutdown;
    private TimeSpan _stopTimeout;
    private bool _started;
    private bool _stopped;
    private bool _stopping;
    private bool _disposed;
    private int _exitRaised;

    public SidecarProcessHost()
        : this(
            new SidecarCommandBuilder(new WslPathConverter()),
            new SystemSidecarProcessFactory())
    {
    }

    public SidecarProcessHost(
        SidecarCommandBuilder commandBuilder,
        ISidecarProcessFactory processFactory)
    {
        _commandBuilder = commandBuilder;
        _processFactory = processFactory;
    }

    public event EventHandler<SidecarExitedEventArgs>? Exited;

    public IEngineClient? Client => _client;

    public string? EngineWorkspacePath { get; private set; }

    public string CapturedStandardError
    {
        get
        {
            lock (_standardErrorLock)
            {
                return _capturedStandardError.ToString();
            }
        }
    }

    public async Task<IEngineClient> StartAsync(
        SidecarLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The sidecar host has already been started.");
            }

            using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startTimeout.CancelAfter(options.StartTimeout);

            SidecarProcessStartInfo? startInfo = null;
            ISidecarProcess process;
            try
            {
                startInfo = await _commandBuilder
                    .BuildAsync(options, startTimeout.Token)
                    .ConfigureAwait(false);
                process = await _processFactory
                    .StartAsync(startInfo, startTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SidecarStartException(
                    SidecarStartFailure.StartTimedOut,
                    $"The sidecar did not start within {options.StartTimeout}.",
                    exception);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Win32Exception exception)
                when (options.ExecutionProfile is ExecutionProfile.WslStrict &&
                      exception.NativeErrorCode == 2)
            {
                throw new SidecarStartException(
                    SidecarStartFailure.WslUnavailable,
                    "Windows Subsystem for Linux is not installed or wsl.exe is unavailable.",
                    exception);
            }
            catch (SidecarStartException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new SidecarStartException(
                    SidecarStartFailure.ProcessStartFailed,
                    $"The sidecar process could not be started for {options.ExecutionProfile}.",
                    exception);
            }
            finally
            {
                startInfo?.ForgetApiKey();
            }

            if (process.HasExited)
            {
                var exitCode = process.ExitCode;
                await process.DisposeAsync().ConfigureAwait(false);
                throw new SidecarStartException(
                    SidecarStartFailure.ProcessExitedDuringStart,
                    $"The sidecar exited during startup with code {exitCode}.");
            }

            _stopTimeout = options.StopTimeout;
            _process = process;
            EngineWorkspacePath = startInfo!.EngineWorkspacePath;
            _standardErrorShutdown = new CancellationTokenSource();
            _standardErrorTask = DrainStandardErrorAsync(
                process.StandardError,
                options.CaptureStandardError,
                options.StandardErrorCharacterLimit,
                options.StandardErrorObserver,
                _standardErrorShutdown.Token);
            _client = new AcpEngineClient(
                process.StandardOutput,
                process.StandardInput,
                options.ApiKey);
            _started = true;
            process.Exited += OnProcessExited;

            if (process.HasExited)
            {
                OnProcessExited(process, EventArgs.Empty);
            }

            return _client;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (!_started || _stopped)
        {
            return;
        }

        _stopping = true;
        var process = _process!;
        Exception? cleanupError = null;
        var clientDisposeTimedOut = false;

        _clientDisposeTask ??= _client is null
            ? Task.CompletedTask
            : _client.DisposeAsync().AsTask();

        try
        {
            await _clientDisposeTask.WaitAsync(_stopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            clientDisposeTimedOut = true;
        }
        catch (Exception exception)
        {
            cleanupError = exception;
        }

        if (!await WaitForExitWithinAsync(process, _stopTimeout).ConfigureAwait(false))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }
            catch (Exception exception)
            {
                cleanupError ??= exception;
            }
        }

        if (!await WaitForExitWithinAsync(process, _stopTimeout).ConfigureAwait(false))
        {
            throw new TimeoutException(
                $"The sidecar process did not exit within {_stopTimeout} after forced termination.",
                cleanupError);
        }

        process.Exited -= OnProcessExited;
        var standardErrorDrainError = await CompleteStandardErrorDrainAsync().ConfigureAwait(false);
        cleanupError ??= standardErrorDrainError;

        var processDisposeTimedOut = false;
        Exception? processDisposeError = null;
        try
        {
            _processDisposeTask ??= process.DisposeAsync().AsTask();
            await _processDisposeTask.WaitAsync(_stopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            processDisposeTimedOut = true;
        }
        catch (Exception exception)
        {
            _processDisposeTask = null;
            processDisposeError = exception;
        }

        if (clientDisposeTimedOut)
        {
            try
            {
                await _clientDisposeTask.WaitAsync(_stopTimeout).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupError ??= exception is TimeoutException
                    ? new TimeoutException(
                        "The ACP client did not stop after the sidecar process exited.",
                        exception)
                    : exception;
            }
        }

        if (processDisposeTimedOut)
        {
            throw new TimeoutException(
                "The sidecar process handle did not finish disposing within the stop timeout.",
                cleanupError);
        }

        if (processDisposeError is not null)
        {
            throw processDisposeError;
        }

        _standardErrorShutdown?.Dispose();
        _standardErrorShutdown = null;
        _standardErrorTask = null;
        _clientDisposeTask = null;
        _processDisposeTask = null;
        _client = null;
        _process = null;
        _stopped = true;

        if (cleanupError is not null)
        {
            throw cleanupError;
        }
    }

    private static async Task<bool> WaitForExitWithinAsync(
        ISidecarProcess process,
        TimeSpan timeout)
    {
        if (process.HasExited)
        {
            return true;
        }

        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        return process.HasExited;
    }

    private async Task<Exception?> CompleteStandardErrorDrainAsync()
    {
        if (_standardErrorTask is null)
        {
            return null;
        }

        try
        {
            await _standardErrorTask.WaitAsync(_stopTimeout).ConfigureAwait(false);
            return null;
        }
        catch (TimeoutException exception)
        {
            _standardErrorShutdown?.Cancel();
            try
            {
                await _standardErrorTask.WaitAsync(_stopTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception cancellationException)
            {
                return new TimeoutException(
                    "The sidecar standard-error drain did not stop after cancellation.",
                    cancellationException);
            }

            return new TimeoutException(
                "The sidecar standard-error stream did not reach EOF after process exit.",
                exception);
        }
        catch (OperationCanceledException) when (
            _standardErrorShutdown?.IsCancellationRequested == true)
        {
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private static void ValidateOptions(SidecarLaunchOptions options)
    {
        if (options.StartTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.StartTimeout,
                "The start timeout must be positive.");
        }

        if (options.StopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.StopTimeout,
                "The stop timeout must be positive.");
        }

        if (options.StandardErrorCharacterLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.StandardErrorCharacterLimit,
                "The standard error capture limit must be positive.");
        }
    }

    private async Task DrainStandardErrorAsync(
        Stream standardError,
        bool capture,
        int characterLimit,
        Action<ReadOnlyMemory<char>>? observer,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            standardError,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        var buffer = new char[1024];

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await reader
                .ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            observer?.Invoke(buffer.AsMemory(0, read));

            if (!capture)
            {
                continue;
            }

            lock (_standardErrorLock)
            {
                _capturedStandardError.Append(buffer, 0, read);
                if (_capturedStandardError.Length > characterLimit)
                {
                    _capturedStandardError.Remove(
                        0,
                        _capturedStandardError.Length - characterLimit);
                }
            }
        }
    }

    private void OnProcessExited(object? sender, EventArgs args)
    {
        var process = _process;
        if (process is null || Interlocked.Exchange(ref _exitRaised, 1) != 0)
        {
            return;
        }

        var exitCode = process.ExitCode;
        var eventArgs = new SidecarExitedEventArgs(exitCode, _stopping);
        var handlers = Exited;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<SidecarExitedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception)
            {
                // A UI callback must not disrupt process-exit cleanup or other subscribers.
            }
        }
    }
}
