using AgentDesk.Core.Engine;
using System.Text.Json;

namespace AgentDesk.ProviderSmoke.Tests;

public sealed class ProviderSmokeCancellationProbeTests
{
    [Fact]
    public async Task ConfigurationFailureOutputIsBoundedAndDoesNotEchoInputs()
    {
        using var directory = new TemporaryDirectory();
        var enginePath = Path.Combine(directory.Path, "sentinel-engine-path.exe");
        File.WriteAllBytes(enginePath, []);
        var variables = new Dictionary<string, string?>
        {
            ["GROK_THIRD_PARTY_API_KEY"] = "fake-provider-secret-never-log",
            ["AGENTDESK_REAL_PROVIDER_BASE_URL"] = "http://sentinel-provider.invalid/v1",
            ["AGENTDESK_REAL_PROVIDER_MODEL"] = "sentinel-model-body",
            ["AGENTDESK_REAL_PROVIDER_BACKEND"] = "chat_completions",
            ["AGENTDESK_REAL_PROVIDER_ALLOW_INSECURE_HTTP"] = "0",
            ["AGENTDESK_REAL_PROVIDER_ENGINE"] = enginePath,
            ["AGENTDESK_REAL_PROVIDER_WORKSPACE"] = directory.Path,
        };
        var previous = variables.Keys.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable);
        var originalError = Console.Error;
        using var error = new StringWriter();

