using System.ComponentModel;

namespace AgentDesk.Engine.Sidecar;

public sealed class WslPathConverter : IWslPathConverter
{
    private readonly ISidecarProcessFactory _processFactory;
    private readonly TimeSpan _helperStopTimeout;

    public WslPathConverter()
        : this(new SystemSidecarProcessFactory(), TimeSpan.FromSeconds(2))
    {
    }

    public WslPathConverter(ISidecarProcessFactory processFactory)
        : this(processFactory, TimeSpan.FromSeconds(2))
    {
    }

    public WslPathConverter(
        ISidecarProcessFactory processFactory,
        TimeSpan helperStopTimeout)
    {
        ArgumentNullException.ThrowIfNull(processFactory);
        if (helperStopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(helperStopTimeout),
                helperStopTimeout,
                "The WSL helper stop timeout must be positive.");
        }

        _processFactory = processFactory;
        _helperStopTimeout = helperStopTimeout;
    }

    public async Task<string> ConvertAsync(
        string windowsPath,
        string distributionName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(distributionName);

        ISidecarProcess process;
        try
        {
            process = await _processFactory.StartAsync(
                    new SidecarProcessStartInfo(
                        "wsl.exe",
                        [
                            "--distribution",
                            distributionName,
                            "--exec",
                            "wslpath",
                            "-a",
                            windowsPath,
                        ],
                        Environment.CurrentDirectory,
                        windowsPath,
                        new Dictionary<string, string?>()),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2)
        {
            throw new SidecarStartException(
                SidecarStartFailure.WslUnavailable,
                "Windows Subsystem for Linux is not installed or wsl.exe is unavailable.",
                exception);
        }

        var disposeProcess = true;
        try
        {
            using var outputReader = new StreamReader(process.StandardOutput, leaveOpen: true);
            using var errorReader = new StreamReader(process.StandardError, leaveOpen: true);
            var standardOutput = outputReader.ReadToEndAsync(cancellationToken);
            var standardError = errorReader.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ = ObserveReadAsync(standardOutput);
                _ = ObserveReadAsync(standardError);
                try
                {
                    await TerminateHelperAsync(process, _helperStopTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    disposeProcess = false;
                    _ = ReapHelperInBackgroundAsync(process, _helperStopTimeout);
                    throw;
                }
                throw;
            }

            var convertedPath = (await standardOutput.ConfigureAwait(false)).Trim();
            var errorText = (await standardError.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(convertedPath))
            {
                var detail = string.IsNullOrWhiteSpace(errorText)
                    ? "wslpath did not return a path."
                    : errorText;
                throw new SidecarStartException(
                    SidecarStartFailure.WorkspacePathConversionFailed,
                    $"The workspace path could not be converted for WSL: {detail}");
            }

            return convertedPath;
        }
        finally
        {
            if (disposeProcess)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task TerminateHelperAsync(
        ISidecarProcess process,
        TimeSpan timeout)
    {
        if (process.HasExited)
        {
            return;
        }

        Exception? killError = null;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
        catch (Exception exception)
        {
            killError = exception;
        }

        if (process.HasExited)
        {
            return;
        }

        using var forcedStop = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(forcedStop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (forcedStop.IsCancellationRequested)
        {
        }

        if (!process.HasExited)
        {
            throw new TimeoutException(
                $"The WSL path helper did not exit within {timeout} after forced termination.",
                killError);
        }
    }

    private static async Task ReapHelperInBackgroundAsync(
        ISidecarProcess process,
        TimeSpan retryTimeout)
    {
        try
        {
            while (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }
                catch (Exception)
                {
                }

                if (process.HasExited)
                {
                    break;
                }

                using var wait = new CancellationTokenSource(retryTimeout);
                try
                {
                    await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (wait.IsCancellationRequested)
                {
                }
            }

            try
            {
                await process.DisposeAsync().AsTask().WaitAsync(retryTimeout).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        catch (Exception)
        {
        }
    }

    private static async Task ObserveReadAsync(Task<string> readTask)
    {
        try
        {
            _ = await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        catch (Exception)
        {
        }
    }
}
