using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentDesk.Cloud.Client;

namespace AgentDesk.Cloud.Client.Tests;

public sealed class AgentDeskCloudClientTests
{
    private const string AccessToken = "cloud-access-token-that-must-stay-in-the-header";

    [Fact]
    public async Task PutSessionAsync_UploadsOnlyCiphertextAndUsesAuthorizationHeader()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        const string plaintextText = "private prompt and file body";
        var envelope = new AesGcmEnvelopeCodec().Encrypt(
            Encoding.UTF8.GetBytes(plaintextText),
            key,
            new EnvelopeBinding("team-1", "device-1", "session-1", 1));
        var handler = new RecordingHandler(
            request => JsonResponse(
                HttpStatusCode.Created,
                new { sessionId = "session-1", revision = 1 }));
        var client = CreateClient(handler);

        var receipt = await client.PutSessionAsync("session-1", 1, envelope);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Equal("/root/api/v1/sync/sessions/session-1", sent.Uri.AbsolutePath);
        Assert.Equal("Bearer", sent.Authorization?.Scheme);
        Assert.Equal(AccessToken, sent.Authorization?.Parameter);
        Assert.DoesNotContain(AccessToken, sent.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(plaintextText, sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(key), sent.Body, StringComparison.Ordinal);
        using var sentJson = JsonDocument.Parse(sent.Body);
        Assert.Equal(
            envelope.Ciphertext,
            sentJson.RootElement.GetProperty("ciphertext").GetString());
        Assert.Equal("session-1", receipt.SessionId);
        Assert.Equal(1, receipt.Revision);
    }

    [Fact]
    public async Task TokenAndPolicyMethods_UseTypedRoutesAndRedactIssuedToken()
    {
        var handler = new RecordingHandler(
            request => request.RequestUri!.AbsolutePath switch
            {
                "/root/api/v1/tokens" => JsonResponse(
                    HttpStatusCode.Created,
                    new { token = "new-device-secret", subjectId = "device-2", role = "device" }),
                "/root/api/v1/tokens/device-2" =>
                    new HttpResponseMessage(HttpStatusCode.NoContent),
                "/root/api/v1/policy" when request.Method == HttpMethod.Get => JsonResponse(
                    HttpStatusCode.OK,
                    Policy(version: 3)),
                "/root/api/v1/policy" when request.Method == HttpMethod.Put => JsonResponse(
                    HttpStatusCode.OK,
                    Policy(version: 4, remoteRunnerEnabled: false)),
                _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath),
            });
        var client = CreateClient(handler);

        var token = await client.CreateTokenAsync("device-2", CloudTokenRole.Device);
        await client.RevokeTokenAsync("device-2");
        var current = await client.GetPolicyAsync();
        var updated = await client.UpdatePolicyAsync(
            new CloudTeamPolicyUpdate(
                ["WslStrict"],
                remoteRunnerEnabled: false,
                uiAutomationEnabled: false,
                maximumConcurrentJobs: 2,
                allowedPluginPublishers: ["publisher-1"]));

