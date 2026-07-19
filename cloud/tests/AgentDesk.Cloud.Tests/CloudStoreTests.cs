using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AgentDesk.Cloud.Tests;

public sealed class CloudStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"agentdesk-cloud-store-{Guid.NewGuid():N}");

    [Fact]
    public async Task SessionDeleteTombstonePreservesRevisionHighWater()
    {
        Directory.CreateDirectory(_root);
        var store = CreateStore(Path.Combine(_root, "session-tombstone.db"));
        await store.InitializeAsync();
        var created = await store.PutSessionAsync("team-sync", "session-1", Envelope(1));

        var deleted = await store.DeleteSessionAsync("team-sync", "session-1", 1);
        var retried = await store.DeleteSessionAsync("team-sync", "session-1", 1);
        var staleResurrection = await store.PutSessionAsync(
            "team-sync",
            "session-1",
            Envelope(2));
        var recreated = await store.PutSessionAsync("team-sync", "session-1", Envelope(3));

        Assert.Equal(SessionWriteResult.Created, created);
        Assert.Equal(SessionDeleteStatus.Deleted, deleted.Status);
        Assert.Equal(2, deleted.Revision);
        Assert.Equal(SessionDeleteStatus.AlreadyDeleted, retried.Status);
        Assert.Equal(2, retried.Revision);
        Assert.Equal(SessionWriteResult.RevisionConflict, staleResurrection);
        Assert.Equal(SessionWriteResult.Created, recreated);
        Assert.Equal(3, (await store.GetSessionAsync("team-sync", "session-1"))!.Revision);
    }

    [Fact]
    public async Task InitializeQuarantinesLegacyUnboundJobsBeforeTheyConsumeQuota()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "legacy.db");
        await CreateLegacyJobsDatabaseAsync(databasePath, count: 4);
        var store = CreateStore(databasePath);

        await store.InitializeAsync();
        var queued = await store.QueueJobAsync(
            "team-legacy",
            Job("job-runner-v2"));

        Assert.Equal(JobQueueStatus.Created, queued.Status);
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var quarantined = connection.CreateCommand();
        quarantined.CommandText =
            "SELECT COUNT(*) FROM jobs WHERE status = 'quarantined' AND payload_binding = 'legacy-unbound';";
        Assert.Equal(4L, (long)(await quarantined.ExecuteScalarAsync())!);
        await using var current = connection.CreateCommand();
        current.CommandText =
            "SELECT payload_binding FROM jobs WHERE team_id = 'team-legacy' AND job_id = 'job-runner-v2';";
        Assert.Equal("runner-v2", (string?)await current.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CreateAutomationRejectsDisabledRemoteRunnerWithoutPersistingAnEnabledRow()
    {
        Directory.CreateDirectory(_root);
        var store = CreateStore(Path.Combine(_root, "automation-disabled.db"));
        await store.InitializeAsync();
        _ = await store.UpdatePolicyAsync("team-disabled", Policy(remoteRunnerEnabled: false));

        var created = await store.CreateAutomationAsync(
            "team-disabled",
            Automation("automation-disabled"));

        Assert.Equal(AutomationCreateStatus.RemoteRunnerDisabled, created.Status);
        Assert.Empty(await store.ListAutomationsAsync("team-disabled"));
    }

    [Fact]
    public async Task ConcurrentPolicyDisableAndAutomationCreateCannotLeaveAnEnabledAutomation()
    {
        Directory.CreateDirectory(_root);
        var store = CreateStore(Path.Combine(_root, "automation-race.db"));
        await store.InitializeAsync();

        for (var index = 0; index < 20; index++)
        {
            _ = await store.UpdatePolicyAsync("team-race", Policy(remoteRunnerEnabled: true));
            var create = store.CreateAutomationAsync(
                "team-race",
                Automation($"automation-race-{index}"));
            var disable = store.UpdatePolicyAsync(
                "team-race",
                Policy(remoteRunnerEnabled: false));

            await Task.WhenAll(create, disable);

            Assert.False((await store.GetPolicyAsync("team-race")).RemoteRunnerEnabled);
            Assert.All(
                await store.ListAutomationsAsync("team-race"),
                automation => Assert.False(automation.Enabled));
        }
    }

    [Fact]
    public async Task DueAutomationScanCannotBeStarvedByOneHundredQuotaBlockedRows()
    {
        Directory.CreateDirectory(_root);
        var store = CreateStore(Path.Combine(_root, "automation-fairness.db"));
        await store.InitializeAsync();
        _ = await store.UpdatePolicyAsync(
            "team-blocked",
            Policy(remoteRunnerEnabled: true, maximumConcurrentJobs: 1));
        _ = await store.UpdatePolicyAsync(
            "team-ready",
            Policy(remoteRunnerEnabled: true, maximumConcurrentJobs: 1));
        for (var index = 0; index < 100; index++)
        {
            var created = await store.CreateAutomationAsync(
                "team-blocked",
                Automation($"blocked-{index:D3}"));
            Assert.Equal(AutomationCreateStatus.Created, created.Status);
        }
        Assert.Equal(
            JobQueueStatus.Created,
            (await store.QueueJobAsync("team-blocked", Job("quota-holder"))).Status);
        Assert.Equal(
            AutomationCreateStatus.Created,
            (await store.CreateAutomationAsync("team-ready", Automation("ready-1"))).Status);
        var dueAt = DateTimeOffset.UtcNow.AddMinutes(1);

        var queued = await store.RunDueAutomationsAsync(dueAt);

        Assert.Contains(queued, item => item.TeamId == "team-ready");
        Assert.Equal(
            99,
            (await store.ListAutomationsAsync("team-blocked"))
                .Count(automation => automation.NextRunAt > dueAt));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CloudStore CreateStore(string databasePath) => new(
        Options.Create(new CloudOptions
        {
            BootstrapToken = "agentdesk-test-bootstrap-token-000000000000",
            DatabasePath = databasePath,
            RequireHttps = false,
        }));

    private static JobQueueRequest Job(string jobId) => new(
        jobId,
        RunnerPayloadKinds.Task,
        "windows",
        AutomationId: null,
        RunId: null,
        "AES-256-GCM",
        Convert.ToBase64String(new byte[12]),
        Convert.ToBase64String(new byte[16]));

    private static EncryptedEnvelopeRequest Envelope(int revision) => new(
        revision,
        "AES-256-GCM",
        Convert.ToBase64String(new byte[12]),
        Convert.ToBase64String(new byte[16]));

    private static AutomationCreateRequest Automation(string automationId) => new(
        automationId,
        RunnerPayloadKinds.Automation,
        "Nightly review",
        60,
        "windows",
        "AES-256-GCM",
        Convert.ToBase64String(new byte[12]),
        Convert.ToBase64String(new byte[16]));

    private static TeamPolicyUpdateRequest Policy(
        bool remoteRunnerEnabled,
        int maximumConcurrentJobs = 128) => new(
        ["NativeProtected", "WslStrict"],
        remoteRunnerEnabled,
        UiAutomationEnabled: false,
        MaximumConcurrentJobs: maximumConcurrentJobs,
        AllowedPluginPublishers: []);

    private static async Task CreateLegacyJobsDatabaseAsync(string databasePath, int count)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText =
                """
                CREATE TABLE jobs (
                    team_id TEXT NOT NULL,
                    job_id TEXT NOT NULL,
                    status TEXT NOT NULL,
                    required_capability TEXT NOT NULL,
                    algorithm TEXT NOT NULL,
                    nonce TEXT NOT NULL,
                    ciphertext TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    lease_owner TEXT NULL,
                    lease_expires_at TEXT NULL,
                    result_algorithm TEXT NULL,
                    result_nonce TEXT NULL,
                    result_ciphertext TEXT NULL,
                    completed_at TEXT NULL,
                    PRIMARY KEY(team_id, job_id)
                );
                """;
            _ = await schema.ExecuteNonQueryAsync();
        }

        for (var index = 0; index < count; index++)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO jobs (
                    team_id, job_id, status, required_capability, algorithm,
                    nonce, ciphertext, created_at)
                VALUES (
                    'team-legacy', $jobId, 'queued', 'windows', 'AES-256-GCM',
                    $nonce, $ciphertext, $createdAt);
                """;
            _ = insert.Parameters.AddWithValue("$jobId", $"legacy-{index}");
            _ = insert.Parameters.AddWithValue("$nonce", Convert.ToBase64String(new byte[12]));
            _ = insert.Parameters.AddWithValue("$ciphertext", Convert.ToBase64String(new byte[16]));
            _ = insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            _ = await insert.ExecuteNonQueryAsync();
        }
    }
}
