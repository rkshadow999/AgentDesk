using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace AgentDesk.Cloud.Tests;

public sealed class CloudApiTests : IAsyncLifetime
{
    private const string BootstrapToken = "agentdesk-test-bootstrap-token-000000000000";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"agentdesk-cloud-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder => builder.ConfigureAppConfiguration(
                (_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AgentDeskCloud:BootstrapToken"] = BootstrapToken,
                        ["AgentDeskCloud:DatabasePath"] = Path.Combine(_root, "cloud.db"),
                        ["AgentDeskCloud:RequireHttps"] = "false",
                        ["AgentDeskCloud:AutomationPollingIntervalSeconds"] = "1",
                    })));
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task HealthIsAnonymousButApiRequiresBearerAuthentication()
    {
        var health = await Client.GetAsync("/health/live");
        var protectedResponse = await Client.GetAsync("/api/v1/policy");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
    }

    [Fact]
    public async Task RateLimitExhaustionIsIsolatedPerAuthenticatedSubject()
    {
        var first = await IssueTokenAsync("rate-limit-first", "device");
        var second = await IssueTokenAsync("rate-limit-second", "device");
        using var firstClient = CreateAuthenticatedClient(first.Token);
        using var secondClient = CreateAuthenticatedClient(second.Token);
        var firstWasLimited = false;

        for (var attempt = 0; attempt < 250; attempt++)
        {
            using var response = await firstClient.GetAsync("/api/v1/policy");
            if (response.StatusCode is HttpStatusCode.TooManyRequests)
            {
                firstWasLimited = true;
                break;
            }
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var secondResponse = await secondClient.GetAsync("/api/v1/policy");
        Assert.True(firstWasLimited);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
    }

    [Fact]
    public async Task SessionSyncStoresOpaqueCiphertextWithMonotonicRevisions()
    {
        Authenticate(Client, BootstrapToken);
        var envelope = new
        {
            revision = 1,
            algorithm = "AES-256-GCM",
            nonce = Convert.ToBase64String(new byte[12]),
            ciphertext = Convert.ToBase64String("opaque encrypted session"u8.ToArray()),
        };

        var created = await Client.PutAsJsonAsync("/api/v1/sync/sessions/session-42", envelope);
        var duplicate = await Client.PutAsJsonAsync("/api/v1/sync/sessions/session-42", envelope);
        var fetched = await Client.GetAsync("/api/v1/sync/sessions/session-42");
        var json = await fetched.Content.ReadAsStringAsync();
        var deleted = await Client.DeleteAsync("/api/v1/sync/sessions/session-42?revision=1");
        var deleteReceipt = await deleted.Content.ReadFromJsonAsync<SessionDeleteReceipt>();
        var retriedDelete = await Client.DeleteAsync(
            "/api/v1/sync/sessions/session-42?revision=1");
        var fetchedAfterDelete = await Client.GetAsync("/api/v1/sync/sessions/session-42");
        var tombstone = await fetchedAfterDelete.Content
            .ReadFromJsonAsync<SessionDeleteReceipt>();
        var staleResurrection = await Client.PutAsJsonAsync(
            "/api/v1/sync/sessions/session-42",
            envelope with { revision = 2 });
        var recreated = await Client.PutAsJsonAsync(
            "/api/v1/sync/sessions/session-42",
            envelope with { revision = 3 });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Contains(envelope.ciphertext, json, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(2, deleteReceipt!.Revision);
        Assert.Equal(HttpStatusCode.OK, retriedDelete.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, fetchedAfterDelete.StatusCode);
        Assert.Equal(2, tombstone!.Revision);
        Assert.Equal(HttpStatusCode.Conflict, staleResurrection.StatusCode);
        Assert.Equal(HttpStatusCode.Created, recreated.StatusCode);
    }

    [Fact]
    public async Task RunnerClaimsOneLeasedJobAndCompletesIt()
    {
        Authenticate(Client, BootstrapToken);
        var register = await Client.PostAsJsonAsync(
            "/api/v1/runners/runner-1/register",
            new { capabilities = new[] { "windows", "native" } });
        var queued = await Client.PostAsJsonAsync(
            "/api/v1/jobs",
            RunnerJob("job-basic-1", "windows", "opaque encrypted job payload"));
        var queuedBody = await queued.Content.ReadFromJsonAsync<JobCreated>();
        var claim = await Client.PostAsJsonAsync(
            "/api/v1/runners/runner-1/claim",
            new { leaseSeconds = 30 });
        var claimed = await claim.Content.ReadFromJsonAsync<JobClaimed>();
        var secondClaim = await Client.PostAsJsonAsync(
            "/api/v1/runners/runner-1/claim",
            new { leaseSeconds = 30 });
        var complete = await Client.PostAsJsonAsync(
            $"/api/v1/jobs/{claimed!.JobId}/complete",
            RunnerCompletion(
                "runner-1",
                claimed.LeaseToken!,
                claimed.LeaseGeneration,
                "task-result",
                claimed.RequiredCapability!,
                claimed.AutomationId,
                claimed.RunId,
                "opaque encrypted result payload"));

        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        Assert.Equal(HttpStatusCode.Created, queued.StatusCode);
        Assert.Equal(queuedBody!.JobId, claimed.JobId);
        Assert.Equal(HttpStatusCode.NoContent, secondClaim.StatusCode);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
    }

    [Fact]
    public async Task RunnerPolicyBlocksQueueAndClaimAndEnforcesTheConcurrentJobLimit()
    {
        var device = await IssueTokenAsync("runner-policy-device", "device");
        var service = await IssueTokenAsync("runner-policy-service", "service");
        using var adminClient = CreateAuthenticatedClient(BootstrapToken);
        using var deviceClient = CreateAuthenticatedClient(device.Token);
        using var serviceClient = CreateAuthenticatedClient(service.Token);
        await serviceClient.PostAsJsonAsync(
            "/api/v1/runners/runner-policy-service/register",
            new { capabilities = new[] { "windows" } });
        await UpdateRunnerPolicyAsync(adminClient, remoteRunnerEnabled: true, maximumConcurrentJobs: 1);

        var first = await deviceClient.PostAsJsonAsync(
            "/api/v1/jobs",
            RunnerJob("job-policy-1", "windows", "first encrypted task"));
        var overLimit = await deviceClient.PostAsJsonAsync(
            "/api/v1/jobs",
            RunnerJob("job-policy-2", "windows", "second encrypted task"));
        await UpdateRunnerPolicyAsync(adminClient, remoteRunnerEnabled: false, maximumConcurrentJobs: 1);
        var disabledQueue = await deviceClient.PostAsJsonAsync(
            "/api/v1/jobs",
            RunnerJob("job-policy-3", "windows", "disabled encrypted task"));
        var disabledClaim = await serviceClient.PostAsJsonAsync(
            "/api/v1/runners/runner-policy-service/claim",
            new { leaseSeconds = 30 });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, overLimit.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, disabledQueue.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, disabledClaim.StatusCode);
    }

    [Fact]
    public async Task RunnerClaimAndCompletionMetadataCannotBeSwapped()
    {
        var device = await IssueTokenAsync("runner-binding-device", "device");
        var service = await IssueTokenAsync("runner-binding-service", "service");
        using var deviceClient = CreateAuthenticatedClient(device.Token);
        using var serviceClient = CreateAuthenticatedClient(service.Token);
        await serviceClient.PostAsJsonAsync(
            "/api/v1/runners/runner-binding-service/register",
            new { capabilities = new[] { "windows" } });
        var queued = await deviceClient.PostAsJsonAsync(
            "/api/v1/jobs",
            RunnerJob("job-binding-1", "windows", "bound encrypted task"));
        var receipt = await queued.Content.ReadFromJsonAsync<JobCreated>();
        var claim = await serviceClient.PostAsJsonAsync(
            "/api/v1/runners/runner-binding-service/claim",
            new { leaseSeconds = 30 });
        var claimed = await claim.Content.ReadFromJsonAsync<JobClaimed>();

        var swapped = await serviceClient.PostAsJsonAsync(
            "/api/v1/jobs/job-binding-1/complete",
            RunnerCompletion(
                "runner-binding-service",
                claimed!.LeaseToken!,
                claimed.LeaseGeneration,
                kind: "task-result",
                requiredCapability: "wsl",
                automationId: null,
                runId: null,
                "swapped encrypted result"));
        var completed = await serviceClient.PostAsJsonAsync(
            "/api/v1/jobs/job-binding-1/complete",
            RunnerCompletion(
                "runner-binding-service",
                claimed.LeaseToken!,
                claimed.LeaseGeneration,
                kind: "task-result",
                requiredCapability: "windows",
                automationId: null,
                runId: null,
                "bound encrypted result"));

        Assert.Equal(HttpStatusCode.Created, queued.StatusCode);
        Assert.Equal("job-binding-1", receipt!.JobId);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        Assert.Equal("job-binding-1", claimed!.JobId);
        Assert.Equal("task", claimed.Kind);
        Assert.Equal("windows", claimed.RequiredCapability);
        Assert.Null(claimed.AutomationId);
        Assert.Null(claimed.RunId);
        Assert.Equal(HttpStatusCode.Conflict, swapped.StatusCode);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
    }

    [Fact]
    public async Task AdminIssuesDeviceAndServiceTokensWithLeastPrivilegeRoles()
    {
        var device = await IssueTokenAsync("device-1", "device");
        var service = await IssueTokenAsync("runner-service", "service");
        using var adminClient = CreateAuthenticatedClient(BootstrapToken);
        using var deviceClient = CreateAuthenticatedClient(device.Token);
        using var serviceClient = CreateAuthenticatedClient(service.Token);

        var deviceRunnerRegistration = await deviceClient.PostAsJsonAsync(
            "/api/v1/runners/device-1/register",
            new { capabilities = new[] { "windows" } });
        var serviceRunnerRegistration = await serviceClient.PostAsJsonAsync(
            "/api/v1/runners/runner-service/register",
            new { capabilities = new[] { "windows" } });
        var serviceImpersonation = await serviceClient.PostAsJsonAsync(
            "/api/v1/runners/another-service/register",
            new { capabilities = new[] { "windows" } });
        var serviceJobCreation = await serviceClient.PostAsJsonAsync(
            "/api/v1/jobs",
            RunnerJob("job-service-forbidden", "windows", "service job"));
        var deviceJobCreation = await deviceClient.PostAsJsonAsync(
            "/api/v1/jobs",
            RunnerJob("job-device-allowed", "windows", "device encrypted job payload"));
        var forbiddenRevocation = await deviceClient.DeleteAsync(
            "/api/v1/tokens/runner-service");
        var revoked = await adminClient.DeleteAsync("/api/v1/tokens/device-1");
        var revokedCredential = await deviceClient.GetAsync("/api/v1/policy");

        Assert.Equal("device", device.Role);
        Assert.Equal("service", service.Role);
        Assert.Equal(HttpStatusCode.Forbidden, deviceRunnerRegistration.StatusCode);
        Assert.Equal(HttpStatusCode.OK, serviceRunnerRegistration.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, serviceImpersonation.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, serviceJobCreation.StatusCode);
        Assert.Equal(HttpStatusCode.Created, deviceJobCreation.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRevocation.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedCredential.StatusCode);
    }

    [Fact]
    public async Task OnlyLeaseOwningServiceCanCompleteJob()
    {
        var device = await IssueTokenAsync("job-device", "device");
        var owner = await IssueTokenAsync("runner-owner", "service");
        var otherService = await IssueTokenAsync("runner-other", "service");
        using var deviceClient = CreateAuthenticatedClient(device.Token);
        using var ownerClient = CreateAuthenticatedClient(owner.Token);
        using var otherClient = CreateAuthenticatedClient(otherService.Token);
        await ownerClient.PostAsJsonAsync(
            "/api/v1/runners/runner-owner/register",
            new { capabilities = new[] { "windows" } });
        await deviceClient.PostAsJsonAsync(
            "/api/v1/jobs",
            RunnerJob("job-lease-owner", "windows", "encrypted job for lease owner"));
        var claim = await ownerClient.PostAsJsonAsync(
            "/api/v1/runners/runner-owner/claim",
            new { leaseSeconds = 30 });
        var claimed = await claim.Content.ReadFromJsonAsync<JobClaimed>();
        var completion = RunnerCompletion(
            "runner-owner",
            claimed!.LeaseToken!,
            claimed.LeaseGeneration,
            "task-result",
            "windows",
            automationId: null,
            runId: null,
            "encrypted job completion payload");

        var rejected = await otherClient.PostAsJsonAsync(
            $"/api/v1/jobs/{claimed.JobId}/complete",
            completion);
        var accepted = await ownerClient.PostAsJsonAsync(
            $"/api/v1/jobs/{claimed.JobId}/complete",
            completion);

        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task LeaseTokenIsHighEntropyHashedAtRestAndRequiredForCompletion()
    {
        var device = await IssueTokenAsync("lease-token-device", "device");
        var runner = await IssueTokenAsync("lease-token-runner", "service");
        using var deviceClient = CreateAuthenticatedClient(device.Token);
        using var runnerClient = CreateAuthenticatedClient(runner.Token);
        await runnerClient.PostAsJsonAsync(
            "/api/v1/runners/lease-token-runner/register",
            new { capabilities = new[] { "windows" } });
        await deviceClient.PostAsJsonAsync(
            "/api/v1/jobs",
            RunnerJob("job-lease-token", "windows", "encrypted lease token task"));

        var claim = await runnerClient.PostAsJsonAsync(
            "/api/v1/runners/lease-token-runner/claim",
            new { leaseSeconds = 30 });
        var claimed = await claim.Content.ReadFromJsonAsync<JobClaimed>();
        var storedHash = await ReadLeaseTokenHashAsync(claimed!.JobId);
        var invalid = await runnerClient.PostAsJsonAsync(
            $"/api/v1/jobs/{claimed.JobId}/complete",
            RunnerCompletion(
                "lease-token-runner",
                "adl_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                claimed.LeaseGeneration,
                "task-result",
                "windows",
                automationId: null,
                runId: null,
                "invalid lease token result"));
        var accepted = await runnerClient.PostAsJsonAsync(
            $"/api/v1/jobs/{claimed.JobId}/complete",
            RunnerCompletion(
                "lease-token-runner",
                claimed.LeaseToken!,
                claimed.LeaseGeneration,
                "task-result",
                "windows",
                automationId: null,
                runId: null,
                "valid lease token result"));

        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        Assert.StartsWith("adl_", claimed.LeaseToken, StringComparison.Ordinal);
        Assert.True(claimed.LeaseToken!.Length >= 47);
        Assert.NotNull(storedHash);
        Assert.NotEqual(claimed.LeaseToken, storedHash);
        Assert.Equal(64, storedHash!.Length);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(claimed.LeaseToken))),
            storedHash);
        Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task ExpiredAndSupersededLeaseTokensCannotCompleteAJob()
    {
        var device = await IssueTokenAsync("lease-generation-device", "device");
        var runner = await IssueTokenAsync("lease-generation-runner", "service");
        using var deviceClient = CreateAuthenticatedClient(device.Token);
        using var runnerClient = CreateAuthenticatedClient(runner.Token);
        await runnerClient.PostAsJsonAsync(
            "/api/v1/runners/lease-generation-runner/register",
            new { capabilities = new[] { "windows" } });
        await deviceClient.PostAsJsonAsync(
            "/api/v1/jobs",
            RunnerJob("job-lease-generation", "windows", "encrypted generation task"));
        var firstResponse = await runnerClient.PostAsJsonAsync(
            "/api/v1/runners/lease-generation-runner/claim",
            new { leaseSeconds = 30 });
        var first = await firstResponse.Content.ReadFromJsonAsync<JobClaimed>();
        await ExpireLeaseAsync(first!.JobId);

        var expired = await runnerClient.PostAsJsonAsync(
            $"/api/v1/jobs/{first.JobId}/complete",
            RunnerCompletion(
                "lease-generation-runner",
                first.LeaseToken!,
                first.LeaseGeneration,
                "task-result",
                "windows",
                automationId: null,
                runId: null,
                "expired lease result"));
        Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);

        var secondResponse = await runnerClient.PostAsJsonAsync(
            "/api/v1/runners/lease-generation-runner/claim",
            new { leaseSeconds = 30 });
        var second = await secondResponse.Content.ReadFromJsonAsync<JobClaimed>();
        var superseded = await runnerClient.PostAsJsonAsync(
            $"/api/v1/jobs/{first.JobId}/complete",
            RunnerCompletion(
                "lease-generation-runner",
                first.LeaseToken!,
                first.LeaseGeneration,
                "task-result",
                "windows",
                automationId: null,
                runId: null,
                "superseded lease result"));
        var accepted = await runnerClient.PostAsJsonAsync(
            $"/api/v1/jobs/{first.JobId}/complete",
            RunnerCompletion(
                "lease-generation-runner",
                second!.LeaseToken!,
                second.LeaseGeneration,
                "task-result",
                "windows",
                automationId: null,
                runId: null,
                "current lease result"));

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        Assert.Equal(HttpStatusCode.Conflict, superseded.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task LeaseGenerationPersistsIncrementsAndFencesCompletion()
    {
        var device = await IssueTokenAsync("generation-fence-device", "device");
        var runner = await IssueTokenAsync("generation-fence-runner", "service");
        using var deviceClient = CreateAuthenticatedClient(device.Token);
        using var runnerClient = CreateAuthenticatedClient(runner.Token);
        await runnerClient.PostAsJsonAsync(
            "/api/v1/runners/generation-fence-runner/register",
            new { capabilities = new[] { "windows" } });
        await deviceClient.PostAsJsonAsync(
            "/api/v1/jobs",
            RunnerJob("job-generation-fence", "windows", "encrypted generation fence task"));

        var firstResponse = await runnerClient.PostAsJsonAsync(
            "/api/v1/runners/generation-fence-runner/claim",
            new { leaseSeconds = 30 });
        var first = await firstResponse.Content.ReadFromJsonAsync<JobClaimed>();
        Assert.Equal(1, first!.LeaseGeneration);
        await ExpireLeaseAsync(first.JobId);

        var secondResponse = await runnerClient.PostAsJsonAsync(
            "/api/v1/runners/generation-fence-runner/claim",
            new { leaseSeconds = 30 });
        var second = await secondResponse.Content.ReadFromJsonAsync<JobClaimed>();
        Assert.Equal(2, second!.LeaseGeneration);
        Assert.Equal(2, await ReadLeaseGenerationAsync(second.JobId));

        var staleGeneration = await runnerClient.PostAsJsonAsync(
            $"/api/v1/jobs/{second.JobId}/complete",
            Envelope(
                new
                {
                    runnerId = "generation-fence-runner",
                    leaseToken = second.LeaseToken,
                    leaseGeneration = first.LeaseGeneration,
                    kind = "task-result",
                    requiredCapability = "windows",
                    automationId = (string?)null,
                    runId = (string?)null,
                },
                "stale generation result"));
        var accepted = await runnerClient.PostAsJsonAsync(
            $"/api/v1/jobs/{second.JobId}/complete",
            Envelope(
                new
                {
                    runnerId = "generation-fence-runner",
                    leaseToken = second.LeaseToken,
                    leaseGeneration = second.LeaseGeneration,
                    kind = "task-result",
                    requiredCapability = "windows",
                    automationId = (string?)null,
                    runId = (string?)null,
                },
                "current generation result"));

        Assert.Equal(HttpStatusCode.Conflict, staleGeneration.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task TeamPolicyUpdatePersistsAndRequiresAdminRole()
    {
        var device = await IssueTokenAsync("device-policy", "device");
        using var adminClient = CreateAuthenticatedClient(BootstrapToken);
        using var deviceClient = CreateAuthenticatedClient(device.Token);
        var update = new
        {
            allowedExecutionProfiles = new[] { "WslStrict" },
            remoteRunnerEnabled = false,
            uiAutomationEnabled = true,
            maximumConcurrentJobs = 2,
            allowedPluginPublishers = new[] { "publisher-1" },
        };

        var updatedResponse = await adminClient.PutAsJsonAsync("/api/v1/policy", update);
        var updated = await updatedResponse.Content.ReadFromJsonAsync<PolicyResponse>();
        var forbidden = await deviceClient.PutAsJsonAsync("/api/v1/policy", update);
        var fetched = await deviceClient.GetFromJsonAsync<PolicyResponse>("/api/v1/policy");

        Assert.Equal(HttpStatusCode.OK, updatedResponse.StatusCode);
        Assert.Equal(1, updated!.Version);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(new[] { "WslStrict" }, fetched!.AllowedExecutionProfiles);
        Assert.False(fetched.RemoteRunnerEnabled);
        Assert.True(fetched.UiAutomationEnabled);
        Assert.Equal(2, fetched.MaximumConcurrentJobs);
    }

    [Fact]
    public async Task EncryptedHandoffIsVisibleAndAcknowledgedOnlyByTargetDevice()
    {
        var source = await IssueTokenAsync("device-source", "device");
        var target = await IssueTokenAsync("device-target", "device");
        var intruder = await IssueTokenAsync("device-intruder", "device");
        using var sourceClient = CreateAuthenticatedClient(source.Token);
        using var targetClient = CreateAuthenticatedClient(target.Token);
        using var intruderClient = CreateAuthenticatedClient(intruder.Token);
        const string handoffId = "handoff-client-generated-1";
        var request = Envelope(
            new { handoffId, targetDeviceId = "device-target", sessionId = "session-7" },
            "opaque handoff payload");

        var createdResponse = await sourceClient.PostAsJsonAsync("/api/v1/handoffs", request);
        var created = await createdResponse.Content.ReadFromJsonAsync<HandoffIssued>();
        var replay = await sourceClient.PostAsJsonAsync("/api/v1/handoffs", request);
        var intruderInbox = await intruderClient.GetFromJsonAsync<List<HandoffIssued>>(
            "/api/v1/handoffs");
        var intruderAck = await intruderClient.PostAsync(
            $"/api/v1/handoffs/{created!.HandoffId}/acknowledge",
            content: null);
        var targetInbox = await targetClient.GetFromJsonAsync<List<HandoffIssued>>(
            "/api/v1/handoffs");
        var targetAck = await targetClient.PostAsync(
            $"/api/v1/handoffs/{created.HandoffId}/acknowledge",
            content: null);
        var targetInboxAfterAck = await targetClient.GetFromJsonAsync<List<HandoffIssued>>(
            "/api/v1/handoffs");

        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.Equal(handoffId, created!.HandoffId);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Empty(intruderInbox!);
        Assert.Equal(HttpStatusCode.NotFound, intruderAck.StatusCode);
        var delivered = Assert.Single(targetInbox!);
        Assert.Equal("device-source", delivered.SourceDeviceId);
        Assert.Equal(HttpStatusCode.NoContent, targetAck.StatusCode);
        Assert.Empty(targetInboxAfterAck!);
    }

    [Fact]
    public async Task PluginPublicationRejectsInvalidSignatureAndAcceptsTrustedPublisher()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string publisherKeyId = "publisher-1";
        const string pluginId = "sample.plugin";
        const string version = "1.2.3";
        const string manifest = "{\"id\":\"sample.plugin\",\"version\":\"1.2.3\"}";
        var sha256 = Convert.ToHexString(SHA256.HashData("plugin package"u8.ToArray()))
            .ToLowerInvariant();
        using var adminClient = CreateAuthenticatedClient(BootstrapToken);
        var publisher = await adminClient.PostAsJsonAsync(
            "/api/v1/plugin-publishers",
            new { keyId = publisherKeyId, publicKeyPem = signingKey.ExportSubjectPublicKeyInfoPem() });
        var policy = await adminClient.PutAsJsonAsync(
            "/api/v1/policy",
            new
            {
                allowedExecutionProfiles = new[] { "NativeProtected", "WslStrict" },
                remoteRunnerEnabled = true,
                uiAutomationEnabled = false,
                maximumConcurrentJobs = 4,
                allowedPluginPublishers = new[] { publisherKeyId },
            });
        var service = await IssueTokenAsync("plugin-service", "service");
        using var serviceClient = CreateAuthenticatedClient(service.Token);
        var invalid = await serviceClient.PostAsJsonAsync(
            $"/api/v1/plugins/{pluginId}/versions/{version}",
            new
            {
                manifestJson = manifest,
                sha256,
                publisherKeyId,
                signature = Convert.ToBase64String(new byte[64]),
            });
        var payload = Encoding.UTF8.GetBytes(
            $"{pluginId}\n{version}\n{sha256}\n{manifest}");
        var signature = Convert.ToBase64String(
            signingKey.SignData(payload, HashAlgorithmName.SHA256));
        var published = await serviceClient.PostAsJsonAsync(
            $"/api/v1/plugins/{pluginId}/versions/{version}",
            new { manifestJson = manifest, sha256, publisherKeyId, signature });
        var plugins = await serviceClient.GetFromJsonAsync<List<PluginPublished>>(
            "/api/v1/plugins");

        Assert.Equal(HttpStatusCode.Created, publisher.StatusCode);
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        var publishedPlugin = Assert.Single(plugins!);
        Assert.Equal(pluginId, publishedPlugin.PluginId);
    }

    [Fact]
    public async Task AutomationWorkerQueuesDueEncryptedJobAndAutomationCanBeDisabled()
    {
        var device = await IssueTokenAsync("automation-device", "device");
        var service = await IssueTokenAsync("automation-runner", "service");
        using var deviceClient = CreateAuthenticatedClient(device.Token);
        using var serviceClient = CreateAuthenticatedClient(service.Token);
        using var adminClient = CreateAuthenticatedClient(BootstrapToken);
        await UpdateRunnerPolicyAsync(adminClient, remoteRunnerEnabled: true, maximumConcurrentJobs: 1);
        var registration = await serviceClient.PostAsJsonAsync(
            "/api/v1/runners/automation-runner/register",
            new { capabilities = new[] { "windows" } });
        var createResponse = await deviceClient.PostAsJsonAsync(
            "/api/v1/automations",
            Automation(
                "automation-nightly-1",
                "Nightly review",
                60,
                "windows",
                "scheduled encrypted payload"));
        var automation = await createResponse.Content.ReadFromJsonAsync<AutomationCreated>();
        var secondCreate = await deviceClient.PostAsJsonAsync(
            "/api/v1/automations",
            Automation(
                "automation-nightly-2",
                "Second nightly review",
                60,
                "windows",
                "second scheduled encrypted payload"));
        var secondAutomation = await secondCreate.Content.ReadFromJsonAsync<AutomationCreated>();

        HttpResponseMessage? claim = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!timeout.IsCancellationRequested)
        {
            claim?.Dispose();
            claim = await serviceClient.PostAsJsonAsync(
                "/api/v1/runners/automation-runner/claim",
                new { leaseSeconds = 30 },
                timeout.Token);
            if (claim.StatusCode == HttpStatusCode.OK)
            {
                break;
            }
            await Task.Delay(100, timeout.Token);
        }
        var claimed = await claim!.Content.ReadFromJsonAsync<JobClaimed>();
        await UpdateRunnerPolicyAsync(adminClient, remoteRunnerEnabled: false, maximumConcurrentJobs: 1);
        var completed = await serviceClient.PostAsJsonAsync(
            $"/api/v1/jobs/{claimed!.JobId}/complete",
            RunnerCompletion(
                "automation-runner",
                claimed.LeaseToken!,
                claimed.LeaseGeneration,
                "automation-result",
                claimed.RequiredCapability!,
                claimed.AutomationId,
                claimed.RunId,
                "scheduled encrypted result"));
        await Task.Delay(1_200);
        var automations = await deviceClient.GetFromJsonAsync<List<AutomationCreated>>(
            "/api/v1/automations");
        var disabled = await deviceClient.DeleteAsync(
            $"/api/v1/automations/{automation!.AutomationId}");
        var secondDisabled = await deviceClient.DeleteAsync(
            $"/api/v1/automations/{secondAutomation!.AutomationId}");
        await UpdateRunnerPolicyAsync(adminClient, remoteRunnerEnabled: true, maximumConcurrentJobs: 1);
        var afterReenable = await serviceClient.PostAsJsonAsync(
            "/api/v1/runners/automation-runner/claim",
            new { leaseSeconds = 30 });

        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondCreate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, claim!.StatusCode);
        Assert.Equal("automation", claimed.Kind);
        Assert.Contains(
            claimed.AutomationId,
            new[] { automation.AutomationId, secondAutomation.AutomationId });
        Assert.False(string.IsNullOrWhiteSpace(claimed.RunId));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(2, automations!.Count);
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, secondDisabled.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, afterReenable.StatusCode);
        claim.Dispose();
    }

    [Fact]
    public async Task NotificationHubNegotiationRequiresAuthentication()
    {
        var device = await IssueTokenAsync("notification-device", "device");
        using var anonymousClient = _factory!.CreateClient();
        using var authenticatedClient = CreateAuthenticatedClient(device.Token);

        var anonymous = await anonymousClient.PostAsync(
            "/hubs/notifications/negotiate?negotiateVersion=1",
            content: null);
        var authenticated = await authenticatedClient.PostAsync(
            "/hubs/notifications/negotiate?negotiateVersion=1",
            content: null);
        var queryStringToken = await anonymousClient.PostAsync(
            $"/hubs/notifications/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(device.Token)}",
            content: null);
        var negotiation = await authenticated.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, queryStringToken.StatusCode);
        Assert.Contains("connectionToken", negotiation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedCryptographicPayloadsReturnValidationErrors()
    {
        using var adminClient = CreateAuthenticatedClient(BootstrapToken);
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var device = await IssueTokenAsync("malformed-device", "device");
        var service = await IssueTokenAsync("malformed-service", "service");
        using var deviceClient = CreateAuthenticatedClient(device.Token);
        using var serviceClient = CreateAuthenticatedClient(service.Token);

        var missingKey = await adminClient.PostAsJsonAsync(
            "/api/v1/plugin-publishers",
            new { keyId = "publisher-null", publicKeyPem = (string?)null });
        var exposedPrivateKey = await adminClient.PostAsJsonAsync(
            "/api/v1/plugin-publishers",
            new
            {
                keyId = "publisher-private",
                publicKeyPem = privateKey.ExportPkcs8PrivateKeyPem(),
            });
        var missingEnvelopeField = await deviceClient.PostAsJsonAsync(
            "/api/v1/jobs",
            new
            {
                jobId = "job-malformed-missing",
                kind = "task",
                requiredCapability = "windows",
                automationId = (string?)null,
                runId = (string?)null,
                algorithm = "AES-256-GCM",
                nonce = (string?)null,
                ciphertext = Convert.ToBase64String("encrypted payload with tag"u8.ToArray()),
            });
        var invalidAesNonce = await deviceClient.PostAsJsonAsync(
            "/api/v1/jobs",
            new
            {
                jobId = "job-malformed-nonce",
                kind = "task",
                requiredCapability = "windows",
                automationId = (string?)null,
                runId = (string?)null,
                algorithm = "AES-256-GCM",
                nonce = Convert.ToBase64String(new byte[24]),
                ciphertext = Convert.ToBase64String("encrypted payload with tag"u8.ToArray()),
            });
        var missingPluginFields = await serviceClient.PostAsJsonAsync(
            "/api/v1/plugins/sample.plugin/versions/1.0.0",
            new
            {
                manifestJson = (string?)null,
                sha256 = (string?)null,
                publisherKeyId = "publisher-null",
                signature = (string?)null,
            });

        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, exposedPrivateKey.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingEnvelopeField.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidAesNonce.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingPluginFields.StatusCode);
    }

    private HttpClient Client => _client ?? throw new InvalidOperationException();

    private async Task<TokenIssued> IssueTokenAsync(string subjectId, string role)
    {
        using var adminClient = CreateAuthenticatedClient(BootstrapToken);
        var response = await adminClient.PostAsJsonAsync(
            "/api/v1/tokens",
            new { subjectId, role });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenIssued>())!;
    }

    private HttpClient CreateAuthenticatedClient(string token)
    {
        var client = _factory!.CreateClient();
        Authenticate(client, token);
        return client;
    }

    private static object Envelope(object leadingFields, string plaintext)
    {
        var properties = leadingFields.GetType()
            .GetProperties()
            .ToDictionary(property => property.Name, property => property.GetValue(leadingFields));
        properties["algorithm"] = "AES-256-GCM";
        properties["nonce"] = Convert.ToBase64String(new byte[12]);
        properties["ciphertext"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        return properties;
    }

    private static object RunnerJob(string jobId, string requiredCapability, string plaintext) =>
        Envelope(
            new
            {
                jobId,
                kind = "task",
                requiredCapability,
                automationId = (string?)null,
                runId = (string?)null,
            },
            plaintext);

    private static object RunnerCompletion(
        string runnerId,
        string leaseToken,
        long leaseGeneration,
        string kind,
        string requiredCapability,
        string? automationId,
        string? runId,
        string plaintext) =>
        Envelope(
            new
            {
                runnerId,
                leaseToken,
                leaseGeneration,
                kind,
                requiredCapability,
                automationId,
                runId,
            },
            plaintext);

    private async Task<string?> ReadLeaseTokenHashAsync(string jobId)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_root, "cloud.db")};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT lease_token_hash FROM jobs WHERE team_id = 'default' AND job_id = $jobId;";
        _ = command.Parameters.AddWithValue("$jobId", jobId);
        return await command.ExecuteScalarAsync() as string;
    }

    private async Task ExpireLeaseAsync(string jobId)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_root, "cloud.db")};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE jobs SET lease_expires_at = $expiredAt WHERE team_id = 'default' AND job_id = $jobId;";
        _ = command.Parameters.AddWithValue("$expiredAt", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
        _ = command.Parameters.AddWithValue("$jobId", jobId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<long> ReadLeaseGenerationAsync(string jobId)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_root, "cloud.db")};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT lease_generation FROM jobs WHERE team_id = 'default' AND job_id = $jobId;";
        _ = command.Parameters.AddWithValue("$jobId", jobId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static object Automation(
        string automationId,
        string name,
        int intervalSeconds,
        string requiredCapability,
        string plaintext) =>
        Envelope(
            new
            {
                automationId,
                kind = "automation",
                name,
                intervalSeconds,
                requiredCapability,
            },
            plaintext);

    private static async Task UpdateRunnerPolicyAsync(
        HttpClient adminClient,
        bool remoteRunnerEnabled,
        int maximumConcurrentJobs)
    {
        var response = await adminClient.PutAsJsonAsync(
            "/api/v1/policy",
            new
            {
                allowedExecutionProfiles = new[] { "NativeProtected", "WslStrict" },
                remoteRunnerEnabled,
                uiAutomationEnabled = false,
                maximumConcurrentJobs,
                allowedPluginPublishers = Array.Empty<string>(),
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static void Authenticate(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private sealed record JobCreated(string JobId);

    private sealed record SessionDeleteReceipt(string SessionId, int Revision);

    private sealed record JobClaimed(
        string JobId,
        string? Kind = null,
        string? RequiredCapability = null,
        string? AutomationId = null,
        string? RunId = null,
        string? LeaseToken = null,
        long LeaseGeneration = 0);

    private sealed record TokenIssued(string Token, string SubjectId, string Role);

    private sealed record PolicyResponse(
        int Version,
        string[] AllowedExecutionProfiles,
        bool RemoteRunnerEnabled,
        bool UiAutomationEnabled,
        int MaximumConcurrentJobs,
        string[] AllowedPluginPublishers);

    private sealed record HandoffIssued(string HandoffId, string SourceDeviceId);

    private sealed record PluginPublished(string PluginId);

    private sealed record AutomationCreated(string AutomationId);
}
