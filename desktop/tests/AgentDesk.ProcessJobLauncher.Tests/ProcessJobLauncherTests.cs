using System.Diagnostics;

namespace AgentDesk.ProcessJobLauncher.Tests;

public sealed class ProcessJobLauncherTests
{
    [Fact]
    public async Task OwnershipPipeClosureClosesJobAndTerminatesDetachedGrandchild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-job-launcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        var processIdPath = Path.Combine(testRoot, "grandchild.pid");
        var escapedProcessIdPath = processIdPath.Replace("'", "''", StringComparison.Ordinal);
        var powershellPath = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var pingPath = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var escapedPingPath = pingPath.Replace("'", "''", StringComparison.Ordinal);
        var script =
            $"$child = Start-Process -FilePath '{escapedPingPath}' " +
            "-ArgumentList '127.0.0.1','-n','60' " +
            "-WindowStyle Hidden -PassThru; " +
            $"[System.IO.File]::WriteAllText('{escapedProcessIdPath}', $child.Id.ToString()); " +
            "Start-Sleep -Seconds 60";
        var ownershipPipe = new OwnershipPipeStream();
        Process? grandchild = null;

        try
        {
            var runTask = global::ProcessJobLauncher.RunOwnedAsync(
                [
                    "--working-directory",
                    testRoot,
                    "--",
                    powershellPath,
                    "-NoLogo",
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    script,
                ],
                ownershipPipe,
                CancellationToken.None);

            Assert.True(
                await WaitForFileAsync(processIdPath, TimeSpan.FromSeconds(10)),
                "The target process did not publish its detached grandchild PID.");
            var processId = int.Parse(await File.ReadAllTextAsync(processIdPath));
            grandchild = Process.GetProcessById(processId);
            Assert.False(grandchild.HasExited);

            ownershipPipe.CloseOwner();

            Assert.Equal(130, await runTask.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(
                await WaitForExitAsync(grandchild, TimeSpan.FromSeconds(10)),
                "Closing the launcher must close its kill-on-close Job Object and terminate descendants.");
        }
        finally
        {
            ownershipPipe.CloseOwner();
            if (grandchild is { HasExited: false })
            {
                grandchild.Kill(entireProcessTree: true);
                await grandchild.WaitForExitAsync();
            }

            grandchild?.Dispose();
            ownershipPipe.Dispose();
            await DeleteDirectoryWithRetryAsync(testRoot, TimeSpan.FromSeconds(5));
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
            }

            await Task.Delay(25);
        }
    }

    private static async Task<bool> WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return true;
            }

            await Task.Delay(25);
        }

        return File.Exists(path);
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    private sealed class OwnershipPipeStream : Stream
    {
        private readonly TaskCompletionSource _closed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void CloseOwner() => _closed.TrySetResult();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await _closed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
