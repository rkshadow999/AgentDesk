using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using AgentDesk.Core.Engine;
using AgentDesk.Core.Execution;
using AgentDesk.Engine.Acp;
using AgentDesk.Engine.Sidecar;

namespace AgentDesk.Engine.Tests;

public sealed class SidecarProcessHostTests
{
    [Fact]
    public async Task StartAsync_ConnectsProcessPipesToAcpEngineClient()
    {
        await using var fixture = new HostFixture();

        var client = await fixture.Host.StartAsync(fixture.Options);
        var initializeTask = client.InitializeAsync(fixture.Timeout.Token);

        using var credential = await fixture.Process.ReadClientMessageAsync(fixture.Timeout.Token);
        Assert.Equal(
            "_agentdesk/v1/credential",
            credential.RootElement.GetProperty("method").GetString());
        Assert.Equal(
            "xai-secret",
            credential.RootElement.GetProperty("params").GetProperty("apiKey").GetString());
        await fixture.Process.WriteServerMessageAsync(
            """
            {"jsonrpc":"2.0","id":1,"result":{"credentialAccepted":true,"authMethodId":"xai.api_key"}}
            """,
            fixture.Timeout.Token);

        using var initialize = await fixture.Process.ReadClientMessageAsync(fixture.Timeout.Token);
        Assert.Equal("initialize", initialize.RootElement.GetProperty("method").GetString());
        await fixture.Process.WriteServerMessageAsync(
            """
            {"jsonrpc":"2.0","id":2,"result":{"protocolVersion":1,"agentCapabilities":{},"authMethods":[]}}
            """,
            fixture.Timeout.Token);
        using var extension = await fixture.Process.ReadClientMessageAsync(fixture.Timeout.Token);
        Assert.Equal("_agentdesk/v1/initialize", extension.RootElement.GetProperty("method").GetString());
        await fixture.Process.WriteServerMessageAsync(
            """
            {"jsonrpc":"2.0","id":3,"error":{"code":-32601,"message":"Method not found"}}
            """,
            fixture.Timeout.Token);

        var capabilities = await initializeTask;

        Assert.IsType<AcpEngineClient>(client);
        Assert.Equal(1, capabilities.ProtocolVersion);
        Assert.Equal(fixture.Options.WorkspacePath, fixture.Host.EngineWorkspacePath);
        Assert.Same(fixture.Process, fixture.Factory.StartedProcess);
    }

    [Fact]
    public async Task StartAsync_TimesOutWithStructuredError()
    {
        await using var fixture = new HostFixture();
        fixture.Factory.StartHandler = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        };
        var options = fixture.Options with { StartTimeout = TimeSpan.FromMilliseconds(20) };

        var exception = await Assert.ThrowsAsync<SidecarStartException>(() =>
            fixture.Host.StartAsync(options));

