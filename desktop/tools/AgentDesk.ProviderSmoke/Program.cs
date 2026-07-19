using System.Diagnostics;
using System.Text.Json;
using AgentDesk.Core.Engine;
using AgentDesk.Core.Execution;
using AgentDesk.Core.Providers;
using AgentDesk.Engine.Sidecar;

return await ProviderSmoke.RunAsync();

internal static class ProviderSmoke
{
    public static async Task<int> RunAsync()
    {
        try
        {
            return await WithTemporaryEngineDataDirectoryAsync(RunCoreAsync);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                success = false,
                stage = "cleanup",
                failureCode = "cleanup_failed",
                errorType = exception.GetType().Name,
                diagnosticScanComplete = false,
                credentialInDiagnostics = false,
            }));
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROK_THIRD_PARTY_API_KEY", null);
        }
    }

    private static async Task<int> RunCoreAsync(string engineDataPath)
    {
        var stage = "configuration";
        var apiKey = string.Empty;
        StreamingSecretDetector? secretDetector = null;
        await using var host = new SidecarProcessHost();

        try
        {
            apiKey = RequiredEnvironmentVariable("GROK_THIRD_PARTY_API_KEY");
            Environment.SetEnvironmentVariable("GROK_THIRD_PARTY_API_KEY", null);
            secretDetector = new StreamingSecretDetector(apiKey);
            var settings = SmokeSettings.Load();

            var profile = new ProviderProfile(
                settings.BaseUrl,
                settings.Model,
                settings.Backend,
                settings.AllowInsecureHttp);
            if (!profile.CanSendCredentials)
            {
                throw new InvalidOperationException("Plain HTTP was not explicitly enabled.");
            }

            var launch = new SidecarLaunchOptions(
                settings.WorkspacePath,
                ExecutionProfile.NativeProtected)
            {
                EnginePath = settings.EnginePath,
                EngineDataPath = engineDataPath,
                ApiKey = apiKey,
                ProviderProfile = profile,
                StartTimeout = TimeSpan.FromSeconds(20),
                StopTimeout = TimeSpan.FromSeconds(15),
                CaptureStandardError = false,
                StandardErrorObserver = secretDetector.Observe,
            };

            stage = "sidecar-start";
            var startWatch = Stopwatch.StartNew();
            var client = await host.StartAsync(launch);
            startWatch.Stop();

            stage = "handshake";
            var handshakeWatch = Stopwatch.StartNew();
            using (var handshakeCancellation = new CancellationTokenSource(
                       TimeSpan.FromSeconds(60)))
            {
                _ = await client.InitializeAsync(handshakeCancellation.Token);
                await client.AuthenticateAsync(handshakeCancellation.Token);
            }
            handshakeWatch.Stop();

            stage = "session";
            SessionId sessionId;
            using (var sessionCancellation = new CancellationTokenSource(
                       TimeSpan.FromSeconds(30)))
            {
                sessionId = await client.NewSessionAsync(
                    settings.WorkspacePath,
                    sessionCancellation.Token);
            }

            var eventCount = 0;
            client.EventReceived += (_, _) => Interlocked.Increment(ref eventCount);

            stage = "streaming-prompt";
            var promptWatch = Stopwatch.StartNew();
            PromptResult promptResult;
            using (var promptCancellation = new CancellationTokenSource(
                       TimeSpan.FromSeconds(settings.PromptTimeoutSeconds)))
            {
                promptResult = await client.PromptAsync(
                    sessionId,
                    "Reply with exactly OK.",
                    promptCancellation.Token);
            }
            promptWatch.Stop();
            if (eventCount == 0)
            {
                throw new InvalidDataException("The provider produced no streamed ACP events.");
            }

            stage = "cancellation";
            var cancellationWatch = Stopwatch.StartNew();
            var cancellationStreamObserved = 0;
            var cancellationStreamStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveCancellationStream(object? _, EngineEvent engineEvent)
            {
                if (engineEvent.SessionId == sessionId &&
                    engineEvent.UpdateKind is "agent_message_chunk" or "agent_thought_chunk")
                {
                    Interlocked.Exchange(ref cancellationStreamObserved, 1);
                    cancellationStreamStarted.TrySetResult();
                }
            }

            client.EventReceived += ObserveCancellationStream;
            PromptResult cancelledResult;
            try
            {
                var longPrompt = client.PromptAsync(
                    sessionId,
                    "Do not use tools. Start immediately and repeat the line STREAMING, " +
                    "one line at a time, until cancelled. Never summarize.");
                cancelledResult = await ObserveCancellationAsync(
                    longPrompt,
                    cancellationStreamStarted.Task,
                    cancellationToken => client.CancelAsync(sessionId, cancellationToken),
                    TimeSpan.FromSeconds(settings.PromptTimeoutSeconds),
                    TimeSpan.FromSeconds(30),
                    CancellationToken.None);
            }
            finally
            {
                client.EventReceived -= ObserveCancellationStream;
            }

            cancellationWatch.Stop();

            stage = "shutdown";
            var diagnostics = await CompleteDiagnosticScanAsync(
                () => host.StopAsync(),
                secretDetector);
            if (!diagnostics.ScanCompleted)
            {
                throw new InvalidDataException(
                    "The sidecar diagnostic stream did not finish draining.");
            }
            if (diagnostics.SecretObserved)
            {
                throw new InvalidDataException("The sidecar diagnostic stream contained a credential.");
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = true,
                backend = settings.Backend.ToString(),
                sidecarStartMs = startWatch.ElapsedMilliseconds,
                handshakeMs = handshakeWatch.ElapsedMilliseconds,
                promptMs = promptWatch.ElapsedMilliseconds,
                promptStopReason = promptResult.StopReason.ToString(),
                streamedEventCount = eventCount,
                cancellationMs = cancellationWatch.ElapsedMilliseconds,
                cancellationStopReason = cancelledResult.StopReason.ToString(),
                cancellationStreamObserved = Volatile.Read(ref cancellationStreamObserved) != 0,
                cancellationObserved = true,
                cleanShutdown = true,
                diagnosticScanComplete = true,
                credentialInDiagnostics = false,
            }));
            return 0;
        }
        catch (Exception exception)
        {
            var diagnostics = await CompleteDiagnosticScanAsync(
                () => host.DisposeAsync().AsTask(),
                secretDetector);
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                success = false,
                stage,
                failureCode = FailureCode(stage, exception),
                errorType = exception.GetType().Name,
                diagnosticScanComplete = diagnostics.ScanCompleted,
                credentialInDiagnostics = diagnostics.SecretObserved,
            }));
            return 1;
        }
        finally
        {
            secretDetector?.Dispose();
            apiKey = string.Empty;
        }
    }

    internal static async Task<int> WithTemporaryEngineDataDirectoryAsync(
        Func<string, Task<int>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var directory = Directory.CreateTempSubdirectory("agentdesk-provider-smoke-");
        try
        {
            return await action(directory.FullName).ConfigureAwait(false);
        }
        finally
        {
            directory.Refresh();
            if (directory.Exists)
            {
                directory.Delete(recursive: true);
            }
        }
    }

    internal static async Task<DiagnosticScanResult> CompleteDiagnosticScanAsync(
        Func<Task> drainAsync,
        StreamingSecretDetector? detector)
    {
        ArgumentNullException.ThrowIfNull(drainAsync);
        var completed = true;
        try
        {
            await drainAsync().ConfigureAwait(false);
        }
        catch
        {
            completed = false;
        }

        return new DiagnosticScanResult(
            completed,
            detector?.SecretObserved ?? false);
    }

    internal static async Task<PromptResult> ObserveCancellationAsync(
        Task<PromptResult> promptTask,
        Task streamStarted,
        Func<CancellationToken, Task> cancelAsync,
        TimeSpan streamStartTimeout,
        TimeSpan promptCompletionTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(promptTask);
        ArgumentNullException.ThrowIfNull(streamStarted);
        ArgumentNullException.ThrowIfNull(cancelAsync);

        Task completed;
        try
        {
            completed = await Task.WhenAny(streamStarted, promptTask)
                .WaitAsync(streamStartTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidDataException(
                "The cancellation prompt produced no streamed model event before timeout.",
                exception);
        }

        if (ReferenceEquals(completed, promptTask))
        {
            var earlyResult = await promptTask.ConfigureAwait(false);
            throw new InvalidDataException(
                $"The cancellation prompt completed before cancellation could be exercised " +
                $"(stop reason: {earlyResult.StopReason}).");
        }

        await cancelAsync(cancellationToken).ConfigureAwait(false);

        PromptResult result;
        try
        {
            result = await promptTask
                .WaitAsync(promptCompletionTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidDataException(
                "The cancellation prompt did not finish after cancellation was requested.",
                exception);
        }

        if (result.StopReason is not EngineStopReason.Cancelled)
        {
            throw new InvalidDataException("The real prompt did not acknowledge cancellation.");
        }

        return result;
    }

    internal static async Task<PromptResult> CancelPromptAsync(
        Task<PromptResult> promptTask,
        Func<CancellationToken, Task> cancelAsync,
        TimeSpan promptCompletionTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(promptTask);
        ArgumentNullException.ThrowIfNull(cancelAsync);

        await cancelAsync(cancellationToken).ConfigureAwait(false);

        PromptResult result;
        try
        {
            result = await promptTask
                .WaitAsync(promptCompletionTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidDataException(
                "The cancellation prompt did not finish after cancellation was requested.",
                exception);
        }

        if (result.StopReason is not EngineStopReason.Cancelled)
        {
            throw new InvalidDataException("The real prompt did not acknowledge cancellation.");
        }

        return result;
    }

    internal static string FailureCode(string stage, Exception exception)
    {
        if (string.Equals(stage, "cancellation", StringComparison.Ordinal) &&
            exception is InvalidDataException)
        {
            if (exception.Message.Contains(
                    "completed before cancellation",
                    StringComparison.Ordinal))
            {
                return "cancellation_completed_early";
            }

            if (exception.Message.Contains(
                    "did not finish after cancellation",
                    StringComparison.Ordinal))
            {
                return "cancellation_completion_timeout";
            }

            if (exception.Message.Contains(
                    "did not acknowledge cancellation",
                    StringComparison.Ordinal))
            {
                return "cancellation_not_acknowledged";
            }

            if (exception.Message.Contains(
                    "no streamed model event",
                    StringComparison.Ordinal))
            {
                return "cancellation_stream_timeout";
            }
        }

        return $"{stage}_failed";
    }

    private static string RequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Required environment variable {name} is missing.");
        }

        return value;
    }

    internal sealed record DiagnosticScanResult(
        bool ScanCompleted,
        bool SecretObserved);

    private sealed record SmokeSettings(
        string BaseUrl,
        string Model,
        ProviderBackend Backend,
        bool AllowInsecureHttp,
        string EnginePath,
        string WorkspacePath,
        int PromptTimeoutSeconds)
    {
        public static SmokeSettings Load()
        {
            var backend = RequiredEnvironmentVariable("AGENTDESK_REAL_PROVIDER_BACKEND") switch
            {
                "chat_completions" => ProviderBackend.ChatCompletions,
                "responses" => ProviderBackend.Responses,
                _ => throw new InvalidOperationException(
                    "AGENTDESK_REAL_PROVIDER_BACKEND is invalid."),
            };
            var enginePath = Path.GetFullPath(
                RequiredEnvironmentVariable("AGENTDESK_REAL_PROVIDER_ENGINE"));
            var workspacePath = Path.GetFullPath(
                RequiredEnvironmentVariable("AGENTDESK_REAL_PROVIDER_WORKSPACE"));
            if (!File.Exists(enginePath) || !Directory.Exists(workspacePath))
            {
                throw new InvalidOperationException("The engine or workspace path is unavailable.");
            }

            var timeout = 240;
            var configuredTimeout = Environment.GetEnvironmentVariable(
                "AGENTDESK_REAL_PROVIDER_PROMPT_TIMEOUT_SECONDS");
            if (configuredTimeout is not null &&
                (!int.TryParse(configuredTimeout, out timeout) || timeout is < 30 or > 600))
            {
                throw new InvalidOperationException("The provider prompt timeout is invalid.");
            }

            return new SmokeSettings(
                RequiredEnvironmentVariable("AGENTDESK_REAL_PROVIDER_BASE_URL"),
                RequiredEnvironmentVariable("AGENTDESK_REAL_PROVIDER_MODEL"),
                backend,
                string.Equals(
                    Environment.GetEnvironmentVariable(
                        "AGENTDESK_REAL_PROVIDER_ALLOW_INSECURE_HTTP"),
                    "1",
                    StringComparison.Ordinal),
                enginePath,
                workspacePath,
                timeout);
        }
    }
}

