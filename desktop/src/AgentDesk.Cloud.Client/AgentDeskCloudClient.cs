using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentDesk.Cloud.Client;

public sealed class AgentDeskCloudClient : IAgentDeskCloudClient
{
    private const int MaximumAccessTokenCharacters = 8 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly CloudConnectionOptions _options;
    private readonly ICloudAccessTokenProvider _accessTokenProvider;

    public AgentDeskCloudClient(
        HttpClient httpClient,
        CloudConnectionOptions options,
        ICloudAccessTokenProvider accessTokenProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(accessTokenProvider);

        _httpClient = httpClient;
        _options = options;
        _accessTokenProvider = accessTokenProvider;
    }

    public async Task<CloudIssuedToken> CreateTokenAsync(
        string subjectId,
        CloudTokenRole role,
        CancellationToken cancellationToken = default)
    {
        subjectId = CloudRequestGuard.Identifier(subjectId, 128, nameof(subjectId));
        var roleValue = RoleToWire(role);
        var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/tokens",
            new TokenCreateWire(subjectId, roleValue),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var wire = Deserialize<TokenIssuedWire>(response);
        if (!string.Equals(wire.SubjectId, subjectId, StringComparison.Ordinal) ||
            !string.Equals(wire.Role, roleValue, StringComparison.Ordinal) ||
            !IsValidAccessToken(wire.Token))
        {
            throw InvalidResponse();
        }

        return new CloudIssuedToken(wire.Token, wire.SubjectId, role);
    }

