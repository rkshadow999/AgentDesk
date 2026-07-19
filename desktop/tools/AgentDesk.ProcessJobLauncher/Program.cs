using AgentDesk.Engine.Sidecar;

return await ProcessJobLauncher.RunOwnedAsync(
    args,
    Console.OpenStandardInput(),
    CancellationToken.None);

internal static class ProcessJobLauncher
{
    private const int CancelledExitCode = 130;
    private const int FailureExitCode = 1;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ISidecarProcess? process = null;
        Task? outputDrain = null;
        Task? errorDrain = null;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "The process Job Object launcher requires Windows.");
            }

            var options = LauncherOptions.Parse(arguments);
            var startInfo = new SidecarProcessStartInfo(
                options.ExecutablePath,
                options.Arguments,
                options.WorkingDirectory,
                options.WorkingDirectory,
                new Dictionary<string, string?>());
            process = await new SystemSidecarProcessFactory()
                .StartAsync(startInfo, cancellationToken)
                .ConfigureAwait(false);
            outputDrain = DrainAsync(process.StandardOutput);
            errorDrain = DrainAsync(process.StandardError);

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return process.ExitCode;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CancelledExitCode;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CancelledExitCode;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            InvalidOperationException or
            PlatformNotSupportedException or
            System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(
                $"AgentDesk process launcher failed ({exception.GetType().Name}).");
            return FailureExitCode;
        }
        finally
        {
            if (process is not null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }

            await ObserveDrainAsync(outputDrain).ConfigureAwait(false);
            await ObserveDrainAsync(errorDrain).ConfigureAwait(false);
        }
    }

    public static async Task<int> RunOwnedAsync(
        IReadOnlyList<string> arguments,
        Stream ownershipPipe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownershipPipe);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        _ = CancelWhenOwnershipPipeClosesAsync(ownershipPipe, lifetime);
        return await RunAsync(arguments, lifetime.Token).ConfigureAwait(false);
    }

    private static async Task CancelWhenOwnershipPipeClosesAsync(
        Stream ownershipPipe,
        CancellationTokenSource lifetime)
    {
        var buffer = new byte[1];
        try
        {
            while (await ownershipPipe
                       .ReadAsync(buffer, lifetime.Token)
                       .ConfigureAwait(false) > 0)
            {
            }

            TryCancel(lifetime);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException)
        {
            TryCancel(lifetime);
        }
    }

    private static void TryCancel(CancellationTokenSource lifetime)
    {
        try
        {
            lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The target exited before the ownership pipe closed.
        }
    }

    private static async Task DrainAsync(Stream source)
    {
        try
        {
            await source.CopyToAsync(Stream.Null).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException)
        {
            // Closing the Job Object also closes the redirected streams.
        }
    }

    private static async Task ObserveDrainAsync(Task? drain)
    {
        if (drain is null)
        {
            return;
        }

        try
        {
            await drain.ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException)
        {
            // The owning process was intentionally terminated or already exited.
        }
    }

    private sealed record LauncherOptions(
        string WorkingDirectory,
        string ExecutablePath,
        IReadOnlyList<string> Arguments)
    {
        public static LauncherOptions Parse(IReadOnlyList<string> arguments)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            if (arguments.Count < 4 ||
                !string.Equals(
                    arguments[0],
                    "--working-directory",
                    StringComparison.Ordinal) ||
                !string.Equals(arguments[2], "--", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Usage: AgentDesk.ProcessJobLauncher --working-directory <path> -- <executable> [arguments]");
            }

            var workingDirectory = Path.GetFullPath(arguments[1]);
            var executablePath = Path.GetFullPath(arguments[3]);
            if (!Directory.Exists(workingDirectory))
            {
                throw new ArgumentException("The target working directory does not exist.");
            }

            return new LauncherOptions(
                workingDirectory,
                executablePath,
                arguments.Skip(4).ToArray());
        }
    }
}