internal sealed class StreamingSecretDetector : IDisposable
{
    private readonly object _gate = new();
    private string _secret;
    private int[] _prefixTable;
    private int _matchedCharacters;
    private int _secretObserved;

    public StreamingSecretDetector(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        _secret = secret;
        _prefixTable = BuildPrefixTable(secret);
    }

    public bool SecretObserved => Volatile.Read(ref _secretObserved) != 0;

    public int RetainedDiagnosticCharacterCount => 0;

    public void Observe(ReadOnlyMemory<char> chunk)
    {
        if (SecretObserved || chunk.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (SecretObserved || _secret.Length == 0)
            {
                return;
            }

            foreach (var value in chunk.Span)
            {
                while (_matchedCharacters > 0 && value != _secret[_matchedCharacters])
                {
                    _matchedCharacters = _prefixTable[_matchedCharacters - 1];
                }

                if (value == _secret[_matchedCharacters])
                {
                    _matchedCharacters++;
                }

                if (_matchedCharacters == _secret.Length)
                {
                    Volatile.Write(ref _secretObserved, 1);
                    _matchedCharacters = _prefixTable[_matchedCharacters - 1];
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _secret = string.Empty;
            Array.Clear(_prefixTable);
            _prefixTable = [];
            _matchedCharacters = 0;
        }
    }

    private static int[] BuildPrefixTable(string value)
    {
        var table = new int[value.Length];
        var matched = 0;
        for (var index = 1; index < value.Length; index++)
        {
            while (matched > 0 && value[index] != value[matched])
            {
                matched = table[matched - 1];
            }

            if (value[index] == value[matched])
            {
                matched++;
            }

            table[index] = matched;
        }

        return table;
    }
}
