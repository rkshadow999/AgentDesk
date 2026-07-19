using AgentDesk.Cloud.Client;
using Microsoft.Data.Sqlite;

namespace AgentDesk.Platform.Windows.Cloud;

public sealed class SqliteCloudSyncMetadataStore : ICloudSyncMetadataStore
{
    private readonly string _databasePath;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public SqliteCloudSyncMetadataStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentDesk",
            "cloud-sync.db"))
    {
    }

    public SqliteCloudSyncMetadataStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public async ValueTask<CloudConnectionProfile?> ReadProfileAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT is_local_only, base_uri, team_id, device_id FROM cloud_profile WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        if (reader.GetInt32(0) == 1)
        {
            return new CloudConnectionProfile();
        }
        if (reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3) ||
            !Uri.TryCreate(reader.GetString(1), UriKind.Absolute, out var baseUri))
        {
            throw new InvalidDataException("The cloud sync profile is invalid.");
        }
        return new CloudConnectionProfile(baseUri, reader.GetString(2), reader.GetString(3));
    }

    public async ValueTask SaveProfileAsync(
        CloudConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var upsert = connection.CreateCommand();
        upsert.CommandText =
            """
            INSERT INTO cloud_profile (id, is_local_only, base_uri, team_id, device_id)
            VALUES (1, $isLocalOnly, $baseUri, $teamId, $deviceId)
            ON CONFLICT(id) DO UPDATE SET
                is_local_only = excluded.is_local_only,
                base_uri = excluded.base_uri,
                team_id = excluded.team_id,
                device_id = excluded.device_id;
            """;
        Add(upsert, "$isLocalOnly", profile.IsLocalOnly ? 1 : 0);
        Add(upsert, "$baseUri", profile.BaseUri?.AbsoluteUri);
        Add(upsert, "$teamId", profile.TeamId);
        Add(upsert, "$deviceId", profile.DeviceId);
        _ = await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int?> ReadRevisionAsync(
        CloudSyncMetadataScope scope,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        sessionId = ValidateSessionId(sessionId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT revision
            FROM cloud_revision
            WHERE server_scope = $serverScope
              AND team_id = $teamId
              AND session_id = $sessionId;
            """;
        Add(command, "$serverScope", scope.ServerScope);
        Add(command, "$teamId", scope.TeamId);
        Add(command, "$sessionId", sessionId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long revision ? checked((int)revision) : null;
    }

    public async ValueTask SaveRevisionAsync(
        CloudSyncMetadataScope scope,
        string sessionId,
        int revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        sessionId = ValidateSessionId(sessionId);
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO cloud_revision (server_scope, team_id, session_id, revision)
            VALUES ($serverScope, $teamId, $sessionId, $revision)
            ON CONFLICT(server_scope, team_id, session_id)
            DO UPDATE SET revision = excluded.revision
            WHERE excluded.revision >= cloud_revision.revision;
            """;
        Add(command, "$serverScope", scope.ServerScope);
        Add(command, "$teamId", scope.TeamId);
        Add(command, "$sessionId", sessionId);
        Add(command, "$revision", revision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The cloud revision cannot move backwards.");
        }
    }

    public async ValueTask DeleteRevisionAsync(
        CloudSyncMetadataScope scope,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        sessionId = ValidateSessionId(sessionId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM cloud_revision
            WHERE server_scope = $serverScope
              AND team_id = $teamId
              AND session_id = $sessionId;
            """;
        Add(command, "$serverScope", scope.ServerScope);
        Add(command, "$teamId", scope.TeamId);
        Add(command, "$sessionId", sessionId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
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
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = _databasePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Shared,
                    Pooling = false,
                }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;
                CREATE TABLE IF NOT EXISTS cloud_profile (
                    id INTEGER PRIMARY KEY CHECK(id = 1),
                    is_local_only INTEGER NOT NULL CHECK(is_local_only IN (0, 1)),
                    base_uri TEXT NULL,
                    team_id TEXT NULL,
                    device_id TEXT NULL
                );
                """;
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await EnsureRevisionSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private static async Task EnsureRevisionSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText = "PRAGMA table_info(cloud_revision);";
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = columns.Add(reader.GetString(1));
            }
        }

        if (columns.Count == 0)
        {
            await CreateRevisionTableAsync(connection, cancellationToken).ConfigureAwait(false);
            return;
        }

        var currentColumns = new[] { "server_scope", "team_id", "session_id", "revision" };
        if (columns.SetEquals(currentColumns))
        {
            return;
        }

        var legacyColumns = new[] { "session_id", "revision" };
        if (!columns.SetEquals(legacyColumns))
        {
            throw new InvalidDataException("The cloud revision database schema is invalid.");
        }

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var migrate = connection.CreateCommand();
        migrate.Transaction = (SqliteTransaction)transaction;
        migrate.CommandText =
            """
            DROP TABLE IF EXISTS cloud_revision_v2;
            CREATE TABLE cloud_revision_v2 (
                server_scope TEXT NOT NULL,
                team_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK(revision >= 1),
                PRIMARY KEY (server_scope, team_id, session_id)
            );
            INSERT INTO cloud_revision_v2 (server_scope, team_id, session_id, revision)
            SELECT
                COALESCE((SELECT base_uri FROM cloud_profile WHERE id = 1), ''),
                COALESCE((SELECT team_id FROM cloud_profile WHERE id = 1), ''),
                session_id,
                revision
            FROM cloud_revision;
            DROP TABLE cloud_revision;
            ALTER TABLE cloud_revision_v2 RENAME TO cloud_revision;
            """;
        _ = await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CreateRevisionTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var create = connection.CreateCommand();
        create.CommandText =
            """
            CREATE TABLE cloud_revision (
                server_scope TEXT NOT NULL,
                team_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK(revision >= 1),
                PRIMARY KEY (server_scope, team_id, session_id)
            );
            """;
        _ = await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId.Length > 128 || sessionId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The cloud session ID is invalid.", nameof(sessionId));
        }
        return sessionId;
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);

}