        Assert.Equal(SidecarStartFailure.StartTimedOut, exception.Failure);
    }

    [Fact]
    public async Task StartAsync_CallerCancellationPropagates()
    {
        await using var fixture = new HostFixture();
        using var callerCancellation = new CancellationTokenSource();
        fixture.Factory.StartHandler = async (_, cancellationToken) =>
        {
            callerCancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Host.StartAsync(fixture.Options, callerCancellation.Token));
    }

    [Fact]
    public async Task StartAsync_WslExecutableMissingReturnsStructuredError()
    {
        await using var fixture = new HostFixture(ExecutionProfile.WslStrict);
        fixture.Factory.StartHandler = (_, _) => Task.FromException<ISidecarProcess>(
            new System.ComponentModel.Win32Exception(2, "The system cannot find wsl.exe"));

        var exception = await Assert.ThrowsAsync<SidecarStartException>(() =>
            fixture.Host.StartAsync(fixture.Options));

        Assert.Equal(SidecarStartFailure.WslUnavailable, exception.Failure);
    }

    [Fact]
    public async Task UnexpectedProcessExitRaisesSingleCrashEvent()
    {
        await using var fixture = new HostFixture();
        var exited = new TaskCompletionSource<SidecarExitedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Host.Exited += (_, args) => exited.TrySetResult(args);
        _ = await fixture.Host.StartAsync(fixture.Options);

        fixture.Process.Exit(17);
        var args = await exited.Task.WaitAsync(fixture.Timeout.Token);

        Assert.Equal(17, args.ExitCode);
        Assert.False(args.WasExpected);
    }

    [Fact]
    public async Task ExitSubscriberFailureDoesNotSuppressOtherSubscribers()
    {
        await using var fixture = new HostFixture();
        var observed = new TaskCompletionSource<SidecarExitedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Host.Exited += (_, _) => throw new InvalidOperationException("UI callback failed");
        fixture.Host.Exited += (_, args) => observed.TrySetResult(args);
        _ = await fixture.Host.StartAsync(fixture.Options);

        fixture.Process.Exit(23);
        var args = await observed.Task.WaitAsync(fixture.Timeout.Token);

        Assert.Equal(23, args.ExitCode);
    }

    [Fact]
    public async Task StopAndDisposeAreIdempotentAndKillTheEntireProcessTreeAfterGracePeriod()
    {
        await using var fixture = new HostFixture();
        _ = await fixture.Host.StartAsync(
            fixture.Options with { StopTimeout = TimeSpan.FromMilliseconds(10) });

        await fixture.Host.StopAsync();
        await fixture.Host.StopAsync();
        await fixture.Host.DisposeAsync();
        await fixture.Host.DisposeAsync();

        Assert.Equal([true], fixture.Process.KillEntireProcessTreeCalls);
        Assert.Equal(1, fixture.Process.DisposeCalls);
    }

    [Fact]
    public async Task StopAsync_WhenForcedTerminationDoesNotExit_KeepsOwnershipForRetry()
    {
        await using var fixture = new HostFixture();
        fixture.Process.ExitOnKill = false;
        _ = await fixture.Host.StartAsync(
            fixture.Options with { StopTimeout = TimeSpan.FromMilliseconds(10) });

        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Host.StopAsync());

        Assert.False(fixture.Process.HasExited);
        Assert.Equal(0, fixture.Process.DisposeCalls);

        fixture.Process.ExitOnKill = true;
        await fixture.Host.StopAsync();

        Assert.True(fixture.Process.HasExited);
        Assert.Equal(1, fixture.Process.DisposeCalls);
    }

    [Fact]
    public async Task StopAsync_WhenClientDisposeIgnoresCancellation_RemainsBoundedAndReapsProcess()
    {
        await using var fixture = new HostFixture(blockClientOutput: true);
        _ = await fixture.Host.StartAsync(
            fixture.Options with { StopTimeout = TimeSpan.FromMilliseconds(20) });

        var stopTask = fixture.Host.StopAsync();
        try
        {
            var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromMilliseconds(500)));

            Assert.Same(stopTask, completed);
            await stopTask;
            Assert.Equal([true], fixture.Process.KillEntireProcessTreeCalls);
            Assert.Equal(1, fixture.Process.DisposeCalls);
        }
        finally
        {
            fixture.Process.ReleaseBlockedOutput();
            try
            {
                await stopTask;
            }
            catch (Exception)
            {
            }
        }
    }

    [Fact]
    public async Task StopAsync_WhenProcessDisposeDoesNotComplete_RemainsBoundedAndCanRetry()
    {
        await using var fixture = new HostFixture();
        fixture.Process.BlockDispose = true;
        _ = await fixture.Host.StartAsync(
            fixture.Options with { StopTimeout = TimeSpan.FromMilliseconds(20) });
        fixture.Process.Exit(0);

        var stopTask = fixture.Host.StopAsync();
        try
        {
            var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromMilliseconds(500)));

            Assert.Same(stopTask, completed);
            _ = await Assert.ThrowsAsync<TimeoutException>(() => stopTask);
            Assert.Equal(1, fixture.Process.DisposeCalls);

            fixture.Process.ReleaseDispose();
            await fixture.Host.StopAsync();

            Assert.Equal(1, fixture.Process.DisposeCalls);
        }
        finally
        {
            fixture.Process.ReleaseDispose();
            try
            {
                await stopTask;
            }
            catch (Exception)
            {
            }
        }
    }

    [Fact]
    public async Task StandardErrorIsDrainedButNotRecordedByDefault()
    {
        const string sensitiveText = "user prompt and file body must not be retained";
        await using var fixture = new HostFixture(standardError: sensitiveText);

        _ = await fixture.Host.StartAsync(fixture.Options);
        await fixture.Process.StandardErrorRead.Task.WaitAsync(fixture.Timeout.Token);

        Assert.Equal(string.Empty, fixture.Host.CapturedStandardError);
    }

    [Fact]
    public async Task ExplicitStandardErrorCaptureRetainsOnlyTheConfiguredTail()
    {
        await using var fixture = new HostFixture(standardError: "0123456789abcdef");

        _ = await fixture.Host.StartAsync(fixture.Options with
        {
            CaptureStandardError = true,
            StandardErrorCharacterLimit = 8,
        });
        await fixture.Process.StandardErrorRead.Task.WaitAsync(fixture.Timeout.Token);

        Assert.Equal("89abcdef", fixture.Host.CapturedStandardError);
    }

    [Fact]
    public async Task StandardErrorObserverSeesEarlyOutputBeyondTheCaptureTail()
    {
        const string earlyMarker = "provider-secret-early";
        var standardError = earlyMarker + new string('x', 70 * 1024);
        await using var fixture = new HostFixture(standardError: standardError);
        var observed = false;
        var options = fixture.Options with
        {
            CaptureStandardError = true,
            StandardErrorCharacterLimit = 8,
            StopTimeout = TimeSpan.FromSeconds(1),
            StandardErrorObserver = chunk =>
            {
                if (chunk.Span.IndexOf(earlyMarker.AsSpan(), StringComparison.Ordinal) >= 0)
                {
                    observed = true;
                }
            },
        };

        _ = await fixture.Host.StartAsync(options);
        await fixture.Process.StandardErrorRead.Task.WaitAsync(fixture.Timeout.Token);
        await fixture.Host.StopAsync();

        Assert.True(observed);
        Assert.DoesNotContain(earlyMarker, fixture.Host.CapturedStandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StandardErrorObserverRunsWithoutRetainingDiagnosticBody()
    {
        const string marker = "provider-secret-discarded";
        await using var fixture = new HostFixture(standardError: marker);
        var observed = false;

        _ = await fixture.Host.StartAsync(fixture.Options with
        {
            StopTimeout = TimeSpan.FromSeconds(1),
            CaptureStandardError = false,
            StandardErrorObserver = chunk =>
                observed |= chunk.Span.IndexOf(marker.AsSpan(), StringComparison.Ordinal) >= 0,
        });
        await fixture.Process.StandardErrorRead.Task.WaitAsync(fixture.Timeout.Token);
        await fixture.Host.StopAsync();

        Assert.True(observed);
        Assert.Equal(string.Empty, fixture.Host.CapturedStandardError);
    }

    [Fact]
    public async Task StopAsyncDrainsStandardErrorWrittenAsTheProcessExits()
    {
        const string exitMarker = "provider-secret-at-exit";
        await using var fixture = new HostFixture(standardErrorAtExit: exitMarker);
        var observed = false;
        var options = fixture.Options with
        {
            StopTimeout = TimeSpan.FromSeconds(1),
            StandardErrorObserver = chunk =>
            {
                if (chunk.Span.IndexOf(exitMarker.AsSpan(), StringComparison.Ordinal) >= 0)
                {
                    observed = true;
                }
            },
        };

        _ = await fixture.Host.StartAsync(options);
        fixture.Process.Exit(0);
        await fixture.Host.StopAsync();

        Assert.True(observed);
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly string _root;

        public HostFixture(
            ExecutionProfile profile = ExecutionProfile.NativeProtected,
            string standardError = "",
            bool blockClientOutput = false,
            string standardErrorAtExit = "")
        {
            _root = Path.Combine(Path.GetTempPath(), $"agentdesk-host-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            var enginePath = Path.Combine(_root, "agentdesk-engine.exe");
            File.WriteAllBytes(enginePath, []);

            Process = new FakeSidecarProcess(
                standardError,
                blockClientOutput,
                standardErrorAtExit);
            Factory = new FakeSidecarProcessFactory(Process);
            var converter = new FakeWslPathConverter("/mnt/c/agentdesk-host");
            Host = new SidecarProcessHost(
                new SidecarCommandBuilder(
                    converter,
                    new FixedWslDistributionResolver("Ubuntu"),
                    new FixedWslEngineInstallationVerifier(isCurrent: true)),
                Factory);
            Options = new SidecarLaunchOptions(_root, profile)
            {
                EnginePath = profile is ExecutionProfile.NativeProtected
                    ? enginePath
                    : "/opt/agentdesk/agentdesk-engine",
                ApiKey = "xai-secret",
                StartTimeout = TimeSpan.FromSeconds(1),
                StopTimeout = TimeSpan.FromMilliseconds(20),
            };
        }

        public SidecarProcessHost Host { get; }

        public FakeSidecarProcess Process { get; }

        public FakeSidecarProcessFactory Factory { get; }

        public SidecarLaunchOptions Options { get; }

        public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(5));

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Timeout.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeSidecarProcessFactory(FakeSidecarProcess defaultProcess)
        : ISidecarProcessFactory
    {
        public Func<SidecarProcessStartInfo, CancellationToken, Task<ISidecarProcess>>? StartHandler
        {
            get;
            set;
        }

        public ISidecarProcess? StartedProcess { get; private set; }

        public SidecarProcessStartInfo? StartInfo { get; private set; }

        public async Task<ISidecarProcess> StartAsync(
            SidecarProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            StartInfo = startInfo;
            StartedProcess = StartHandler is null
                ? defaultProcess
                : await StartHandler(startInfo, cancellationToken);
            return StartedProcess;
        }
    }

    private sealed class FakeSidecarProcess : ISidecarProcess
    {
        private readonly Pipe _serverToClient = new();
        private readonly Pipe _clientToServer = new();
        private readonly StreamReader _clientMessages;
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Pipe _standardErrorPipe;
        private readonly TrackingReadStream _standardError;
        private readonly string _standardErrorAtExit;
        private readonly Stream _standardOutput;
        private readonly BlockingReadStream? _blockingStandardOutput;
        private readonly TaskCompletionSource _disposeRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _exitCode;

        public FakeSidecarProcess(
            string standardError,
            bool blockClientOutput = false,
            string standardErrorAtExit = "")
        {
            _standardErrorPipe = new Pipe(new PipeOptions(
                pauseWriterThreshold: 1024 * 1024,
                resumeWriterThreshold: 512 * 1024));
            _clientMessages = new StreamReader(
                _clientToServer.Reader.AsStream(),
                Encoding.UTF8,
                leaveOpen: true);
            _standardError = new TrackingReadStream(_standardErrorPipe.Reader.AsStream());
            _standardErrorAtExit = standardErrorAtExit;
            if (standardError.Length > 0)
            {
                _standardErrorPipe.Writer
                    .WriteAsync(Encoding.UTF8.GetBytes(standardError))
                    .GetAwaiter()
                    .GetResult();
            }
            if (standardErrorAtExit.Length == 0)
            {
                _standardErrorPipe.Writer.Complete();
            }
            _blockingStandardOutput = blockClientOutput ? new BlockingReadStream() : null;
            _standardOutput = _blockingStandardOutput ?? _serverToClient.Reader.AsStream();
        }

        public event EventHandler? Exited;

        public Stream StandardInput => _clientToServer.Writer.AsStream();

        public Stream StandardOutput => _standardOutput;

        public Stream StandardError => _standardError;

        public bool HasExited { get; private set; }

        public int ExitCode => HasExited
            ? _exitCode
            : throw new InvalidOperationException("Process has not exited.");

        public TaskCompletionSource StandardErrorRead => _standardError.ReadStarted;

        public List<bool> KillEntireProcessTreeCalls { get; } = [];

        public int DisposeCalls { get; private set; }

        public bool ExitOnKill { get; set; } = true;

        public bool BlockDispose { get; set; }

        public async Task<JsonDocument> ReadClientMessageAsync(CancellationToken cancellationToken)
        {
            var line = await _clientMessages.ReadLineAsync(cancellationToken);
            Assert.NotNull(line);
            return JsonDocument.Parse(line);
        }

        public async Task WriteServerMessageAsync(string json, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            await _serverToClient.Writer.WriteAsync(bytes, cancellationToken);
        }

        public void Exit(int exitCode)
        {
            if (HasExited)
            {
                return;
            }

            HasExited = true;
            _exitCode = exitCode;
            if (_standardErrorAtExit.Length > 0)
            {
                _standardErrorPipe.Writer
                    .WriteAsync(Encoding.UTF8.GetBytes(_standardErrorAtExit))
                    .GetAwaiter()
                    .GetResult();
            }
            _standardErrorPipe.Writer.Complete();
            _serverToClient.Writer.Complete();
            _exit.TrySetResult();
            Exited?.Invoke(this, EventArgs.Empty);
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _exit.Task.WaitAsync(cancellationToken);

        public void Kill(bool entireProcessTree)
        {
            KillEntireProcessTreeCalls.Add(entireProcessTree);
            if (ExitOnKill)
            {
                Exit(-1);
            }
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (BlockDispose)
            {
                await _disposeRelease.Task;
            }
            _clientMessages.Dispose();
            _standardOutput.Dispose();
            _standardError.Dispose();
        }

        public void ReleaseBlockedOutput() => _blockingStandardOutput?.Release();

        public void ReleaseDispose() => _disposeRelease.TrySetResult();
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource<int> _release = new(
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

        public override int Read(byte[] buffer, int offset, int count) =>
            _release.Task.GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(_release.Task);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            _release.Task;

        public void Release() => _release.TrySetResult(0);

        protected override void Dispose(bool disposing)
        {
            Release();
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class TrackingReadStream(Stream inner) : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadStarted.TrySetResult();
            return inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            ReadStarted.TrySetResult();
            return inner.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWslPathConverter(string convertedPath) : IWslPathConverter
    {
        public Task<string> ConvertAsync(
            string windowsPath,
            string distributionName,
            CancellationToken cancellationToken) =>
            Task.FromResult(convertedPath);
    }

    private sealed class FixedWslDistributionResolver(string? distributionName)
        : IWslDistributionResolver
    {
        public string? Resolve(string wslExecutablePath) => distributionName;
    }

    private sealed class FixedWslEngineInstallationVerifier(bool isCurrent)
        : IWslEngineInstallationVerifier
    {
        public bool IsCurrent(
            string wslExecutablePath,
            string distributionName,
            string bundledEnginePath) =>
            isCurrent;
    }
}
