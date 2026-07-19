using Microsoft.Data.Sqlite;

namespace AgentDesk.Cloud;

internal static class CloudDatabaseMaintenanceCommand
{
    public const string CommandName = "--agentdesk-cloud-database-maintenance";
    private const string CheckpointMode = "checkpoint-and-verify";
    private const string VerifyMode = "verify";
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    public static bool IsRequested(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.Ordinal);

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        if (args.Length != 3 ||
            (args[1] is not CheckpointMode && args[1] is not VerifyMode))
        {
            await error.WriteLineAsync(
                $"Usage: {CommandName} <{CheckpointMode}|{VerifyMode}> <database-path>");
            return 2;
        }

        try
        {
            var databasePath = Path.GetFullPath(args[2]);
            await VerifyHeaderAsync(databasePath, cancellationToken).ConfigureAwait(false);
            await VerifyDatabaseAsync(
                databasePath,
                checkpoint: args[1] is CheckpointMode,
                cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync("{\"status\":\"ok\"}");
            return 0;
        }
        catch (Exception validationError) when (
            validationError is IOException or InvalidDataException or SqliteException)
        {
            await error.WriteLineAsync(
                $"AgentDesk Cloud SQLite integrity validation failed: {validationError.Message}");
            return 1;
        }
    }

    private static async Task VerifyHeaderAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            databasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: SqliteHeader.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[SqliteHeader.Length];
        if (await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) != header.Length ||
            !header.AsSpan().SequenceEqual(SqliteHeader))
        {
            throw new InvalidDataException("The file does not contain the SQLite header.");
        }
    }

    private static async Task VerifyDatabaseAsync(
        string databasePath,
        bool checkpoint,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = checkpoint ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (checkpoint)
        {
            await using var checkpointCommand = connection.CreateCommand();
            checkpointCommand.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpointCommand.CommandTimeout = 30;
            await using var checkpointResult = await checkpointCommand
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await checkpointResult.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                checkpointResult.GetInt32(0) != 0)
            {
                throw new InvalidDataException("The SQLite WAL checkpoint could not obtain an exclusive checkpoint.");
            }
        }

        await using var integrityCommand = connection.CreateCommand();
        integrityCommand.CommandText = "PRAGMA integrity_check;";
        integrityCommand.CommandTimeout = 60;
        await using var rows = await integrityCommand
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var results = new List<string>();
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(rows.GetString(0));
        }
        if (results.Count != 1 || !string.Equals(results[0], "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"PRAGMA integrity_check reported {string.Join("; ", results.Take(8))}.");
        }
    }
}
