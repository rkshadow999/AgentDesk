using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentDesk.Engine.Sidecar;

namespace AgentDesk.Engine.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SystemSidecarProcessFactoryTests
{
    [Fact]
    public async Task StartAsync_ConfiguresAndAssignsAKillOnCloseJobBeforeReturning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var jobApi = new RecordingWindowsJobObjectApi();
        var factory = new SystemSidecarProcessFactory(jobApi);
        var startInfo = CreateSleepingProcessStartInfo();
        var sidecar = await factory.StartAsync(startInfo, CancellationToken.None);

        try
        {
            Assert.Equal(["create", "configure-kill-on-close", "assign"], jobApi.Calls);
        }
        finally
        {
            if (!sidecar.HasExited)
            {
                sidecar.Kill(entireProcessTree: true);
                await sidecar.WaitForExitAsync(CancellationToken.None);
            }

            await sidecar.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_DoesNotRunChildCodeBeforeJobAssignment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var markerPath = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-suspended-start-{Guid.NewGuid():N}.txt");
        var jobApi = new RecordingWindowsJobObjectApi
        {
            BeforeAssignment = () =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(5));
                Assert.False(
                    File.Exists(markerPath),
                    "The child must remain suspended until Job Object assignment succeeds.");
            },
        };
        var factory = new SystemSidecarProcessFactory(jobApi);
        var startInfo = new SidecarProcessStartInfo(
            "powershell.exe",
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                $"[System.IO.File]::WriteAllText('{markerPath.Replace("'", "''")}', 'started'); " +
                    "Start-Sleep -Seconds 30",
            ],
            Environment.CurrentDirectory,
            Environment.CurrentDirectory,
            new Dictionary<string, string?>());

        ISidecarProcess? sidecar = null;
        try
        {
            sidecar = await factory.StartAsync(startInfo, CancellationToken.None);
            Assert.True(
                await WaitForFileAsync(markerPath, TimeSpan.FromSeconds(15)),
                "The child must resume after successful Job Object assignment.");
        }
        finally
        {
            if (sidecar is not null)
            {
                if (!sidecar.HasExited)
                {
                    sidecar.Kill(entireProcessTree: true);
                    await sidecar.WaitForExitAsync(CancellationToken.None);
                }

                await sidecar.DisposeAsync();
            }

            File.Delete(markerPath);
        }
    }

    [Fact]
    public async Task StartAsync_DoesNotResolveBareExecutableFromChildWorkingDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-executable-resolution-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(workingDirectory, "cmd.exe"),
            "This file must never be executed.");

        try
        {
            var startInfo = new SidecarProcessStartInfo(
                "cmd.exe",
                ["/d", "/c", "echo safe"],
                workingDirectory,
                workingDirectory,
                new Dictionary<string, string?>());
            var factory = new SystemSidecarProcessFactory();
            await using var sidecar = await factory.StartAsync(
                startInfo,
                CancellationToken.None);
            using var reader = new StreamReader(sidecar.StandardOutput);

            var output = await reader.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await sidecar.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, sidecar.ExitCode);
            Assert.Equal("safe", output.Trim());
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public void BuildCommandLine_PreservesWindowsArgumentBoundaries()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = @"C:\Program Files\AgentDesk\sidecar.exe",
        };
        startInfo.ArgumentList.Add(string.Empty);
        startInfo.ArgumentList.Add("plain");
        startInfo.ArgumentList.Add("contains space");
        startInfo.ArgumentList.Add("say \"hello\"");
        startInfo.ArgumentList.Add(@"C:\path with space\");

        var commandLine = WindowsSuspendedProcessLauncher.BuildCommandLine(startInfo);

        Assert.Equal(
            "\"C:\\Program Files\\AgentDesk\\sidecar.exe\" \"\" plain " +
                "\"contains space\" \"say \\\"hello\\\"\" " +
                "\"C:\\path with space\\\\\"",
            commandLine);
    }

    [Fact]
    public async Task StartAsync_WhenJobAssignmentFails_TerminatesChildWithoutRunningIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var markerPath = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-failed-assignment-{Guid.NewGuid():N}.txt");
        var assignmentFailure = new InvalidOperationException("assignment failed");
        var jobApi = new RecordingWindowsJobObjectApi
        {
            AssignmentFailure = assignmentFailure,
        };
        var factory = new SystemSidecarProcessFactory(jobApi);
        var startInfo = new SidecarProcessStartInfo(
            "powershell.exe",
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                $"[System.IO.File]::WriteAllText('{markerPath.Replace("'", "''")}', 'started'); " +
                    "Start-Sleep -Seconds 30",
            ],
            Environment.CurrentDirectory,
            Environment.CurrentDirectory,
            new Dictionary<string, string?>());

        try
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => factory.StartAsync(startInfo, CancellationToken.None));

            Assert.Same(assignmentFailure, thrown);
            Assert.True(jobApi.JobObject.IsClosed);
            Assert.NotNull(jobApi.AssignedProcessId);
            Assert.True(
                await WaitForExitAsync(jobApi.AssignedProcessId.Value, TimeSpan.FromSeconds(5)),
                "A sidecar that could not be assigned to its Job Object must not remain running.");
            Assert.False(
                File.Exists(markerPath),
                "A sidecar must not run user code when Job Object assignment fails.");
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    [Fact]
    public async Task StartAsync_RedirectsStandardInputOutputAndError()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var startInfo = new SidecarProcessStartInfo(
            commandInterpreter,
            [
                "/d",
                "/v:on",
                "/c",
                "set /p input= & echo out:!input! & echo err:!input! 1>&2",
            ],
            Environment.CurrentDirectory,
            Environment.CurrentDirectory,
            new Dictionary<string, string?>());
        var factory = new SystemSidecarProcessFactory();
        await using var sidecar = await factory.StartAsync(startInfo, CancellationToken.None);
        using var outputReader = new StreamReader(sidecar.StandardOutput);
        using var errorReader = new StreamReader(sidecar.StandardError);
        await using var inputWriter = new StreamWriter(sidecar.StandardInput)
        {
            AutoFlush = true,
        };

        await inputWriter.WriteLineAsync("hello");
        var outputTask = outputReader.ReadToEndAsync();
        var errorTask = errorReader.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sidecar.WaitForExitAsync(timeout.Token);

        Assert.Equal(0, sidecar.ExitCode);
        Assert.Equal("out:hello", (await outputTask).Trim());
        Assert.Equal("err:hello", (await errorTask).Trim());
    }

    [Fact]
    public async Task DisposeAsync_TerminatesTheRunningChildThroughAKillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var startInfo = CreateSleepingProcessStartInfo("Write-Output $PID; ");
        var factory = new SystemSidecarProcessFactory();
        var sidecar = await factory.StartAsync(startInfo, CancellationToken.None);

        using var reader = new StreamReader(sidecar.StandardOutput);
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var processIdText = await reader.ReadLineAsync(readTimeout.Token);
        var processId = Assert.IsType<int>(
            int.TryParse(processIdText, out var parsedProcessId) ? parsedProcessId : null);
        using var child = Process.GetProcessById(processId);

        try
        {
            await sidecar.DisposeAsync();

            var exited = await WaitForExitAsync(child, TimeSpan.FromSeconds(5));

            Assert.True(
                exited,
                "Closing the sidecar wrapper must close its kill-on-close Job Object.");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task DisposeAsync_WaitsForTheRootProcessAfterClosingTheJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var jobClosed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var jobApi = new RecordingWindowsJobObjectApi();
        jobApi.JobObject.Released = () => jobClosed.TrySetResult();
        var factory = new SystemSidecarProcessFactory(jobApi);
        var sidecar = await factory.StartAsync(
            CreateSleepingProcessStartInfo(),
            CancellationToken.None);
        using var child = Process.GetProcessById(jobApi.AssignedProcessId!.Value);

        try
        {
            var disposeTask = sidecar.DisposeAsync().AsTask();
            await jobClosed.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.False(
                disposeTask.IsCompleted,
                "Disposal must not return while the root process is still exiting after Job closure.");

            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task DisposeAsync_StopsWaitingWhenTheRootProcessDoesNotExit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var jobApi = new RecordingWindowsJobObjectApi();
        var factory = new SystemSidecarProcessFactory(jobApi);
        var sidecar = await factory.StartAsync(
            CreateSleepingProcessStartInfo(),
            CancellationToken.None);
        using var child = Process.GetProcessById(jobApi.AssignedProcessId!.Value);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await sidecar.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));

            Assert.InRange(
                stopwatch.Elapsed,
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(8));
            Assert.False(
                child.HasExited,
                "The fake Job deliberately leaves the root process running to exercise the timeout.");
        }
        finally
        {
            stopwatch.Stop();
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task StartAsync_RemovesInheritedCredentialVariablesFromTheChild()
    {
        const string genericSecretName = "AGENTDESK_TEST_API_KEY";
        const string grokAuthName = "GROK_AUTH";
        var previousGenericSecret = Environment.GetEnvironmentVariable(genericSecretName);
        var previousGrokAuth = Environment.GetEnvironmentVariable(grokAuthName);
        Environment.SetEnvironmentVariable(genericSecretName, "must-not-leak");
        Environment.SetEnvironmentVariable(grokAuthName, "must-not-leak-either");

        try
        {
            var output = await RunCommandAsync(
                $"if defined {genericSecretName} (echo leaked) else " +
                $"(if defined {grokAuthName} (echo leaked) else (echo clean))");

            Assert.Equal("clean", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable(genericSecretName, previousGenericSecret);
            Environment.SetEnvironmentVariable(grokAuthName, previousGrokAuth);
        }
    }

    [Fact]
    public async Task StartAsync_RemovesAnExplicitDesktopApiKeyOverride()
    {
        var output = await RunCommandAsync(
            "if defined XAI_API_KEY (echo leaked) else (echo clean)",
            new Dictionary<string, string?>
            {
                ["XAI_API_KEY"] = "desktop-managed-key",
            });

        Assert.Equal("clean", output);
    }

    [Fact]
    public async Task StartAsync_RemovesInheritedRustAndSamplingLogConfiguration()
    {
        const string rustLogName = "RUST_LOG";
        const string samplingLogName = "GROK_LOG_SAMPLING";
        var previousRustLog = Environment.GetEnvironmentVariable(rustLogName);
        var previousSamplingLog = Environment.GetEnvironmentVariable(samplingLogName);
        Environment.SetEnvironmentVariable(rustLogName, "sampling_log=info");
        Environment.SetEnvironmentVariable(samplingLogName, "1");

        try
        {
            var output = await RunCommandAsync(
                $"if defined {rustLogName} (echo leaked) else " +
                $"(if defined {samplingLogName} (echo leaked) else (echo clean))");

            Assert.Equal("clean", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable(rustLogName, previousRustLog);
            Environment.SetEnvironmentVariable(samplingLogName, previousSamplingLog);
        }
    }

    private static async Task<string> RunCommandAsync(
        string command,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var startInfo = new SidecarProcessStartInfo(
            commandInterpreter,
            ["/d", "/c", command],
            Environment.CurrentDirectory,
            Environment.CurrentDirectory,
            environment ?? new Dictionary<string, string?>());
        var factory = new SystemSidecarProcessFactory();
        await using var process = await factory.StartAsync(startInfo, CancellationToken.None);
        using var reader = new StreamReader(process.StandardOutput);
        var output = await reader.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, process.ExitCode);
        return output.Trim();
    }

    private static SidecarProcessStartInfo CreateSleepingProcessStartInfo(
        string commandPrefix = "") =>
        new(
            "powershell.exe",
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                $"{commandPrefix}Start-Sleep -Seconds 30",
            ],
            Environment.CurrentDirectory,
            Environment.CurrentDirectory,
            new Dictionary<string, string?>());

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

    private static async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return await WaitForExitAsync(process, timeout);
        }
        catch (ArgumentException)
        {
            return true;
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

    private sealed class RecordingWindowsJobObjectApi : IWindowsJobObjectApi
    {
        public List<string> Calls { get; } = [];

        public FakeSafeHandle JobObject { get; } = new();

        public Exception? AssignmentFailure { get; init; }

        public Action? BeforeAssignment { get; init; }

        public int? AssignedProcessId { get; private set; }

        public SafeHandle CreateJobObject()
        {
            Calls.Add("create");
            return JobObject;
        }

        public void ConfigureKillOnClose(SafeHandle jobObject)
        {
            Assert.Same(JobObject, jobObject);
            Calls.Add("configure-kill-on-close");
        }

        public void AssignProcess(
            SafeHandle jobObject,
            SafeHandle processHandle,
            int processId)
        {
            Assert.Same(JobObject, jobObject);
            Assert.False(processHandle.IsInvalid);
            using var process = Process.GetProcessById(processId);
            Assert.False(process.HasExited);
            BeforeAssignment?.Invoke();
            AssignedProcessId = processId;
            Calls.Add("assign");
            if (AssignmentFailure is not null)
            {
                throw AssignmentFailure;
            }
        }
    }

    private sealed class FakeSafeHandle : SafeHandle
    {
        public FakeSafeHandle()
            : base(IntPtr.Zero, ownsHandle: true)
        {
            SetHandle(new IntPtr(1));
        }

        public override bool IsInvalid => false;

        public Action? Released { get; set; }

        protected override bool ReleaseHandle()
        {
            Released?.Invoke();
            return true;
        }
    }
}