    public async Task RevokeTokenAsync(
        string subjectId,
        CancellationToken cancellationToken = default)
    {
        subjectId = CloudRequestGuard.Identifier(subjectId, 128, nameof(subjectId));
        var response = await SendAsync(
            HttpMethod.Delete,
            $"api/v1/tokens/{Uri.EscapeDataString(subjectId)}",
            body: null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    public async Task<CloudTeamPolicy> GetPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            HttpMethod.Get,
            "api/v1/policy",
            body: null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return MapPolicy(Deserialize<PolicyWire>(response));
    }

    public async Task<CloudTeamPolicy> UpdatePolicyAsync(
        CloudTeamPolicyUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var executionProfiles = NormalizeExecutionProfiles(update.AllowedExecutionProfiles);
        if (update.MaximumConcurrentJobs is < 1 or > CloudPolicyLimits.MaximumConcurrentJobs)
        {
            throw new ArgumentOutOfRangeException(nameof(update), "The job limit must be between 1 and 128.");
        }
        var publishers = NormalizeIdentifiers(
            update.AllowedPluginPublishers,
            maximumCount: CloudPolicyLimits.MaximumPluginPublishers,
            maximumLength: CloudPolicyLimits.MaximumPublisherIdCharacters,
            nameof(update));
        var response = await SendAsync(
            HttpMethod.Put,
            "api/v1/policy",
            new PolicyUpdateWire(
                executionProfiles,
                update.RemoteRunnerEnabled,
                update.UiAutomationEnabled,
                update.MaximumConcurrentJobs,
                publishers),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return MapPolicy(Deserialize<PolicyWire>(response));
    }

    public async Task<CloudSessionWriteReceipt> PutSessionAsync(
        string sessionId,
        int revision,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        sessionId = CloudRequestGuard.Identifier(sessionId, 128, nameof(sessionId));
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }
        envelope = ValidateEnvelope(envelope);
        var response = await SendAsync(
            HttpMethod.Put,
            $"api/v1/sync/sessions/{Uri.EscapeDataString(sessionId)}",
            new SessionWriteWire(
                revision,
                envelope.Algorithm,
                envelope.Nonce,
                envelope.Ciphertext),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var wire = Deserialize<SessionWriteReceiptWire>(response);
        if (!string.Equals(wire.SessionId, sessionId, StringComparison.Ordinal) ||
            wire.Revision != revision)
        {
            throw InvalidResponse();
        }
        return new CloudSessionWriteReceipt(wire.SessionId, wire.Revision);
    }

    public async Task<CloudSyncedSession?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        sessionId = CloudRequestGuard.Identifier(sessionId, 128, nameof(sessionId));
        var response = await SendAsync(
            HttpMethod.Get,
            $"api/v1/sync/sessions/{Uri.EscapeDataString(sessionId)}",
            body: null,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (response.StatusCode == HttpStatusCode.Gone)
        {
            var tombstone = Deserialize<SessionDeleteReceiptWire>(response);
            if (!string.Equals(tombstone.SessionId, sessionId, StringComparison.Ordinal) ||
                tombstone.Revision < 1)
            {
                throw InvalidResponse();
            }
            throw new CloudSessionDeletedException(tombstone.SessionId, tombstone.Revision);
        }
        EnsureSuccess(response);
        var wire = Deserialize<SessionWire>(response);
        if (!string.Equals(wire.SessionId, sessionId, StringComparison.Ordinal) || wire.Revision < 1)
        {
            throw InvalidResponse();
        }
        return new CloudSyncedSession(
            wire.SessionId,
            wire.Revision,
            MapResponseEnvelope(wire.Algorithm, wire.Nonce, wire.Ciphertext),
            wire.UpdatedAt);
    }

    public async Task<CloudSessionDeleteReceipt> DeleteSessionAsync(
        string sessionId,
        int knownRevision,
        CancellationToken cancellationToken = default)
    {
        sessionId = CloudRequestGuard.Identifier(sessionId, 128, nameof(sessionId));
        if (knownRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(knownRevision));
        }
        var response = await SendAsync(
            HttpMethod.Delete,
            $"api/v1/sync/sessions/{Uri.EscapeDataString(sessionId)}" +
            $"?revision={knownRevision.ToString(CultureInfo.InvariantCulture)}",
            body: null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var wire = Deserialize<SessionDeleteReceiptWire>(response);
        if (!string.Equals(wire.SessionId, sessionId, StringComparison.Ordinal) ||
            wire.Revision < knownRevision)
        {
            throw InvalidResponse();
        }
        return new CloudSessionDeleteReceipt(wire.SessionId, wire.Revision);
    }

    public async Task<CloudHandoff> CreateHandoffAsync(
        string handoffId,
        string targetDeviceId,
        string sessionId,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        handoffId = CloudRequestGuard.Identifier(handoffId, 128, nameof(handoffId));
        targetDeviceId = CloudRequestGuard.Identifier(
            targetDeviceId,
            128,
            nameof(targetDeviceId));
        sessionId = CloudRequestGuard.Identifier(sessionId, 128, nameof(sessionId));
        envelope = ValidateEnvelope(envelope);
        var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/handoffs",
            new HandoffCreateWire(
                handoffId,
                targetDeviceId,
                sessionId,
                envelope.Algorithm,
                envelope.Nonce,
                envelope.Ciphertext),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var handoff = MapHandoff(Deserialize<HandoffWire>(response));
        if (!string.Equals(handoff.HandoffId, handoffId, StringComparison.Ordinal) ||
            !string.Equals(handoff.TargetDeviceId, targetDeviceId, StringComparison.Ordinal) ||
            !string.Equals(handoff.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw InvalidResponse();
        }
        return handoff;
    }

    public async Task<IReadOnlyList<CloudHandoff>> ListHandoffsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        var response = await SendAsync(
            HttpMethod.Get,
            $"api/v1/handoffs?limit={limit}",
            body: null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return Deserialize<HandoffWire[]>(response).Select(MapHandoff).ToArray();
    }

    public async Task<bool> AcknowledgeHandoffAsync(
        string handoffId,
        CancellationToken cancellationToken = default)
    {
        handoffId = CloudRequestGuard.Identifier(handoffId, 128, nameof(handoffId));
        var response = await SendAsync(
            HttpMethod.Post,
            $"api/v1/handoffs/{Uri.EscapeDataString(handoffId)}/acknowledge",
            body: null,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        EnsureSuccess(response);
        return true;
    }

    public async Task RegisterRunnerAsync(
        string runnerId,
        IReadOnlyList<string> capabilities,
        CancellationToken cancellationToken = default)
    {
        runnerId = CloudRequestGuard.Identifier(runnerId, 128, nameof(runnerId));
        var normalizedCapabilities = NormalizeIdentifiers(
            capabilities,
            maximumCount: 32,
            maximumLength: 64,
            nameof(capabilities),
            requireAtLeastOne: true);
        var response = await SendAsync(
            HttpMethod.Post,
            $"api/v1/runners/{Uri.EscapeDataString(runnerId)}/register",
            new RunnerRegistrationWire(normalizedCapabilities),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    public async Task<CloudJobReceipt> QueueJobAsync(
        CloudRunnerJobIdentity identity,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Kind != CloudRunnerPayloadKinds.Task)
        {
            throw new ArgumentException("Only direct tasks can be queued by a device.", nameof(identity));
        }
        envelope = ValidateEnvelope(envelope);
        var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/jobs",
            new JobQueueWire(
                identity.JobId,
                identity.Kind,
                identity.RequiredCapability,
                identity.AutomationId,
                identity.RunId,
                envelope.Algorithm,
                envelope.Nonce,
                envelope.Ciphertext),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var wire = Deserialize<JobReceiptWire>(response);
        try
        {
            CloudRequestGuard.Identifier(wire.JobId, 128, nameof(wire.JobId));
            if (!string.Equals(wire.JobId, identity.JobId, StringComparison.Ordinal))
            {
                throw InvalidResponse();
            }
            return new CloudJobReceipt(wire.JobId);
        }
        catch (ArgumentException)
        {
            throw InvalidResponse();
        }
    }

    public async Task<CloudRunnerJob?> ClaimJobAsync(
        string runnerId,
        int leaseSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        runnerId = CloudRequestGuard.Identifier(runnerId, 128, nameof(runnerId));
        if (leaseSeconds is < 10 or > 600)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        }
        var response = await SendAsync(
            HttpMethod.Post,
            $"api/v1/runners/{Uri.EscapeDataString(runnerId)}/claim",
            new JobClaimWire(leaseSeconds),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }
        EnsureSuccess(response);
        var wire = Deserialize<RunnerJobWire>(response);
        try
        {
            CloudRequestGuard.Identifier(wire.JobId, 128, nameof(wire.JobId));
            CloudRequestGuard.Identifier(
                wire.RequiredCapability,
                64,
                nameof(wire.RequiredCapability));
            return new CloudRunnerJob(
                CloudRunnerJobIdentity.Claimed(
                    wire.JobId,
                    wire.Kind,
                    wire.RequiredCapability,
                    wire.AutomationId,
                    wire.RunId,
                    runnerId,
                    wire.LeaseToken,
                    wire.LeaseGeneration),
                MapResponseEnvelope(wire.Algorithm, wire.Nonce, wire.Ciphertext),
                wire.LeaseExpiresAt);
        }
        catch (ArgumentException)
        {
            throw InvalidResponse();
        }
    }

    public async Task CompleteJobAsync(
        CloudRunnerJobIdentity identity,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.LeaseRunnerId is null || identity.LeaseToken is null ||
            identity.LeaseGeneration < 1)
        {
            throw new ArgumentException(
                "Only an identity returned by a runner claim can complete a job.",
                nameof(identity));
        }
        envelope = ValidateEnvelope(envelope);
        var response = await SendAsync(
            HttpMethod.Post,
            $"api/v1/jobs/{Uri.EscapeDataString(identity.JobId)}/complete",
            new JobCompleteWire(
                identity.LeaseRunnerId,
                identity.LeaseToken,
                identity.LeaseGeneration,
                identity.ResultKind,
                identity.RequiredCapability,
                identity.AutomationId,
                identity.RunId,
                envelope.Algorithm,
                envelope.Nonce,
                envelope.Ciphertext),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    public async Task<CloudAutomation> CreateAutomationAsync(
        string automationId,
        string name,
        int intervalSeconds,
        string requiredCapability,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        automationId = CloudRequestGuard.Identifier(
            automationId,
            128,
            nameof(automationId));
        name = CloudRequestGuard.AutomationName(name);
        if (intervalSeconds is < 60 or > 2_678_400)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
        }
        requiredCapability = CloudRequestGuard.Identifier(
            requiredCapability,
            64,
            nameof(requiredCapability));
        envelope = ValidateEnvelope(envelope);
        var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/automations",
            new AutomationCreateWire(
                automationId,
                CloudRunnerPayloadKinds.Automation,
                name,
                intervalSeconds,
                requiredCapability,
                envelope.Algorithm,
                envelope.Nonce,
                envelope.Ciphertext),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var automation = MapAutomation(Deserialize<AutomationWire>(response));
        if (!string.Equals(automation.AutomationId, automationId, StringComparison.Ordinal))
        {
            throw InvalidResponse();
        }
        return automation;
    }

    public async Task<IReadOnlyList<CloudAutomation>> ListAutomationsAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            HttpMethod.Get,
            "api/v1/automations",
            body: null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return Deserialize<AutomationWire[]>(response).Select(MapAutomation).ToArray();
    }

    public async Task<bool> DisableAutomationAsync(
        string automationId,
        CancellationToken cancellationToken = default)
    {
        automationId = CloudRequestGuard.Identifier(
            automationId,
            128,
            nameof(automationId));
        var response = await SendAsync(
            HttpMethod.Delete,
            $"api/v1/automations/{Uri.EscapeDataString(automationId)}",
            body: null,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        EnsureSuccess(response);
        return true;
    }

    private async Task<CloudHttpResponse> SendAsync(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(_options.RequestTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var linkedToken = linkedSource.Token;
        string accessToken;
        try
        {
            accessToken = await _accessTokenProvider
                .GetAccessTokenAsync(linkedToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            throw TimeoutError();
        }
        catch (OperationCanceledException)
        {
            throw TimeoutError();
        }
        catch (Exception)
        {
            throw new CloudClientException(
                CloudClientErrorKind.Credential,
                "The cloud access token could not be obtained.");
        }

        if (!IsValidAccessToken(accessToken))
        {
            throw new CloudClientException(
                CloudClientErrorKind.Credential,
                "The cloud access token is invalid.");
        }

        using var request = new HttpRequestMessage(method, new Uri(_options.BaseUri, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), JsonOptions);
            if (json.Length > _options.MaximumSerializedEnvelopeBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(body), "The request exceeds the configured limit.");
            }
            request.Content = new ByteArrayContent(json);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
        }

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedToken).ConfigureAwait(false);
            var retryAfter = GetRetryAfter(response.Headers.RetryAfter);
            if ((!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Gone) ||
                response.Content is null)
            {
                return new CloudHttpResponse(response.StatusCode, [], retryAfter);
            }
            var content = await ReadContentAsync(response.Content, linkedToken).ConfigureAwait(false);
            return new CloudHttpResponse(response.StatusCode, content, retryAfter);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            throw TimeoutError();
        }
        catch (OperationCanceledException)
        {
            throw TimeoutError();
        }
        catch (HttpRequestException)
        {
            throw TransportError();
        }
        catch (IOException)
        {
            throw TransportError();
        }
    }

    private async Task<byte[]> ReadContentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 &&
            content.Headers.ContentLength > _options.MaximumResponseBytes)
        {
            throw new CloudClientException(
                CloudClientErrorKind.ResponseTooLarge,
                "The cloud response exceeded the configured limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return output.ToArray();
                }
                if (output.Length + read > _options.MaximumResponseBytes)
                {
                    throw new CloudClientException(
                        CloudClientErrorKind.ResponseTooLarge,
                        "The cloud response exceeded the configured limit.");
                }
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            Array.Clear(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static T Deserialize<T>(CloudHttpResponse response)
    {
        if (response.Body.Length == 0)
        {
            throw InvalidResponse();
        }
        try
        {
            return JsonSerializer.Deserialize<T>(response.Body, JsonOptions) ?? throw InvalidResponse();
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
        catch (NotSupportedException)
        {
            throw InvalidResponse();
        }
    }

    private static void EnsureSuccess(CloudHttpResponse response)
    {
        if ((int)response.StatusCode is >= 200 and <= 299)
        {
            return;
        }

        var kind = response.StatusCode switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                CloudClientErrorKind.Validation,
            HttpStatusCode.Unauthorized => CloudClientErrorKind.Authentication,
            HttpStatusCode.Forbidden => CloudClientErrorKind.Authorization,
            HttpStatusCode.NotFound => CloudClientErrorKind.NotFound,
            HttpStatusCode.Conflict => CloudClientErrorKind.Conflict,
            HttpStatusCode.RequestTimeout => CloudClientErrorKind.Timeout,
            HttpStatusCode.TooManyRequests => CloudClientErrorKind.RateLimited,
            >= HttpStatusCode.InternalServerError => CloudClientErrorKind.Server,
            _ => CloudClientErrorKind.InvalidResponse,
        };
        throw new CloudClientException(
            kind,
            "The cloud service rejected the request.",
            response.StatusCode,
            response.RetryAfter);
    }

    private EncryptedEnvelope ValidateEnvelope(EncryptedEnvelope envelope) =>
        CloudRequestGuard.Envelope(envelope, _options.MaximumEnvelopeBytes, nameof(envelope));

    private EncryptedEnvelope MapResponseEnvelope(
        string algorithm,
        string nonce,
        string ciphertext)
    {
        try
        {
            return ValidateEnvelope(new EncryptedEnvelope(algorithm, nonce, ciphertext));
        }
        catch (ArgumentException)
        {
            throw InvalidResponse();
        }
    }

    private EncryptedEnvelope MapResponseEnvelope(EnvelopeWire wire) =>
        MapResponseEnvelope(wire.Algorithm, wire.Nonce, wire.Ciphertext);

    private CloudHandoff MapHandoff(HandoffWire wire)
    {
        try
        {
            CloudRequestGuard.Identifier(wire.HandoffId, 128, nameof(wire.HandoffId));
            CloudRequestGuard.Identifier(
                wire.SourceDeviceId,
                128,
                nameof(wire.SourceDeviceId));
            CloudRequestGuard.Identifier(
                wire.TargetDeviceId,
                128,
                nameof(wire.TargetDeviceId));
            CloudRequestGuard.Identifier(wire.SessionId, 128, nameof(wire.SessionId));
            return new CloudHandoff(
                wire.HandoffId,
                wire.SourceDeviceId,
                wire.TargetDeviceId,
                wire.SessionId,
                MapResponseEnvelope(wire.Algorithm, wire.Nonce, wire.Ciphertext),
                wire.CreatedAt);
        }
        catch (ArgumentException)
        {
            throw InvalidResponse();
        }
    }

    private static CloudAutomation MapAutomation(AutomationWire wire)
    {
        try
        {
            CloudRequestGuard.Identifier(
                wire.AutomationId,
                128,
                nameof(wire.AutomationId));
            CloudRequestGuard.AutomationName(wire.Name);
            if (wire.IntervalSeconds is < 60 or > 2_678_400)
            {
                throw new ArgumentOutOfRangeException(nameof(wire.IntervalSeconds));
            }
            return new CloudAutomation(
                wire.AutomationId,
                wire.Name,
                wire.IntervalSeconds,
                wire.Enabled,
                wire.NextRunAt);
        }
        catch (ArgumentException)
        {
            throw InvalidResponse();
        }
    }

    private static CloudTeamPolicy MapPolicy(PolicyWire wire)
    {
        try
        {
            if (wire.Version < 0 ||
                wire.MaximumConcurrentJobs is < 1 or > CloudPolicyLimits.MaximumConcurrentJobs)
            {
                throw new ArgumentOutOfRangeException(nameof(wire));
            }
            var profiles = NormalizeExecutionProfiles(wire.AllowedExecutionProfiles);
            var publishers = NormalizeIdentifiers(
                wire.AllowedPluginPublishers,
                maximumCount: CloudPolicyLimits.MaximumPluginPublishers,
                maximumLength: CloudPolicyLimits.MaximumPublisherIdCharacters,
                nameof(wire));
            return new CloudTeamPolicy(
                wire.Version,
                profiles,
                wire.RemoteRunnerEnabled,
                wire.UiAutomationEnabled,
                wire.MaximumConcurrentJobs,
                publishers);
        }
        catch (ArgumentException)
        {
            throw InvalidResponse();
        }
    }

    private static string[] NormalizeExecutionProfiles(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var normalized = values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (normalized.Length is < 1 or > 2 ||
            normalized.Any(value => value is not ("NativeProtected" or "WslStrict")))
        {
            throw new ArgumentException("At least one supported execution profile is required.", nameof(values));
        }
        return normalized;
    }

    private static string[] NormalizeIdentifiers(
        IReadOnlyList<string> values,
        int maximumCount,
        int maximumLength,
        string parameterName,
        bool requireAtLeastOne = false)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > maximumCount || (requireAtLeastOne && values.Count == 0))
        {
            throw new ArgumentException("The identifier list has an invalid size.", parameterName);
        }
        return values
            .Select(value => CloudRequestGuard.Identifier(value, maximumLength, parameterName))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string RoleToWire(CloudTokenRole role) => role switch
    {
        CloudTokenRole.Device => "device",
        CloudTokenRole.Service => "service",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static bool IsValidAccessToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumAccessTokenCharacters &&
        !value.Any(char.IsWhiteSpace);

    private static TimeSpan? GetRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }
        if (retryAfter?.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return null;
    }

    private static CloudClientException TimeoutError() => new(
        CloudClientErrorKind.Timeout,
        "The cloud request timed out.");

    private static CloudClientException TransportError() => new(
        CloudClientErrorKind.Transport,
        "The cloud service could not be reached.");

    private static CloudClientException InvalidResponse() => new(
        CloudClientErrorKind.InvalidResponse,
        "The cloud service returned an invalid response.");

    private sealed record CloudHttpResponse(
        HttpStatusCode StatusCode,
        byte[] Body,
        TimeSpan? RetryAfter);
}