        try
        {
            foreach (var (name, value) in variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            Console.SetError(error);

            var exitCode = await global::ProviderSmoke.RunAsync();
            var output = error.ToString().Trim();

            Assert.Equal(1, exitCode);
            Assert.InRange(output.Length, 1, 512);
            Assert.DoesNotContain("fake-provider-secret-never-log", output, StringComparison.Ordinal);
            Assert.DoesNotContain("sentinel-provider", output, StringComparison.Ordinal);
            Assert.DoesNotContain("sentinel-model-body", output, StringComparison.Ordinal);
            Assert.DoesNotContain("sentinel-engine-path", output, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(output);
            Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        }
        finally
        {
            Console.SetError(originalError);
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Fact]
    public async Task TemporaryEngineDataDirectoryIsUniqueAndRemovedAfterSuccess()
    {
        string? engineDataPath = null;

        var result = await global::ProviderSmoke.WithTemporaryEngineDataDirectoryAsync(path =>
        {
            engineDataPath = path;
            Directory.CreateDirectory(Path.Combine(path, "sessions"));
            File.WriteAllText(Path.Combine(path, "sessions", "probe.txt"), "temporary");
            return Task.FromResult(17);
        });

        Assert.Equal(17, result);
        Assert.NotNull(engineDataPath);
        Assert.False(Directory.Exists(engineDataPath));
        Assert.False(IsUnderDefaultEngineDataPath(engineDataPath));
    }

    [Fact]
    public async Task TemporaryEngineDataDirectoryIsRemovedAfterFailure()
    {
        string? engineDataPath = null;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            global::ProviderSmoke.WithTemporaryEngineDataDirectoryAsync(path =>
            {
                engineDataPath = path;
                File.WriteAllText(Path.Combine(path, "probe.txt"), "temporary");
                throw new InvalidOperationException("expected failure");
            }));

        Assert.Equal("expected failure", exception.Message);
        Assert.NotNull(engineDataPath);
        Assert.False(Directory.Exists(engineDataPath));
        Assert.False(IsUnderDefaultEngineDataPath(engineDataPath));
    }

    [Fact]
    public void StreamingSecretDetectorFindsSplitSecretAfterMoreThan64KiBWithoutRetainingBody()
    {
        const string secret = "provider-secret-for-test";
        using var detector = new global::StreamingSecretDetector(secret);

        detector.Observe((new string('x', 70 * 1024) + secret[..9]).AsMemory());
        detector.Observe((secret[9..] + new string('y', 4096)).AsMemory());

        Assert.True(detector.SecretObserved);
        Assert.Equal(0, detector.RetainedDiagnosticCharacterCount);
    }

    [Fact]
    public async Task DiagnosticResultReadsTheDetectorOnlyAfterTheDrainCompletes()
    {
        const string secret = "provider-secret-at-exit";
        using var detector = new global::StreamingSecretDetector(secret);
        var drainCompleted = false;
        Func<Task> drain = () =>
        {
            detector.Observe(secret.AsMemory());
            drainCompleted = true;
            return Task.CompletedTask;
        };

        var result = await global::ProviderSmoke.CompleteDiagnosticScanAsync(drain, detector);

        Assert.True(drainCompleted);
        Assert.True(result.ScanCompleted);
        Assert.True(result.SecretObserved);
    }

    [Fact]
    public async Task DiagnosticResultReportsObservedSecretWhenTheDrainFailsAtTheEnd()
    {
        const string secret = "provider-secret-before-drain-failure";
        using var detector = new global::StreamingSecretDetector(secret);

        var result = await global::ProviderSmoke.CompleteDiagnosticScanAsync(
            () =>
            {
                detector.Observe(secret.AsMemory());
                return Task.FromException(new IOException("drain failed"));
            },
            detector);

        Assert.False(result.ScanCompleted);
        Assert.True(result.SecretObserved);
    }

    [Fact]
    public async Task CancelPromptStartsImmediatelyWithoutWaitingForStreaming()
    {
        var promptCompleted = new TaskCompletionSource<PromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelCalled = false;

        var result = await global::ProviderSmoke.CancelPromptAsync(
            promptCompleted.Task,
            _ =>
            {
                cancelCalled = true;
                promptCompleted.TrySetResult(
                    new PromptResult(EngineStopReason.Cancelled, "cancelled"));
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(cancelCalled);
        Assert.Equal(EngineStopReason.Cancelled, result.StopReason);
    }

    [Theory]
    [InlineData(
        "The cancellation prompt completed before cancellation could be exercised (stop reason: EndTurn).",
        "cancellation_completed_early")]
    [InlineData(
        "The cancellation prompt did not finish after cancellation was requested.",
        "cancellation_completion_timeout")]
    [InlineData(
        "The real prompt did not acknowledge cancellation.",
        "cancellation_not_acknowledged")]
    [InlineData(
        "The cancellation prompt produced no streamed model event before timeout.",
        "cancellation_stream_timeout")]
    public void FailureCodeClassifiesCancellationWithoutExposingExceptionText(
        string message,
        string expected)
    {
        var actual = global::ProviderSmoke.FailureCode(
            "cancellation",
            new InvalidDataException(message));

        Assert.Equal(expected, actual);
        Assert.DoesNotContain(message, actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObserveCancellationWaitsForStreamingBeforeCancelling()
    {
        var streamStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var promptCompleted = new TaskCompletionSource<PromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelCalled = false;

        var probe = global::ProviderSmoke.ObserveCancellationAsync(
            promptCompleted.Task,
            streamStarted.Task,
            _ =>
            {
                Assert.True(streamStarted.Task.IsCompletedSuccessfully);
                cancelCalled = true;
                promptCompleted.TrySetResult(
                    new PromptResult(EngineStopReason.Cancelled, "cancelled"));
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        await Task.Yield();
        Assert.False(cancelCalled);
        streamStarted.TrySetResult();

        var result = await probe;

        Assert.True(cancelCalled);
        Assert.Equal(EngineStopReason.Cancelled, result.StopReason);
    }

    [Fact]
    public async Task ObserveCancellationRejectsPromptThatCompletesBeforeStreaming()
    {
        var promptCompleted = Task.FromResult(
            new PromptResult(EngineStopReason.EndTurn, "end_turn"));
        var streamStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelCalled = false;

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            global::ProviderSmoke.ObserveCancellationAsync(
                promptCompleted,
                streamStarted.Task,
                _ =>
                {
                    cancelCalled = true;
                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2),
                CancellationToken.None));

        Assert.Contains("completed before cancellation", exception.Message, StringComparison.Ordinal);
        Assert.False(cancelCalled);
    }

    [Fact]
    public async Task ObserveCancellationRejectsNonCancelledStopReason()
    {
        var promptCompleted = new TaskCompletionSource<PromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            global::ProviderSmoke.ObserveCancellationAsync(
                promptCompleted.Task,
                Task.CompletedTask,
                _ =>
                {
                    promptCompleted.TrySetResult(
                        new PromptResult(EngineStopReason.EndTurn, "end_turn"));
                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2),
                CancellationToken.None));

        Assert.Contains("did not acknowledge cancellation", exception.Message, StringComparison.Ordinal);
    }

    private static bool IsUnderDefaultEngineDataPath(string path)
    {
        var defaultPath = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentDesk",
            "Engine"));
        var candidate = Path.GetFullPath(path);
        return candidate.Equals(defaultPath, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(defaultPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("agentdesk-provider-smoke-test-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
