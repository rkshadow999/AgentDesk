using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AgentDesk.Cloud;

internal sealed class CloudStore(IOptions<CloudOptions> options)
{
    private readonly string _databasePath = Path.GetFullPath(options.Value.DatabasePath);
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            await using var connection = await OpenRawAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;

                CREATE TABLE IF NOT EXISTS sync_envelopes (
                    team_id TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision > 0),
                    algorithm TEXT NOT NULL,
                    nonce TEXT NOT NULL,
                    ciphertext TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY(team_id, session_id)
                );

                CREATE TABLE IF NOT EXISTS sync_tombstones (
                    team_id TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision > 0),
                    deleted_at TEXT NOT NULL,
                    PRIMARY KEY(team_id, session_id)
                );

                CREATE TABLE IF NOT EXISTS auth_tokens (
                    token_hash TEXT PRIMARY KEY,
                    team_id TEXT NOT NULL,
                    subject_id TEXT NOT NULL,
                    role TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    revoked INTEGER NOT NULL DEFAULT 0 CHECK(revoked IN (0, 1))
                );

                CREATE TABLE IF NOT EXISTS runners (
                    team_id TEXT NOT NULL,
                    runner_id TEXT NOT NULL,
                    capabilities_json TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL,
                    PRIMARY KEY(team_id, runner_id)
                );

                CREATE TABLE IF NOT EXISTS jobs (
                    team_id TEXT NOT NULL,
                    job_id TEXT NOT NULL,
                    status TEXT NOT NULL,
                    kind TEXT NOT NULL DEFAULT 'task',
                    required_capability TEXT NOT NULL,
                    automation_id TEXT NULL,
                    run_id TEXT NULL,
                    payload_binding TEXT NOT NULL DEFAULT 'runner-v2',
                    algorithm TEXT NOT NULL,
                    nonce TEXT NOT NULL,
                    ciphertext TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    lease_owner TEXT NULL,
                    lease_expires_at TEXT NULL,
                    lease_token_hash TEXT NULL,
                    lease_generation INTEGER NOT NULL DEFAULT 0 CHECK(lease_generation >= 0),
                    result_algorithm TEXT NULL,
                    result_nonce TEXT NULL,
                    result_ciphertext TEXT NULL,
                    completed_at TEXT NULL,
                    PRIMARY KEY(team_id, job_id)
                );
                CREATE INDEX IF NOT EXISTS ix_jobs_claim
                    ON jobs(team_id, status, required_capability, created_at);

                CREATE TABLE IF NOT EXISTS team_policies (
                    team_id TEXT PRIMARY KEY,
                    policy_json TEXT NOT NULL,
                    version INTEGER NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS handoffs (
                    team_id TEXT NOT NULL,
                    handoff_id TEXT NOT NULL,
                    source_device_id TEXT NOT NULL,
                    target_device_id TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    algorithm TEXT NOT NULL,
                    nonce TEXT NOT NULL,
                    ciphertext TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    acknowledged_at TEXT NULL,
                    PRIMARY KEY(team_id, handoff_id)
                );
                CREATE INDEX IF NOT EXISTS ix_handoffs_target
                    ON handoffs(team_id, target_device_id, acknowledged_at, created_at);

                CREATE TABLE IF NOT EXISTS plugin_publishers (
                    team_id TEXT NOT NULL,
                    key_id TEXT NOT NULL,
                    public_key_pem TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY(team_id, key_id)
                );

                CREATE TABLE IF NOT EXISTS plugins (
                    team_id TEXT NOT NULL,
                    plugin_id TEXT NOT NULL,
                    version TEXT NOT NULL,
                    manifest_json TEXT NOT NULL,
                    sha256 TEXT NOT NULL,
                    publisher_key_id TEXT NOT NULL,
                    signature TEXT NOT NULL,
                    published_at TEXT NOT NULL,
                    PRIMARY KEY(team_id, plugin_id, version)
                );

                CREATE TABLE IF NOT EXISTS automations (
                    team_id TEXT NOT NULL,
                    automation_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    interval_seconds INTEGER NOT NULL CHECK(interval_seconds >= 60),
                    required_capability TEXT NOT NULL,
                    algorithm TEXT NOT NULL,
                    nonce TEXT NOT NULL,
                    ciphertext TEXT NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0, 1)),
                    next_run_at TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY(team_id, automation_id)
                );
                """;
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(
                connection,
                "jobs",
                "kind",
                "TEXT NOT NULL DEFAULT 'task'",
                cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(
                connection,
                "jobs",
                "automation_id",
                "TEXT NULL",
                cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(
                connection,
                "jobs",
                "run_id",
                "TEXT NULL",
                cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(
                connection,
                "jobs",
                "payload_binding",
                "TEXT NULL",
                cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(
                connection,
                "jobs",
                "lease_token_hash",
                "TEXT NULL",
                cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(
                connection,
                "jobs",
                "lease_generation",
                "INTEGER NOT NULL DEFAULT 0",
                cancellationToken).ConfigureAwait(false);
            await QuarantineLegacyJobsAsync(connection, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            return Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) == 1;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> CreateTokenAsync(
        string teamId,
        string subjectId,
        string role,
        CancellationToken cancellationToken = default)
    {
        var token = "ad_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO auth_tokens (token_hash, team_id, subject_id, role, created_at)
            VALUES ($tokenHash, $teamId, $subjectId, $role, $createdAt);
            """;
        Add(command, "$tokenHash", TokenHash(token));
        Add(command, "$teamId", teamId);
        Add(command, "$subjectId", subjectId);
        Add(command, "$role", role);
        Add(command, "$createdAt", Timestamp(DateTimeOffset.UtcNow));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return token;
    }

    public async Task<CloudIdentity?> AuthenticateTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT team_id, subject_id, role
            FROM auth_tokens
            WHERE token_hash = $tokenHash AND revoked = 0;
            """;
        Add(command, "$tokenHash", TokenHash(token));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CloudIdentity(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task RevokeTokensAsync(
        string teamId,
        string subjectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE auth_tokens SET revoked = 1 WHERE team_id = $teamId AND subject_id = $subjectId;";
        Add(command, "$teamId", teamId);
        Add(command, "$subjectId", subjectId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TeamPolicy> GetPolicyAsync(
        string teamId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT policy_json FROM team_policies WHERE team_id = $teamId;";
        Add(command, "$teamId", teamId);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null
            ? DefaultPolicy()
            : JsonSerializer.Deserialize<TeamPolicy>(json) ?? DefaultPolicy();
    }

    public async Task<TeamPolicy> UpdatePolicyAsync(
        string teamId,
        TeamPolicyUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var existing = await ReadPolicyAsync(
                connection,
                (SqliteTransaction)transaction,
                teamId,
                cancellationToken).ConfigureAwait(false);
            var updated = new TeamPolicy(
                checked(existing.Version + 1),
                request.AllowedExecutionProfiles,
                request.RemoteRunnerEnabled,
                request.UiAutomationEnabled,
                request.MaximumConcurrentJobs,
                request.AllowedPluginPublishers);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO team_policies (team_id, policy_json, version, updated_at)
                VALUES ($teamId, $policyJson, $version, $updatedAt)
                ON CONFLICT(team_id) DO UPDATE SET
                    policy_json = excluded.policy_json,
                    version = excluded.version,
                    updated_at = excluded.updated_at;
                """;
            Add(command, "$teamId", teamId);
            Add(command, "$policyJson", JsonSerializer.Serialize(updated));
            Add(command, "$version", updated.Version);
            Add(command, "$updatedAt", Timestamp(DateTimeOffset.UtcNow));
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (!updated.RemoteRunnerEnabled)
            {
                await using var disable = connection.CreateCommand();
                disable.Transaction = (SqliteTransaction)transaction;
                disable.CommandText =
                    "UPDATE automations SET enabled = 0 WHERE team_id = $teamId AND enabled = 1;";
                Add(disable, "$teamId", teamId);
                _ = await disable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<SessionWriteResult> PutSessionAsync(
        string teamId,
        string sessionId,
        EncryptedEnvelopeRequest envelope,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var read = connection.CreateCommand();
            read.Transaction = (SqliteTransaction)transaction;
            read.CommandText =
                """
                SELECT
                    (SELECT revision FROM sync_envelopes
                     WHERE team_id = $teamId AND session_id = $sessionId),
                    (SELECT revision FROM sync_tombstones
                     WHERE team_id = $teamId AND session_id = $sessionId);
                """;
            Add(read, "$teamId", teamId);
            Add(read, "$sessionId", sessionId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var existingRevision = reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0);
            var tombstoneRevision = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
            await reader.DisposeAsync().ConfigureAwait(false);
            var highWater = Math.Max(existingRevision.GetValueOrDefault(), tombstoneRevision.GetValueOrDefault());
            var expectedRevision = (long)highWater + 1;
            if (envelope.Revision != expectedRevision)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return SessionWriteResult.RevisionConflict;
            }

            var now = Timestamp(DateTimeOffset.UtcNow);
            await using var write = connection.CreateCommand();
            write.Transaction = (SqliteTransaction)transaction;
            write.CommandText =
                """
                INSERT INTO sync_envelopes (
                    team_id, session_id, revision, algorithm, nonce, ciphertext, updated_at)
                VALUES ($teamId, $sessionId, $revision, $algorithm, $nonce, $ciphertext, $updatedAt)
                ON CONFLICT(team_id, session_id) DO UPDATE SET
                    revision = excluded.revision,
                    algorithm = excluded.algorithm,
                    nonce = excluded.nonce,
                    ciphertext = excluded.ciphertext,
                    updated_at = excluded.updated_at;
                """;
            Add(write, "$teamId", teamId);
            Add(write, "$sessionId", sessionId);
            Add(write, "$revision", envelope.Revision);
            Add(write, "$algorithm", envelope.Algorithm);
            Add(write, "$nonce", envelope.Nonce);
            Add(write, "$ciphertext", envelope.Ciphertext);
            Add(write, "$updatedAt", now);
            _ = await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var clearTombstone = connection.CreateCommand();
            clearTombstone.Transaction = (SqliteTransaction)transaction;
            clearTombstone.CommandText =
                "DELETE FROM sync_tombstones WHERE team_id = $teamId AND session_id = $sessionId;";
            Add(clearTombstone, "$teamId", teamId);
            Add(clearTombstone, "$sessionId", sessionId);
            _ = await clearTombstone.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existingRevision is null ? SessionWriteResult.Created : SessionWriteResult.Updated;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<EncryptedEnvelopeResponse?> GetSessionAsync(
        string teamId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT revision, algorithm, nonce, ciphertext, updated_at
            FROM sync_envelopes
            WHERE team_id = $teamId AND session_id = $sessionId;
            """;
        Add(command, "$teamId", teamId);
        Add(command, "$sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return new EncryptedEnvelopeResponse(
            sessionId,
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseTimestamp(reader.GetString(4)));
    }

    public async Task<int?> GetSessionTombstoneRevisionAsync(
        string teamId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT revision FROM sync_tombstones " +
            "WHERE team_id = $teamId AND session_id = $sessionId;";
        Add(command, "$teamId", teamId);
        Add(command, "$sessionId", sessionId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long revision ? checked((int)revision) : null;
    }

    public async Task<SessionDeleteResult> DeleteSessionAsync(
        string teamId,
        string sessionId,
        int knownRevision,
        CancellationToken cancellationToken = default)
    {
        if (knownRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(knownRevision));
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var read = connection.CreateCommand();
            read.Transaction = (SqliteTransaction)transaction;
            read.CommandText =
                """
                SELECT
                    (SELECT revision FROM sync_envelopes
                     WHERE team_id = $teamId AND session_id = $sessionId),
                    (SELECT revision FROM sync_tombstones
                     WHERE team_id = $teamId AND session_id = $sessionId);
                """;
            Add(read, "$teamId", teamId);
            Add(read, "$sessionId", sessionId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var existingRevision = reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0);
            var tombstoneRevision = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
            await reader.DisposeAsync().ConfigureAwait(false);

            if (existingRevision is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                if (tombstoneRevision is null)
                {
                    return new SessionDeleteResult(SessionDeleteStatus.NotFound);
                }
                return knownRevision == tombstoneRevision ||
                    (long)knownRevision + 1 == tombstoneRevision
                    ? new SessionDeleteResult(
                        SessionDeleteStatus.AlreadyDeleted,
                        tombstoneRevision)
                    : new SessionDeleteResult(SessionDeleteStatus.RevisionConflict);
            }
            if (knownRevision != existingRevision)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new SessionDeleteResult(SessionDeleteStatus.RevisionConflict);
            }

            var highWater = Math.Max(existingRevision.Value, tombstoneRevision.GetValueOrDefault());
            var deletionRevision = checked(highWater + 1);
            var deletedAt = Timestamp(DateTimeOffset.UtcNow);

            await using var deleteEnvelope = connection.CreateCommand();
            deleteEnvelope.Transaction = (SqliteTransaction)transaction;
            deleteEnvelope.CommandText =
                "DELETE FROM sync_envelopes WHERE team_id = $teamId AND session_id = $sessionId;";
            Add(deleteEnvelope, "$teamId", teamId);
            Add(deleteEnvelope, "$sessionId", sessionId);
            _ = await deleteEnvelope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var writeTombstone = connection.CreateCommand();
            writeTombstone.Transaction = (SqliteTransaction)transaction;
            writeTombstone.CommandText =
                """
                INSERT INTO sync_tombstones (team_id, session_id, revision, deleted_at)
                VALUES ($teamId, $sessionId, $revision, $deletedAt)
                ON CONFLICT(team_id, session_id) DO UPDATE SET
                    revision = excluded.revision,
                    deleted_at = excluded.deleted_at;
                """;
            Add(writeTombstone, "$teamId", teamId);
            Add(writeTombstone, "$sessionId", sessionId);
            Add(writeTombstone, "$revision", deletionRevision);
            Add(writeTombstone, "$deletedAt", deletedAt);
            _ = await writeTombstone.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SessionDeleteResult(SessionDeleteStatus.Deleted, deletionRevision);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<HandoffCreateResult> CreateHandoffAsync(
        string teamId,
        string sourceDeviceId,
        HandoffCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var createdAt = DateTimeOffset.UtcNow;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR IGNORE INTO handoffs (
                    team_id, handoff_id, source_device_id, target_device_id, session_id,
                    algorithm, nonce, ciphertext, created_at)
                VALUES (
                    $teamId, $handoffId, $sourceDeviceId, $targetDeviceId, $sessionId,
                    $algorithm, $nonce, $ciphertext, $createdAt);
                """;
            Add(command, "$teamId", teamId);
            Add(command, "$handoffId", request.HandoffId);
            Add(command, "$sourceDeviceId", sourceDeviceId);
            Add(command, "$targetDeviceId", request.TargetDeviceId);
            Add(command, "$sessionId", request.SessionId);
            Add(command, "$algorithm", request.Algorithm);
            Add(command, "$nonce", request.Nonce);
            Add(command, "$ciphertext", request.Ciphertext);
            Add(command, "$createdAt", Timestamp(createdAt));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return new HandoffCreateResult(HandoffCreateStatus.Duplicate);
            }
            return new HandoffCreateResult(
                HandoffCreateStatus.Created,
                new HandoffResponse(
                    request.HandoffId,
                    sourceDeviceId,
                    request.TargetDeviceId,
                    request.SessionId,
                    request.Algorithm,
                    request.Nonce,
                    request.Ciphertext,
                    createdAt));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<HandoffResponse>> ListHandoffsAsync(
        string teamId,
        string targetDeviceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT handoff_id, source_device_id, target_device_id, session_id,
                   algorithm, nonce, ciphertext, created_at
            FROM handoffs
            WHERE team_id = $teamId
              AND target_device_id = $targetDeviceId
              AND acknowledged_at IS NULL
            ORDER BY created_at ASC
            LIMIT $limit;
            """;
        Add(command, "$teamId", teamId);
        Add(command, "$targetDeviceId", targetDeviceId);
        Add(command, "$limit", limit);
        var results = new List<HandoffResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(
                new HandoffResponse(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    ParseTimestamp(reader.GetString(7))));
        }
        return results;
    }

    public async Task<bool> AcknowledgeHandoffAsync(
        string teamId,
        string targetDeviceId,
        string handoffId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE handoffs
            SET acknowledged_at = $acknowledgedAt
            WHERE team_id = $teamId
              AND target_device_id = $targetDeviceId
              AND handoff_id = $handoffId
              AND acknowledged_at IS NULL;
            """;
        Add(command, "$acknowledgedAt", Timestamp(DateTimeOffset.UtcNow));
        Add(command, "$teamId", teamId);
        Add(command, "$targetDeviceId", targetDeviceId);
        Add(command, "$handoffId", handoffId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task UpsertPublisherAsync(
        string teamId,
        string keyId,
        string publicKeyPem,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO plugin_publishers (team_id, key_id, public_key_pem, created_at)
            VALUES ($teamId, $keyId, $publicKeyPem, $createdAt)
            ON CONFLICT(team_id, key_id) DO UPDATE SET
                public_key_pem = excluded.public_key_pem,
                created_at = excluded.created_at;
            """;
        Add(command, "$teamId", teamId);
        Add(command, "$keyId", keyId);
        Add(command, "$publicKeyPem", publicKeyPem);
        Add(command, "$createdAt", Timestamp(DateTimeOffset.UtcNow));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetPublisherKeyAsync(
        string teamId,
        string keyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT public_key_pem FROM plugin_publishers WHERE team_id = $teamId AND key_id = $keyId;";
        Add(command, "$teamId", teamId);
        Add(command, "$keyId", keyId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    public async Task<PluginRecord> PublishPluginAsync(
        string teamId,
        string pluginId,
        string version,
        PluginPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        var publishedAt = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO plugins (
                team_id, plugin_id, version, manifest_json, sha256,
                publisher_key_id, signature, published_at)
            VALUES (
                $teamId, $pluginId, $version, $manifestJson, $sha256,
                $publisherKeyId, $signature, $publishedAt);
            """;
        Add(command, "$teamId", teamId);
        Add(command, "$pluginId", pluginId);
        Add(command, "$version", version);
        Add(command, "$manifestJson", request.ManifestJson);
        Add(command, "$sha256", request.Sha256);
        Add(command, "$publisherKeyId", request.PublisherKeyId);
        Add(command, "$signature", request.Signature);
        Add(command, "$publishedAt", Timestamp(publishedAt));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new PluginRecord(
            pluginId,
            version,
            request.ManifestJson,
            request.Sha256,
            request.PublisherKeyId,
            request.Signature,
            publishedAt);
    }

    public async Task<IReadOnlyList<PluginRecord>> ListPluginsAsync(
        string teamId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT plugin_id, version, manifest_json, sha256, publisher_key_id, signature, published_at
            FROM plugins
            WHERE team_id = $teamId
            ORDER BY published_at DESC
            LIMIT $limit;
            """;
        Add(command, "$teamId", teamId);
        Add(command, "$limit", limit);
        var results = new List<PluginRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(
                new PluginRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    ParseTimestamp(reader.GetString(6))));
        }
        return results;
    }

    public async Task<AutomationCreateResult> CreateAutomationAsync(
        string teamId,
        AutomationCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var policy = await ReadPolicyAsync(
                connection,
                (SqliteTransaction)transaction,
                teamId,
                cancellationToken).ConfigureAwait(false);
            if (!policy.RemoteRunnerEnabled)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new AutomationCreateResult(AutomationCreateStatus.RemoteRunnerDisabled);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT OR IGNORE INTO automations (
                    team_id, automation_id, name, interval_seconds, required_capability,
                    algorithm, nonce, ciphertext, enabled, next_run_at, created_at)
                VALUES (
                    $teamId, $automationId, $name, $intervalSeconds, $requiredCapability,
                    $algorithm, $nonce, $ciphertext, 1, $nextRunAt, $createdAt);
                """;
            Add(command, "$teamId", teamId);
            Add(command, "$automationId", request.AutomationId);
            Add(command, "$name", request.Name);
            Add(command, "$intervalSeconds", request.IntervalSeconds);
            Add(command, "$requiredCapability", request.RequiredCapability);
            Add(command, "$algorithm", request.Algorithm);
            Add(command, "$nonce", request.Nonce);
            Add(command, "$ciphertext", request.Ciphertext);
            Add(command, "$nextRunAt", Timestamp(now));
            Add(command, "$createdAt", Timestamp(now));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new AutomationCreateResult(AutomationCreateStatus.Duplicate);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AutomationCreateResult(
                AutomationCreateStatus.Created,
                new AutomationRecord(
                    request.AutomationId,
                    request.Name,
                    request.IntervalSeconds,
                    Enabled: true,
                    now));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<AutomationRecord>> ListAutomationsAsync(
        string teamId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT automation_id, name, interval_seconds, enabled, next_run_at
            FROM automations
            WHERE team_id = $teamId
            ORDER BY created_at DESC;
            """;
        Add(command, "$teamId", teamId);
        var results = new List<AutomationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(
                new AutomationRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3) == 1,
                    ParseTimestamp(reader.GetString(4))));
        }
        return results;
    }

    public async Task<bool> DisableAutomationAsync(
        string teamId,
        string automationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE automations SET enabled = 0 WHERE team_id = $teamId AND automation_id = $automationId;";
        Add(command, "$teamId", teamId);
        Add(command, "$automationId", automationId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyList<(string TeamId, string JobId)>> RunDueAutomationsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var due = new List<DueAutomation>();
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText =
                    """
                    WITH ranked AS (
                        SELECT team_id, automation_id, interval_seconds, required_capability,
                               algorithm, nonce, ciphertext, next_run_at,
                               ROW_NUMBER() OVER (
                                   PARTITION BY team_id
                                   ORDER BY next_run_at ASC, automation_id ASC) AS team_rank
                        FROM automations
                        WHERE enabled = 1 AND next_run_at <= $now
                    )
                    SELECT team_id, automation_id, interval_seconds, required_capability,
                           algorithm, nonce, ciphertext
                    FROM ranked
                    ORDER BY team_rank ASC, next_run_at ASC, team_id ASC, automation_id ASC
                    LIMIT 100;
                    """;
                Add(command, "$now", Timestamp(now));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    due.Add(
                        new DueAutomation(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetInt32(2),
                            reader.GetString(3),
                            reader.GetString(4),
                            reader.GetString(5),
                            reader.GetString(6)));
                }
            }

            var queued = new List<(string TeamId, string JobId)>();
            foreach (var automation in due)
            {
                await using (var update = connection.CreateCommand())
                {
                    update.Transaction = (SqliteTransaction)transaction;
                    update.CommandText =
                        """
                        UPDATE automations
                        SET next_run_at = $nextRunAt
                        WHERE team_id = $teamId AND automation_id = $automationId AND enabled = 1;
                        """;
                    Add(update, "$nextRunAt", Timestamp(now.AddSeconds(automation.IntervalSeconds)));
                    Add(update, "$teamId", automation.TeamId);
                    Add(update, "$automationId", automation.AutomationId);
                    _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                var policy = await ReadPolicyAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    automation.TeamId,
                    cancellationToken).ConfigureAwait(false);
                if (!policy.RemoteRunnerEnabled ||
                    await CountOutstandingJobsAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        automation.TeamId,
                        cancellationToken).ConfigureAwait(false) >= policy.MaximumConcurrentJobs)
                {
                    continue;
                }

                var jobId = Guid.CreateVersion7().ToString();
                var runId = Guid.CreateVersion7().ToString();
                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = (SqliteTransaction)transaction;
                    insert.CommandText =
                        """
                        INSERT INTO jobs (
                            team_id, job_id, status, kind, required_capability,
                            automation_id, run_id, payload_binding, algorithm, nonce, ciphertext,
                            created_at)
                        VALUES (
                            $teamId, $jobId, 'queued', $kind, $capability,
                            $automationId, $runId, $payloadBinding, $algorithm, $nonce, $ciphertext,
                            $createdAt);
                        """;
                    Add(insert, "$teamId", automation.TeamId);
                    Add(insert, "$jobId", jobId);
                    Add(insert, "$kind", RunnerPayloadKinds.Automation);
                    Add(insert, "$capability", automation.RequiredCapability);
                    Add(insert, "$automationId", automation.AutomationId);
                    Add(insert, "$runId", runId);
                    Add(insert, "$payloadBinding", RunnerPayloadBindings.Current);
                    Add(insert, "$algorithm", automation.Algorithm);
                    Add(insert, "$nonce", automation.Nonce);
                    Add(insert, "$ciphertext", automation.Ciphertext);
                    Add(insert, "$createdAt", Timestamp(now));
                    _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                queued.Add((automation.TeamId, jobId));
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return queued;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task RegisterRunnerAsync(
        string teamId,
        string runnerId,
        IReadOnlyList<string> capabilities,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO runners (team_id, runner_id, capabilities_json, last_seen_at)
            VALUES ($teamId, $runnerId, $capabilities, $lastSeenAt)
            ON CONFLICT(team_id, runner_id) DO UPDATE SET
                capabilities_json = excluded.capabilities_json,
                last_seen_at = excluded.last_seen_at;
            """;
        Add(command, "$teamId", teamId);
        Add(command, "$runnerId", runnerId);
        Add(command, "$capabilities", JsonSerializer.Serialize(capabilities));
        Add(command, "$lastSeenAt", Timestamp(DateTimeOffset.UtcNow));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobQueueResult> QueueJobAsync(
        string teamId,
        JobQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var policy = await ReadPolicyAsync(
                connection,
                (SqliteTransaction)transaction,
                teamId,
                cancellationToken).ConfigureAwait(false);
            if (!policy.RemoteRunnerEnabled)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new JobQueueResult(JobQueueStatus.RemoteRunnerDisabled);
            }
            if (await CountOutstandingJobsAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    teamId,
                    cancellationToken).ConfigureAwait(false) >= policy.MaximumConcurrentJobs)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new JobQueueResult(JobQueueStatus.MaximumConcurrentJobsReached);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT OR IGNORE INTO jobs (
                    team_id, job_id, status, kind, required_capability,
                    automation_id, run_id, payload_binding, algorithm, nonce, ciphertext,
                    created_at)
                VALUES (
                    $teamId, $jobId, 'queued', $kind, $capability,
                    $automationId, $runId, $payloadBinding, $algorithm, $nonce, $ciphertext,
                    $createdAt);
                """;
            Add(command, "$teamId", teamId);
            Add(command, "$jobId", request.JobId);
            Add(command, "$kind", request.Kind);
            Add(command, "$capability", request.RequiredCapability);
            Add(command, "$automationId", request.AutomationId);
            Add(command, "$runId", request.RunId);
            Add(command, "$payloadBinding", RunnerPayloadBindings.Current);
            Add(command, "$algorithm", request.Algorithm);
            Add(command, "$nonce", request.Nonce);
            Add(command, "$ciphertext", request.Ciphertext);
            Add(command, "$createdAt", Timestamp(DateTimeOffset.UtcNow));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new JobQueueResult(JobQueueStatus.Duplicate);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new JobQueueResult(JobQueueStatus.Created, request.JobId);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<JobClaimResult> ClaimJobAsync(
        string teamId,
        string runnerId,
        int leaseSeconds,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var policy = await ReadPolicyAsync(
                connection,
                (SqliteTransaction)transaction,
                teamId,
                cancellationToken).ConfigureAwait(false);
            if (!policy.RemoteRunnerEnabled)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new JobClaimResult(JobClaimStatus.RemoteRunnerDisabled);
            }
            if (await CountActiveLeasesAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    teamId,
                    now,
                    cancellationToken).ConfigureAwait(false) >= policy.MaximumConcurrentJobs)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new JobClaimResult(JobClaimStatus.MaximumConcurrentJobsReached);
            }

            var capabilities = await ReadRunnerCapabilitiesAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    teamId,
                    runnerId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (capabilities.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new JobClaimResult(JobClaimStatus.Empty);
            }

            await using var select = connection.CreateCommand();
            select.Transaction = (SqliteTransaction)transaction;
            var capabilityParameters = capabilities
                .Select((_, index) => $"$capability{index}")
                .ToArray();
            select.CommandText =
                $"""
                SELECT job_id, kind, required_capability, automation_id, run_id,
                       algorithm, nonce, ciphertext
                FROM jobs
                WHERE team_id = $teamId
                  AND payload_binding = $payloadBinding
                  AND required_capability IN ({string.Join(", ", capabilityParameters)})
                  AND (status = 'queued' OR (status = 'leased' AND lease_expires_at < $now))
                ORDER BY created_at ASC
                LIMIT 1;
                """;
            Add(select, "$teamId", teamId);
            Add(select, "$payloadBinding", RunnerPayloadBindings.Current);
            Add(select, "$now", Timestamp(now));
            for (var index = 0; index < capabilities.Count; index++)
            {
                Add(select, capabilityParameters[index], capabilities[index]);
            }
            string? jobId = null;
            string? kind = null;
            string? requiredCapability = null;
            string? automationId = null;
            string? runId = null;
            string? algorithm = null;
            string? nonce = null;
            string? ciphertext = null;
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken)
                             .ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    jobId = reader.GetString(0);
                    kind = reader.GetString(1);
                    requiredCapability = reader.GetString(2);
                    automationId = reader.IsDBNull(3) ? null : reader.GetString(3);
                    runId = reader.IsDBNull(4) ? null : reader.GetString(4);
                    algorithm = reader.GetString(5);
                    nonce = reader.GetString(6);
                    ciphertext = reader.GetString(7);
                }
            }
            if (jobId is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new JobClaimResult(JobClaimStatus.Empty);
            }

            var leaseExpiresAt = now.AddSeconds(leaseSeconds);
            var leaseToken = CreateLeaseToken();
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText =
                """
                UPDATE jobs
                SET status = 'leased',
                    lease_owner = $runnerId,
                    lease_expires_at = $leaseExpiresAt,
                    lease_token_hash = $leaseTokenHash,
                    lease_generation = lease_generation + 1
                WHERE team_id = $teamId AND job_id = $jobId
                RETURNING lease_generation;
                """;
            Add(update, "$runnerId", runnerId);
            Add(update, "$leaseExpiresAt", Timestamp(leaseExpiresAt));
            Add(update, "$leaseTokenHash", TokenHash(leaseToken));
            Add(update, "$teamId", teamId);
            Add(update, "$jobId", jobId);
            var leaseGeneration = Convert.ToInt64(
                await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new JobClaimResult(
                JobClaimStatus.Claimed,
                new JobClaimResponse(
                    jobId,
                    kind!,
                    requiredCapability!,
                    automationId,
                    runId,
                    algorithm!,
                    nonce!,
                    ciphertext!,
                    leaseExpiresAt,
                    leaseToken,
                    leaseGeneration));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> CompleteJobAsync(
        string teamId,
        string jobId,
        JobCompleteRequest request,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var read = connection.CreateCommand();
            read.Transaction = (SqliteTransaction)transaction;
            read.CommandText =
                """
                SELECT status, lease_owner, lease_expires_at, lease_token_hash,
                       lease_generation, kind, required_capability, automation_id, run_id
                FROM jobs
                WHERE team_id = $teamId AND job_id = $jobId;
                """;
            Add(read, "$teamId", teamId);
            Add(read, "$jobId", jobId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
            var status = reader.GetString(0);
            var actualLeaseOwner = reader.IsDBNull(1) ? null : reader.GetString(1);
            var leaseExpiresAt = reader.IsDBNull(2)
                ? (DateTimeOffset?)null
                : ParseTimestamp(reader.GetString(2));
            var leaseTokenHash = reader.IsDBNull(3) ? null : reader.GetString(3);
            var leaseGeneration = reader.GetInt64(4);
            var taskKind = reader.GetString(5);
            var requiredCapability = reader.GetString(6);
            var automationId = reader.IsDBNull(7) ? null : reader.GetString(7);
            var runId = reader.IsDBNull(8) ? null : reader.GetString(8);
            await reader.DisposeAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var expectedResultKind = taskKind switch
            {
                RunnerPayloadKinds.Task => RunnerPayloadKinds.TaskResult,
                RunnerPayloadKinds.Automation => RunnerPayloadKinds.AutomationResult,
                _ => null,
            };
            if (!string.Equals(status, "leased", StringComparison.Ordinal) ||
                !string.Equals(request.RunnerId, actualLeaseOwner, StringComparison.Ordinal) ||
                leaseExpiresAt is null ||
                leaseExpiresAt < now ||
                !FixedTimeHashEquals(leaseTokenHash, TokenHash(request.LeaseToken)) ||
                request.LeaseGeneration != leaseGeneration ||
                expectedResultKind is null ||
                !string.Equals(request.Kind, expectedResultKind, StringComparison.Ordinal) ||
                !string.Equals(
                    request.RequiredCapability,
                    requiredCapability,
                    StringComparison.Ordinal) ||
                !string.Equals(request.AutomationId, automationId, StringComparison.Ordinal) ||
                !string.Equals(request.RunId, runId, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                UPDATE jobs
                SET status = 'completed',
                    result_algorithm = $algorithm,
                    result_nonce = $nonce,
                    result_ciphertext = $ciphertext,
                    completed_at = $completedAt,
                    lease_token_hash = NULL
                WHERE team_id = $teamId
                  AND job_id = $jobId
                  AND status = 'leased'
                  AND lease_owner = $runnerId
                  AND lease_expires_at >= $now
                  AND lease_token_hash = $leaseTokenHash
                  AND lease_generation = $leaseGeneration;
                """;
            Add(command, "$algorithm", request.Algorithm);
            Add(command, "$nonce", request.Nonce);
            Add(command, "$ciphertext", request.Ciphertext);
            Add(command, "$completedAt", Timestamp(now));
            Add(command, "$runnerId", request.RunnerId);
            Add(command, "$now", Timestamp(now));
            Add(command, "$leaseTokenHash", TokenHash(request.LeaseToken));
            Add(command, "$leaseGeneration", request.LeaseGeneration);
            Add(command, "$teamId", teamId);
            Add(command, "$jobId", jobId);
            var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            if (updated)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            return updated;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<List<string>> ReadRunnerCapabilitiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string teamId,
        string runnerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT capabilities_json FROM runners WHERE team_id = $teamId AND runner_id = $runnerId;";
        Add(command, "$teamId", teamId);
        Add(command, "$runnerId", runnerId);
        var raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return raw is null
            ? []
            : JsonSerializer.Deserialize<List<string>>(raw) ?? [];
    }

    private static async Task<TeamPolicy> ReadPolicyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string teamId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT policy_json FROM team_policies WHERE team_id = $teamId;";
        Add(command, "$teamId", teamId);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null
            ? DefaultPolicy()
            : JsonSerializer.Deserialize<TeamPolicy>(json) ?? DefaultPolicy();
    }

    private static async Task<int> CountOutstandingJobsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string teamId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM jobs WHERE team_id = $teamId AND status IN ('queued', 'leased');";
        Add(command, "$teamId", teamId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountActiveLeasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string teamId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM jobs
            WHERE team_id = $teamId AND status = 'leased' AND lease_expires_at >= $now;
            """;
        Add(command, "$teamId", teamId);
        Add(command, "$now", Timestamp(now));
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string declaration,
        CancellationToken cancellationToken)
    {
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = $"PRAGMA table_info(\"{tableName}\");";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
                {
                    return;
                }
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText =
            $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {declaration};";
        _ = await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task QuarantineLegacyJobsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE jobs
            SET payload_binding = $legacyBinding,
                status = CASE
                    WHEN status IN ('queued', 'leased') THEN 'quarantined'
                    ELSE status
                END,
                lease_owner = CASE
                    WHEN status IN ('queued', 'leased') THEN NULL
                    ELSE lease_owner
                END,
                lease_expires_at = CASE
                    WHEN status IN ('queued', 'leased') THEN NULL
                    ELSE lease_expires_at
                END,
                lease_token_hash = CASE
                    WHEN status IN ('queued', 'leased') THEN NULL
                    ELSE lease_token_hash
                END,
                lease_generation = CASE
                    WHEN status IN ('queued', 'leased') THEN 0
                    ELSE lease_generation
                END
            WHERE payload_binding IS NULL;
            """;
        Add(command, "$legacyBinding", RunnerPayloadBindings.LegacyUnbound);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await OpenRawAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenRawAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var timeout = connection.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout = 5000;";
        _ = await timeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string TokenHash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string CreateLeaseToken() =>
        "adl_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool FixedTimeHashEquals(string? storedHash, string candidateHash)
    {
        if (storedHash is null || storedHash.Length != candidateHash.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash),
            Encoding.ASCII.GetBytes(candidateHash));
    }

    private static TeamPolicy DefaultPolicy() => new(
        Version: 0,
        AllowedExecutionProfiles: ["NativeProtected", "WslStrict"],
        RemoteRunnerEnabled: true,
        UiAutomationEnabled: false,
        MaximumConcurrentJobs: 4,
        AllowedPluginPublishers: []);

    private sealed record DueAutomation(
        string TeamId,
        string AutomationId,
        int IntervalSeconds,
        string RequiredCapability,
        string Algorithm,
        string Nonce,
        string Ciphertext);
}
