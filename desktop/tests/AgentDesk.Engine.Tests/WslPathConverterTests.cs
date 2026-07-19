using System.Text;
using AgentDesk.Engine.Sidecar;

namespace AgentDesk.Engine.Tests;

public sealed class WslPathConverterTests
{
    [Fact]
    public async Task ConvertAsync_WhenCancelled_KillsAndDisposesTheHelperProcess()
    {
        var process = new FakeWslHelperProcess();
        var converter = new WslPathConverter(new FakeProcessFactory(process));
        using var cancellation = new CancellationTokenSource();

        var conversion = converter.ConvertAsync(
            "C:\\workspace",
            "Ubuntu",
            cancellation.Token);
        await process.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conversion);

        Assert.Equal([true], process.KillEntireProcessTreeCalls);
        Assert.Equal(1, process.DisposeCalls);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task ConvertAsync_WhenHelperSucceeds_ReturnsTheTrimmedPath()
    {
        var process = new FakeWslHelperProcess(
            hasExited: true,
            exitCode: 0,
            standardOutput: "  /mnt/c/workspace  \n");
        var converter = new WslPathConverter(new FakeProcessFactory(process));

        var converted = await converter.ConvertAsync(
            "C:\\workspace",
            "Ubuntu",
            CancellationToken.None);

        Assert.Equal("/mnt/c/workspace", converted);
        Assert.Equal(
            ["--distribution", "Ubuntu", "--exec", "wslpath", "-a", "C:\\workspace"],
            process.StartInfo?.Arguments);
        Assert.Empty(process.KillEntireProcessTreeCalls);
        Assert.Equal(1, process.DisposeCalls);
    }

    [Fact]
    public async Task ConvertAsync_WhenHelperSurvivesKill_HandsItToBackgroundReaping()
    {
        var process = new FakeWslHelperProcess { ExitOnKill = false };
        var converter = new WslPathConverter(
            new FakeProcessFactory(process),
            TimeSpan.FromMilliseconds(20));
        using var cancellation = new CancellationTokenSource();

        var conversion = converter.ConvertAsync(
            "C:\\workspace",
            "Ubuntu",
            cancellation.Token);
        await process.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        _ = await Assert.ThrowsAsync<TimeoutException>(() => conversion);
        Assert.False(process.HasExited);
        Assert.Equal(0, process.DisposeCalls);

        process.Exit();
        await process.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, process.DisposeCalls);
    }

    private sealed class FakeProcessFactory(ISidecarProcess process) : ISidecarProcessFactory
    {
        public Task<ISidecarProcess> StartAsync(
            SidecarProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            if (process is FakeWslHelperProcess helper)
            {
                helper.StartInfo = startInfo;
            }

            return Task.FromResult(process);
        }
    }

    private sealed class FakeWslHelperProcess : ISidecarProcess
    {
        private readonly TaskCompletionSource _exit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _exitCode;

        public FakeWslHelperProcess(
            bool hasExited = false,
            int exitCode = -1,
            string standardOutput = "/mnt/c/workspace\n")
        {
            HasExited = hasExited;
            _exitCode = exitCode;
            StandardOutput = new MemoryStream(Encoding.UTF8.GetBytes(standardOutput));
            if (hasExited)
            {
                _exit.TrySetResult();
            }
        }

        public event EventHandler? Exited;

        public Stream StandardInput { get; } = new MemoryStream();

        public Stream StandardOutput { get; }

        public Stream StandardError { get; } = new MemoryStream();

        public bool HasExited { get; private set; }

        public int ExitCode => HasExited ? _exitCode : throw new InvalidOperationException();

        public TaskCompletionSource WaitStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<bool> KillEntireProcessTreeCalls { get; } = [];

        public int DisposeCalls { get; private set; }

        public bool ExitOnKill { get; set; } = true;

        public TaskCompletionSource Disposed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public SidecarProcessStartInfo? StartInfo { get; set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitStarted.TrySetResult();
            return _exit.Task.WaitAsync(cancellationToken);
        }

        public void Kill(bool entireProcessTree)
        {
            KillEntireProcessTreeCalls.Add(entireProcessTree);
            if (ExitOnKill)
            {
                Exit();
            }
        }

        public void Exit()
        {
            if (HasExited)
            {
                return;
            }

            HasExited = true;
            _exit.TrySetResult();
            Exited?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            StandardInput.Dispose();
            StandardOutput.Dispose();
            StandardError.Dispose();
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
