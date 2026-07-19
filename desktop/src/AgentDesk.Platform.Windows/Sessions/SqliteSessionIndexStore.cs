using System.Globalization;
using AgentDesk.Core.Engine;
using Microsoft.Data.Sqlite;

namespace AgentDesk.Platform.Windows.Sessions;

public sealed class SqliteSessionIndexStore : ISessionIndexStore
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumPageSize = 100;
    private static readonly string[] RequiredBaseColumns =
    [
        "session_id",
        "title",
        "workspace_path",
        "created_at",
        "updated_at",
        "message_count",
    ];
    private static readonly (string Name, string Definition)[] AdditiveColumns =
    [
        ("model_id", "TEXT NULL"),
        ("parent_session_id", "TEXT NULL"),
        ("branch", "TEXT NULL"),
        ("worktree_label", "TEXT NULL"),
        ("source_workspace_path", "TEXT NULL"),
        ("archived", "INTEGER NOT NULL DEFAULT 0 CHECK(archived IN (0, 1))"),
    ];
    private readonly string _databasePath;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public SqliteSessionIndexStore()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentDesk",
                "agentdesk.db"))
    {
    }

    public SqliteSessionIndexStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public async Task UpsertAsync(
        IReadOnlyCollection<SessionSummary> sessions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var session in sessions)
        {
            ArgumentNullException.ThrowIfNull(session);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO session_index (
                    session_id,
                    title,
                    workspace_path,
                    created_at,
                    updated_at,
                    message_count,
                    model_id,
                    parent_session_id,
                    branch,
                    worktree_label,
                    source_workspace_path)
                VALUES (
                    $sessionId,
                    $title,
                    $workspacePath,
                    $createdAt,
                    $updatedAt,
                    $messageCount,
                    $modelId,
                    $parentSessionId,
                    $branch,
                    $worktreeLabel,
                    $sourceWorkspacePath)
                ON CONFLICT(session_id) DO UPDATE SET
                    title = excluded.title,
                    workspace_path = excluded.workspace_path,
                    created_at = excluded.created_at,
                    updated_at = excluded.updated_at,
                    message_count = excluded.message_count,
                    model_id = excluded.model_id,
                    parent_session_id = excluded.parent_session_id,
                    branch = excluded.branch,
                    worktree_label = excluded.worktree_label,
                    source_workspace_path = excluded.source_workspace_path;
                """;
            Add(command, "$sessionId", session.SessionId.Value);
            Add(command, "$title", session.Title);
            Add(command, "$workspacePath", session.WorkspacePath);
            Add(command, "$createdAt", Timestamp(session.CreatedAt));
            Add(command, "$updatedAt", Timestamp(session.UpdatedAt));
            Add(command, "$messageCount", session.MessageCount);
            Add(command, "$modelId", session.ModelId);
            Add(command, "$parentSessionId", session.ParentSessionId);
            Add(command, "$branch", session.Branch);
            Add(command, "$worktreeLabel", session.WorktreeLabel);
            Add(command, "$sourceWorkspacePath", session.SourceWorkspacePath);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionSummary>> SearchAsync(
        string? workspacePath,
        string? query,
        bool archived,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                session_id,
                title,
                workspace_path,
                created_at,
                updated_at,
                message_count,
                model_id,
                parent_session_id,
                branch,
                worktree_label,
                source_workspace_path
            FROM session_index
            WHERE archived = $archived
              AND ($workspacePath IS NULL OR workspace_path = $workspacePath COLLATE NOCASE)
              AND (
                    $query IS NULL
                    OR title LIKE $query ESCAPE '\'
                    OR workspace_path LIKE $query ESCAPE '\'
                    OR COALESCE(branch, '') LIKE $query ESCAPE '\'
                    OR COALESCE(model_id, '') LIKE $query ESCAPE '\')
            ORDER BY updated_at DESC, session_id ASC
            LIMIT $limit OFFSET $offset;
            """;
        Add(command, "$archived", archived ? 1 : 0);
        Add(command, "$workspacePath", string.IsNullOrWhiteSpace(workspacePath) ? null : workspacePath);
        Add(
            command,
            "$query",
            string.IsNullOrEmpty(query) ? null : $"%{EscapeLike(query)}%");
        Add(command, "$limit", limit);
        Add(command, "$offset", offset);

        var results = new List<SessionSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadSession(reader));
        }
        return results;
    }

    public async Task<SessionSummary?> FindByIdAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                session_id,
                title,
                workspace_path,
                created_at,
                updated_at,
                message_count,
                model_id,
                parent_session_id,
                branch,
                worktree_label,
                source_workspace_path
            FROM session_index
            WHERE session_id = $sessionId
            LIMIT 1;
            """;
        Add(command, "$sessionId", sessionId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return ReadSession(reader);
    }

    public async Task<bool> SetArchivedAsync(
        SessionId sessionId,
        bool archived,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE session_index SET archived = $archived WHERE session_id = $sessionId;";
        Add(command, "$archived", archived ? 1 : 0);
        Add(command, "$sessionId", sessionId.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlySet<string>> GetArchivedIdsAsync(
        IReadOnlyCollection<SessionId> sessionIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        if (sessionIds.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        if (sessionIds.Count > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionIds));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var parameterNames = new List<string>(sessionIds.Count);
        var index = 0;
        foreach (var sessionId in sessionIds)
        {
            ArgumentNullException.ThrowIfNull(sessionId);
            var parameterName = $"$session{index++}";
            parameterNames.Add(parameterName);
            Add(command, parameterName, sessionId.Value);
        }
        command.CommandText =
            $"SELECT session_id FROM session_index WHERE archived = 1 AND session_id IN ({string.Join(", ", parameterNames)});";

        var archived = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = archived.Add(reader.GetString(0));
        }
        return archived;
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
            await ConfigureDatabaseAsync(connection, cancellationToken).ConfigureAwait(false);
            await MigrateDatabaseAsync(connection, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private static async Task ConfigureDatabaseAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateDatabaseAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var version = await ReadUserVersionAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"The session index schema version {version} is newer than supported version {CurrentSchemaVersion}.");
        }

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                connection,
                transaction,
                """
                CREATE TABLE IF NOT EXISTS session_index (
                    session_id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    workspace_path TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    message_count INTEGER NOT NULL CHECK(message_count >= 0),
                    model_id TEXT NULL,
                    parent_session_id TEXT NULL,
                    branch TEXT NULL,
                    worktree_label TEXT NULL,
                    source_workspace_path TEXT NULL,
                    archived INTEGER NOT NULL DEFAULT 0 CHECK(archived IN (0, 1))
                );
                """,
                cancellationToken)
            .ConfigureAwait(false);

        var columns = await ReadColumnsAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (RequiredBaseColumns.Any(column => !columns.Contains(column)))
        {
            throw new InvalidDataException("The session index base schema is not supported.");
        }

        if (version == 0)
        {
            foreach (var column in AdditiveColumns)
            {
                if (columns.Contains(column.Name))
                {
                    continue;
                }

                await ExecuteAsync(
                        connection,
                        transaction,
                        $"ALTER TABLE session_index ADD COLUMN {column.Name} {column.Definition};",
                        cancellationToken)
                    .ConfigureAwait(false);
                _ = columns.Add(column.Name);
            }
        }

        var expectedColumns = RequiredBaseColumns
            .Concat(AdditiveColumns.Select(column => column.Name));
        if (expectedColumns.Any(column => !columns.Contains(column)))
        {
            throw new InvalidDataException("The session index schema is incomplete.");
        }

        await ExecuteAsync(
                connection,
                transaction,
                """
                CREATE INDEX IF NOT EXISTS ix_session_index_workspace_updated
                    ON session_index(workspace_path, archived, updated_at DESC);
                PRAGMA user_version = 1;
                """,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(session_index);";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value, string column)
    {
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            return timestamp;
        }
        throw new InvalidDataException($"The session index contains an invalid {column} value.");
    }

    private static string? OptionalString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static SessionSummary ReadSession(SqliteDataReader reader) =>
        new(
            new SessionId(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            ParseTimestamp(reader.GetString(3), "created_at"),
            ParseTimestamp(reader.GetString(4), "updated_at"),
            reader.GetInt32(5),
            ModelId: OptionalString(reader, 6),
            ParentSessionId: OptionalString(reader, 7),
            Branch: OptionalString(reader, 8),
            WorktreeLabel: OptionalString(reader, 9),
            SourceWorkspacePath: OptionalString(reader, 10));

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
