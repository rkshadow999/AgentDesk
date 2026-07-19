using AgentDesk.Core.Engine;
using AgentDesk.Platform.Windows.Sessions;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace AgentDesk.Platform.Windows.Tests;

public sealed class SqliteSessionIndexStoreTests
{
    [Fact]
    public async Task MetadataSearchAndArchiveStatePersistAcrossStoreInstances()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(root, "agentdesk.db");
            var store = new SqliteSessionIndexStore(databasePath);
            var parser = Session("parser", "Fix parser", "C:\\repo", 2);
            var renderer = Session("renderer", "Polish renderer", "C:\\other", 1);

            await store.UpsertAsync([parser, renderer]);

            var matches = await store.SearchAsync(
                "C:\\repo",
                "parser",
                archived: false,
                limit: 20,
                offset: 0);
            Assert.Equal(parser, Assert.Single(matches));
            Assert.True(await store.SetArchivedAsync(parser.SessionId, archived: true));
            Assert.Equal(
                ["parser"],
                await store.GetArchivedIdsAsync([parser.SessionId, renderer.SessionId]));
            Assert.Empty(
                await store.SearchAsync(
                    "C:\\repo",
                    query: null,
                    archived: false,
                    limit: 20,
                    offset: 0));

            var renamed = parser with { Title = "Parser repaired", MessageCount = 3 };
            await store.UpsertAsync([renamed]);
            var reopened = new SqliteSessionIndexStore(databasePath);
            var archived = await reopened.SearchAsync(
                "c:\\REPO",
                "repaired",
                archived: true,
                limit: 20,
                offset: 0);

            Assert.Equal(renamed, Assert.Single(archived));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SearchTreatsSqlWildcardsAsLiteralText()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var store = new SqliteSessionIndexStore(Path.Combine(root, "agentdesk.db"));
            await store.UpsertAsync(
                [
                    Session("percent", "100% ready", "C:\\repo", 1),
                    Session("plain", "1000 ready", "C:\\repo", 1),
                ]);

            var matches = await store.SearchAsync(
                "C:\\repo",
                "%",
                archived: false,
                limit: 20,
                offset: 0);

            Assert.Equal("percent", Assert.Single(matches).SessionId.Value);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FindByIdReturnsArchivedSessionsWithoutScanningTheCatalog()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var store = new SqliteSessionIndexStore(Path.Combine(root, "agentdesk.db"));
            var session = Session("saved-session", "Saved task", "C:\\repo", 3);
            await store.UpsertAsync([session]);
            Assert.True(await store.SetArchivedAsync(session.SessionId, archived: true));

            Assert.Equal(session, await store.FindByIdAsync(session.SessionId));
            Assert.Null(await store.FindByIdAsync(new SessionId("missing-session")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializationMigratesLegacyIndexesAndPreservesRows()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(root, "agentdesk.db");
            await using (var connection = new SqliteConnection(
                             $"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE session_index (
                        session_id TEXT PRIMARY KEY,
                        title TEXT NOT NULL,
                        workspace_path TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL,
                        message_count INTEGER NOT NULL
                    );
                    INSERT INTO session_index (
                        session_id, title, workspace_path, created_at, updated_at, message_count)
                    VALUES (
                        'legacy', 'Legacy task', 'C:\repo',
                        '2026-07-16T08:00:00.0000000+00:00',
                        '2026-07-16T09:00:00.0000000+00:00', 4);
                    """;
                _ = await command.ExecuteNonQueryAsync();
            }

            var store = new SqliteSessionIndexStore(databasePath);
            var migrated = await store.FindByIdAsync(new SessionId("legacy"));

            Assert.NotNull(migrated);
            Assert.Equal("Legacy task", migrated.Title);
            Assert.Null(migrated.ModelId);
            Assert.Equal(1L, await ReadUserVersionAsync(databasePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializationRejectsIndexesFromANewerSchemaVersion()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(root, "agentdesk.db");
            await using (var connection = new SqliteConnection(
                             $"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 2;";
                _ = await command.ExecuteNonQueryAsync();
            }

            var store = new SqliteSessionIndexStore(databasePath);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.SearchAsync(null, null, archived: false, limit: 20, offset: 0));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(101, 0)]
    [InlineData(20, -1)]
    public async Task SearchRejectsUnboundedOrNegativePaging(int limit, int offset)
    {
        var root = NewTemporaryDirectory();
        try
        {
            var store = new SqliteSessionIndexStore(Path.Combine(root, "agentdesk.db"));
            await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() =>
                store.SearchAsync(null, null, archived: false, limit, offset));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TenThousandSessionCatalogRemainsPagedAndInteractive()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var store = new SqliteSessionIndexStore(Path.Combine(root, "agentdesk.db"));
            var sessions = Enumerable.Range(0, 10_000)
                .Select(index => Session(
                    $"session-{index:D5}",
                    index == 9_999 ? "Needle release review" : $"Task {index:D5}",
                    "C:\\large-repo",
                    index % 200))
                .ToArray();

            var writeElapsed = Stopwatch.StartNew();
            await store.UpsertAsync(sessions);
            writeElapsed.Stop();

            var queryElapsed = Stopwatch.StartNew();
            var page = await store.SearchAsync(
                "C:\\large-repo",
                "needle",
                archived: false,
                limit: 100,
                offset: 0);
            queryElapsed.Stop();

            Assert.Equal("session-09999", Assert.Single(page).SessionId.Value);
            Assert.True(
                writeElapsed.Elapsed < TimeSpan.FromSeconds(30),
                $"Indexing 10,000 sessions took {writeElapsed.Elapsed}.");
            Assert.True(
                queryElapsed.Elapsed < TimeSpan.FromSeconds(2),
                $"Searching 10,000 sessions took {queryElapsed.Elapsed}.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SessionSummary Session(
        string id,
        string title,
        string workspacePath,
        int messageCount) =>
        new(
            new SessionId(id),
            title,
            workspacePath,
            DateTimeOffset.Parse("2026-07-16T08:00:00Z"),
            DateTimeOffset.Parse("2026-07-16T09:00:00Z"),
            messageCount,
            ModelId: "grok-4.5",
            Branch: "main");

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentdesk-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<long> ReadUserVersionAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)(await command.ExecuteScalarAsync() ?? -1L);
    }
}
