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
        var stage = "configuration";
        var apiKey = string.Empty;
        await using var host = new SidecarProcessHost();

        try
        {
            var settings = SmokeSettings.Load();
            apiKey = RequiredEnvironmentVariable("GROK_THIRD_PARTY_API_KEY");
            Environment.SetEnvironmentVariable("GROK_THIRD_PARTY_API_KEY", null);

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
                ApiKey = apiKey,
                ProviderProfile = profile,
                StartTimeout = TimeSpan.FromSeconds(20),
                StopTimeout = TimeSpan.FromSeconds(15),
                CaptureStandardError = true,
                StandardErrorCharacterLimit = 64 * 1024,
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
            await host.StopAsync();
            var standardErrorContainsCredential = host.CapturedStandardError.Contains(
                apiKey,
                StringComparison.Ordinal);
            if (standardErrorContainsCredential)
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
                credentialInDiagnostics = false,
            }));
            return 0;
        }
        catch (Exception exception)
        {
            var credentialInDiagnostics = !string.IsNullOrEmpty(apiKey) &&
                host.CapturedStandardError.Contains(apiKey, StringComparison.Ordinal);
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                success = false,
                stage,
                failureCode = FailureCode(stage, exception),
                errorType = exception.GetType().Name,
                credentialInDiagnostics,
            }));
            return 1;
        }
        finally
        {
            apiKey = string.Empty;
        }
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