        Assert.Equal("new-device-secret", token.Token);
        Assert.DoesNotContain("new-device-secret", token.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new-device-secret",
            JsonSerializer.Serialize(token),
            StringComparison.Ordinal);
        Assert.Equal(3, current.Version);
        Assert.Equal(4, updated.Version);
        Assert.False(updated.RemoteRunnerEnabled);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Get, handler.Requests[2].Method);
        Assert.Equal(HttpMethod.Put, handler.Requests[3].Method);
    }

    [Fact]
    public async Task SessionAndHandoffMethods_MapEncryptedResponses()
    {
        var envelope = TestEnvelope();
        var handler = new RecordingHandler(
            request => request.RequestUri!.AbsolutePath switch
            {
                "/root/api/v1/sync/sessions/session-2" when request.Method == HttpMethod.Get =>
                    JsonResponse(
                        HttpStatusCode.OK,
                        EnvelopeResponse("session-2", 5, envelope)),
                "/root/api/v1/sync/sessions/session-2" when request.Method == HttpMethod.Delete =>
                    JsonResponse(
                        HttpStatusCode.OK,
                        new { sessionId = "session-2", revision = 6 }),
                "/root/api/v1/handoffs" when request.Method == HttpMethod.Post =>
                    JsonResponse(HttpStatusCode.Created, Handoff(envelope)),
                "/root/api/v1/handoffs" when request.Method == HttpMethod.Get =>
                    JsonResponse(HttpStatusCode.OK, new[] { Handoff(envelope) }),
                "/root/api/v1/handoffs/handoff-1/acknowledge" =>
                    new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath),
            });
        var client = CreateClient(handler);

        var session = await client.GetSessionAsync("session-2");
        var deleted = await client.DeleteSessionAsync("session-2", knownRevision: 5);
        var created = await client.CreateHandoffAsync(
            "handoff-1",
            "device-target",
            "session-2",
            envelope);
        var inbox = await client.ListHandoffsAsync(limit: 25);
        var acknowledged = await client.AcknowledgeHandoffAsync("handoff-1");

        Assert.Equal(5, session!.Revision);
        Assert.Equal(envelope.Ciphertext, session.Envelope.Ciphertext);
        Assert.Equal(6, deleted.Revision);
        Assert.Equal("device-target", created.TargetDeviceId);
        Assert.Single(inbox);
        Assert.True(acknowledged);
        Assert.Equal("?revision=5", handler.Requests[1].Uri.Query);
        Assert.Equal("?limit=25", handler.Requests[3].Uri.Query);
    }

    [Fact]
    public async Task RunnerAndAutomationMethods_UseAllRequiredRoutes()
    {
        const string leaseToken = "adl_client-lease-token-that-must-only-be-sent-on-completion";
        var envelope = TestEnvelope();
        var handler = new RecordingHandler(
            request => request.RequestUri!.AbsolutePath switch
            {
                "/root/api/v1/runners/runner-1/register" => JsonResponse(
                    HttpStatusCode.OK,
                    new { runnerId = "runner-1", status = "online" }),
                "/root/api/v1/jobs" => JsonResponse(
                    HttpStatusCode.Created,
                    new { jobId = "job-1" }),
                "/root/api/v1/runners/runner-1/claim" => JsonResponse(
                    HttpStatusCode.OK,
                    new
                    {
                        jobId = "job-1",
                        kind = "task",
                        requiredCapability = "windows",
                        automationId = (string?)null,
                        runId = (string?)null,
                        algorithm = envelope.Algorithm,
                        nonce = envelope.Nonce,
                        ciphertext = envelope.Ciphertext,
                        leaseExpiresAt = DateTimeOffset.Parse("2026-07-17T12:00:00Z"),
                        leaseToken,
                        leaseGeneration = 7,
                    }),
                "/root/api/v1/jobs/job-1/complete" => JsonResponse(
                    HttpStatusCode.OK,
                    new { jobId = "job-1", status = "completed" }),
                "/root/api/v1/automations" when request.Method == HttpMethod.Post => JsonResponse(
                    HttpStatusCode.Created,
                    Automation()),
                "/root/api/v1/automations" when request.Method == HttpMethod.Get => JsonResponse(
                    HttpStatusCode.OK,
                    new[] { Automation() }),
                "/root/api/v1/automations/automation-1" =>
                    new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath),
            });
        var client = CreateClient(handler);
        var identity = new CloudRunnerJobIdentity(
            "job-1",
            CloudRunnerPayloadKinds.Task,
            "windows");

        await client.RegisterRunnerAsync("runner-1", ["windows", "native"]);
        var queued = await client.QueueJobAsync(identity, envelope);
        var claimed = await client.ClaimJobAsync("runner-1", leaseSeconds: 30);
        await client.CompleteJobAsync(claimed!.Identity, envelope);
        var created = await client.CreateAutomationAsync(
            "automation-1",
            "Nightly review",
            60,
            "windows",
            envelope);
        var automations = await client.ListAutomationsAsync();
        var disabled = await client.DisableAutomationAsync("automation-1");

        Assert.Equal("job-1", queued.JobId);
        Assert.Equal("job-1", claimed.JobId);
        Assert.Equal(envelope.Ciphertext, claimed.Envelope.Ciphertext);
        Assert.DoesNotContain(leaseToken, claimed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(leaseToken, claimed.Identity.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            leaseToken,
            JsonSerializer.Serialize(claimed.Identity),
            StringComparison.Ordinal);
        Assert.Equal("automation-1", created.AutomationId);
        Assert.Single(automations);
        Assert.True(disabled);
        Assert.Equal(7, handler.Requests.Count);
        Assert.Contains("\"jobId\":\"job-1\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"task\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"task-result\"", handler.Requests[3].Body, StringComparison.Ordinal);
        Assert.Contains("\"runnerId\":\"runner-1\"", handler.Requests[3].Body, StringComparison.Ordinal);
        Assert.Contains($"\"leaseToken\":\"{leaseToken}\"", handler.Requests[3].Body, StringComparison.Ordinal);
        Assert.Contains("\"leaseGeneration\":7", handler.Requests[3].Body, StringComparison.Ordinal);
        Assert.Contains(
            "\"automationId\":\"automation-1\"",
            handler.Requests[4].Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClaimRejectsAResponseWithoutALeaseToken()
    {
        var envelope = TestEnvelope();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            new
            {
                jobId = "job-missing-lease-token",
                kind = "task",
                requiredCapability = "windows",
                automationId = (string?)null,
                runId = (string?)null,
                algorithm = envelope.Algorithm,
                nonce = envelope.Nonce,
                ciphertext = envelope.Ciphertext,
                leaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            }));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<CloudClientException>(
            () => client.ClaimJobAsync("runner-1", leaseSeconds: 30));

        Assert.Equal(CloudClientErrorKind.InvalidResponse, exception.Kind);
    }

    [Fact]
    public async Task ClaimRejectsAResponseWithoutAPositiveLeaseGeneration()
    {
        var envelope = TestEnvelope();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            new
            {
                jobId = "job-missing-lease-generation",
                kind = "task",
                requiredCapability = "windows",
                automationId = (string?)null,
                runId = (string?)null,
                algorithm = envelope.Algorithm,
                nonce = envelope.Nonce,
                ciphertext = envelope.Ciphertext,
                leaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
                leaseToken = "adl_positive-generation-required",
            }));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<CloudClientException>(
            () => client.ClaimJobAsync("runner-1", leaseSeconds: 30));

        Assert.Equal(CloudClientErrorKind.InvalidResponse, exception.Kind);
    }

    [Fact]
    public async Task CompletionRejectsAnIdentityThatWasNotReturnedByAClaim()
    {
        var handler = new RecordingHandler(_ =>
            throw new InvalidOperationException("The completion request must not be sent."));
        var client = CreateClient(handler);
        var identity = new CloudRunnerJobIdentity(
            "job-not-claimed",
            CloudRunnerPayloadKinds.Task,
            "windows");

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.CompleteJobAsync(identity, TestEnvelope()));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task QueueAndAutomationCreationRejectMismatchedServerIdentities()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/root/api/v1/jobs" => JsonResponse(
                HttpStatusCode.Created,
                new { jobId = "job-swapped" }),
            "/root/api/v1/automations" => JsonResponse(
                HttpStatusCode.Created,
                Automation("automation-swapped")),
            _ => throw new InvalidOperationException(),
        });
        var client = CreateClient(handler);
        var identity = new CloudRunnerJobIdentity(
            "job-expected",
            CloudRunnerPayloadKinds.Task,
            "windows");

        var queue = await Assert.ThrowsAsync<CloudClientException>(
            () => client.QueueJobAsync(identity, TestEnvelope()));
        var automation = await Assert.ThrowsAsync<CloudClientException>(() =>
            client.CreateAutomationAsync(
                "automation-expected",
                "Nightly review",
                60,
                "windows",
                TestEnvelope()));

        Assert.Equal(CloudClientErrorKind.InvalidResponse, queue.Kind);
        Assert.Equal(CloudClientErrorKind.InvalidResponse, automation.Kind);
    }

    [Fact]
    public async Task MissingSessionAndEmptyClaim_ReturnNull()
    {
        var handler = new RecordingHandler(
            request => request.RequestUri!.AbsolutePath.Contains("sync", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(handler);

        var session = await client.GetSessionAsync("missing-session");
        var claim = await client.ClaimJobAsync("runner-1");

        Assert.Null(session);
        Assert.Null(claim);
    }

    [Fact]
    public async Task DeletedSessionMapsGoneReceiptToATypedTombstone()
    {
        var handler = new RecordingHandler(
            _ => JsonResponse(
                HttpStatusCode.Gone,
                new { sessionId = "session-deleted", revision = 4 }));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<CloudSessionDeletedException>(
            () => client.GetSessionAsync("session-deleted"));

        Assert.Equal("session-deleted", exception.SessionId);
        Assert.Equal(4, exception.Revision);
        Assert.DoesNotContain("ciphertext", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, CloudClientErrorKind.Validation)]
    [InlineData(HttpStatusCode.Unauthorized, CloudClientErrorKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, CloudClientErrorKind.Authorization)]
    [InlineData(HttpStatusCode.NotFound, CloudClientErrorKind.NotFound)]
    [InlineData(HttpStatusCode.Conflict, CloudClientErrorKind.Conflict)]
    [InlineData(HttpStatusCode.RequestTimeout, CloudClientErrorKind.Timeout)]
    [InlineData(HttpStatusCode.TooManyRequests, CloudClientErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, CloudClientErrorKind.Server)]
    public async Task ErrorResponses_MapToStableSafeKinds(
        HttpStatusCode statusCode,
        CloudClientErrorKind expectedKind)
    {
        const string sensitiveBody = "server echoed a secret token";
        var handler = new RecordingHandler(
            _ => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(sensitiveBody),
            });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<CloudClientException>(
            () => client.GetPolicyAsync());

        Assert.Equal(expectedKind, exception.Kind);
        Assert.Equal(statusCode, exception.StatusCode);
        Assert.DoesNotContain(sensitiveBody, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedOrMalformedResponse_MapsWithoutReturningPartialData()
    {
        var oversizedHandler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 128)),
            });
        var malformedHandler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{"),
            });
        var options = new CloudConnectionOptions(
            new Uri("http://localhost:5050/root/"),
            maximumResponseBytes: 64);

        var oversized = await Assert.ThrowsAsync<CloudClientException>(
            () => CreateClient(oversizedHandler, options).GetPolicyAsync());
        var malformed = await Assert.ThrowsAsync<CloudClientException>(
            () => CreateClient(malformedHandler, options).GetPolicyAsync());

        Assert.Equal(CloudClientErrorKind.ResponseTooLarge, oversized.Kind);
        Assert.Equal(CloudClientErrorKind.InvalidResponse, malformed.Kind);
    }

    [Fact]
    public async Task RateLimitedResponse_PreservesRetryAfterWithoutReadingErrorBody()
    {
        var handler = new RecordingHandler(
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("sensitive server detail"),
                };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(30));
                return response;
            });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<CloudClientException>(
            () => client.GetPolicyAsync());

        Assert.Equal(TimeSpan.FromSeconds(30), exception.RetryAfter);
        Assert.DoesNotContain(
            "sensitive server detail",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestTimeout_MapsSeparatelyFromCallerCancellation()
    {
        var handler = new RecordingHandler(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        var timeoutOptions = new CloudConnectionOptions(
            new Uri("http://localhost:5050/"),
            requestTimeout: TimeSpan.FromMilliseconds(50));

        var timeout = await Assert.ThrowsAsync<CloudClientException>(
            () => CreateClient(handler, timeoutOptions).GetPolicyAsync());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateClient(
                    handler,
                    new CloudConnectionOptions(
                        new Uri("http://localhost:5050/"),
                        requestTimeout: TimeSpan.FromSeconds(5)))
                .GetPolicyAsync(cancellation.Token));

        Assert.Equal(CloudClientErrorKind.Timeout, timeout.Kind);
    }

    [Fact]
    public async Task TransportFailure_MapsWithoutLeakingHandlerMessage()
    {
        const string sensitiveMessage = "network failed with token material";
        var handler = new RecordingHandler(
            (_, _) => throw new HttpRequestException(sensitiveMessage));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<CloudClientException>(
            () => client.GetPolicyAsync());

        Assert.Equal(CloudClientErrorKind.Transport, exception.Kind);
        Assert.DoesNotContain(sensitiveMessage, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task InvalidIdentifierOrEnvelope_IsRejectedBeforeNetworkAccess()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("must not send"));
        var client = CreateClient(
            handler,
            new CloudConnectionOptions(
                new Uri("http://localhost:5050/"),
                maximumEnvelopeBytes: 16));
        var oversized = new EncryptedEnvelope(
            "AES-256-GCM",
            Convert.ToBase64String(new byte[12]),
            Convert.ToBase64String(new byte[17]));

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetSessionAsync("../secrets"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.QueueJobAsync(
                new CloudRunnerJobIdentity(
                    "job-oversized",
                    CloudRunnerPayloadKinds.Task,
                    "windows"),
                oversized));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PutSessionAsync_AllowsConfiguredEnvelopeAfterBase64Expansion()
    {
        const int decodedEnvelopeBytes = 1024 * 1024;
        var handler = new RecordingHandler(
            _ => JsonResponse(
                HttpStatusCode.Created,
                new { sessionId = "large-session", revision = 1 }));
        var client = CreateClient(
            handler,
            new CloudConnectionOptions(
                new Uri("http://localhost:5050/"),
                maximumEnvelopeBytes: decodedEnvelopeBytes));
        var envelope = new EncryptedEnvelope(
            "AES-256-GCM",
            Convert.ToBase64String(new byte[12]),
            Convert.ToBase64String(new byte[decodedEnvelopeBytes]));

        var receipt = await client.PutSessionAsync("large-session", 1, envelope);

        Assert.Equal("large-session", receipt.SessionId);
        Assert.Single(handler.Requests);
    }

    private static AgentDeskCloudClient CreateClient(
        HttpMessageHandler handler,
        CloudConnectionOptions? options = null) =>
        new(
            new HttpClient(handler),
            options ?? new CloudConnectionOptions(new Uri("http://localhost:5050/root/")),
            new StaticTokenProvider(AccessToken));

    private static EncryptedEnvelope TestEnvelope() => new(
        "AES-256-GCM",
        Convert.ToBase64String(new byte[12]),
        Convert.ToBase64String(new byte[16]));

    private static object EnvelopeResponse(
        string sessionId,
        int revision,
        EncryptedEnvelope envelope) => new
        {
            sessionId,
            revision,
            algorithm = envelope.Algorithm,
            nonce = envelope.Nonce,
            ciphertext = envelope.Ciphertext,
            updatedAt = DateTimeOffset.Parse("2026-07-17T11:00:00Z"),
        };

    private static object Handoff(EncryptedEnvelope envelope) => new
    {
        handoffId = "handoff-1",
        sourceDeviceId = "device-source",
        targetDeviceId = "device-target",
        sessionId = "session-2",
        algorithm = envelope.Algorithm,
        nonce = envelope.Nonce,
        ciphertext = envelope.Ciphertext,
        createdAt = DateTimeOffset.Parse("2026-07-17T11:30:00Z"),
    };

    private static object Automation(string automationId = "automation-1") => new
    {
        automationId,
        name = "Nightly review",
        intervalSeconds = 60,
        enabled = true,
        nextRunAt = DateTimeOffset.Parse("2026-07-17T12:30:00Z"),
    };

    private static object Policy(
        int version,
        bool remoteRunnerEnabled = true) => new
        {
            version,
            allowedExecutionProfiles = new[] { "NativeProtected", "WslStrict" },
            remoteRunnerEnabled,
            uiAutomationEnabled = false,
            maximumConcurrentJobs = 4,
            allowedPluginPublishers = Array.Empty<string>(),
        };

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, object value) =>
        new(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, JsonSerializerOptions.Web),
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class StaticTokenProvider(string token) : ICloudAccessTokenProvider
    {
        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(token);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this((request, _) => Task.FromResult(responder(request)))
        {
        }

        public RecordingHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(
                new RecordedRequest(
                    request.Method,
                    request.RequestUri!,
                    request.Headers.Authorization,
                    body));
            return await _responder(request, cancellationToken);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        AuthenticationHeaderValue? Authorization,
        string Body);
}
