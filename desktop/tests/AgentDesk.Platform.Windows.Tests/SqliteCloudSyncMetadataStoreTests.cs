using AgentDesk.Cloud.Client;
using AgentDesk.Platform.Windows.Cloud;
using Microsoft.Data.Sqlite;

namespace AgentDesk.Platform.Windows.Tests;

public sealed class SqliteCloudSyncMetadataStoreTests
{
    [Fact]
    public async Task ProfileAndMonotonicRevisionsPersistAcrossInstances()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(root, "cloud-sync.db");
            var profile = new CloudConnectionProfile(
                new Uri("https://cloud.example.test/"),
                "team-1",
                "device-1");
            var store = new SqliteCloudSyncMetadataStore(databasePath);
            var scope = CloudSyncMetadataScope.FromProfile(profile);

            await store.SaveProfileAsync(profile);
            await store.SaveRevisionAsync(scope, "session-42", 3);

            var reopened = new SqliteCloudSyncMetadataStore(databasePath);
            var restoredProfile = await reopened.ReadProfileAsync();
            Assert.NotNull(restoredProfile);
            Assert.Equal(profile.BaseUri, restoredProfile.BaseUri);
            Assert.Equal("team-1", restoredProfile.TeamId);
            Assert.Equal("device-1", restoredProfile.DeviceId);
            Assert.Equal(3, await reopened.ReadRevisionAsync(scope, "session-42"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reopened.SaveRevisionAsync(scope, "session-42", 2).AsTask());
            Assert.Equal(3, await reopened.ReadRevisionAsync(scope, "session-42"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RevisionsArePartitionedByServerAndTeamInsteadOfDevice()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var store = new SqliteCloudSyncMetadataStore(Path.Combine(root, "cloud-sync.db"));
            var teamOne = new CloudConnectionProfile(
                new Uri("https://cloud.example.test/"),
                "team-1",
                "device-1");
            var teamOneScope = CloudSyncMetadataScope.FromProfile(teamOne);
            await store.SaveProfileAsync(teamOne);
            await store.SaveRevisionAsync(teamOneScope, "session-42", 7);

            await store.SaveProfileAsync(new CloudConnectionProfile(
                new Uri("https://cloud.example.test/"),
                "team-1",
                "device-2"));

            Assert.Equal(7, await store.ReadRevisionAsync(teamOneScope, "session-42"));

            var teamTwo = new CloudConnectionProfile(
                new Uri("https://cloud.example.test/"),
                "team-2",
                "device-2");
            var teamTwoScope = CloudSyncMetadataScope.FromProfile(teamTwo);
            await store.SaveProfileAsync(teamTwo);

            Assert.Null(await store.ReadRevisionAsync(teamTwoScope, "session-42"));
            Assert.Equal("team-2", (await store.ReadProfileAsync())?.TeamId);

            await store.SaveRevisionAsync(teamTwoScope, "session-42", 3);
            await store.SaveProfileAsync(new CloudConnectionProfile(
                new Uri("https://cloud.example.test/"),
                "team-1",
                "device-3"));

            Assert.Equal(7, await store.ReadRevisionAsync(teamOneScope, "session-42"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CapturedScopePreventsAnOldProfileCompletionWritingIntoTheNewPartition()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var store = new SqliteCloudSyncMetadataStore(Path.Combine(root, "cloud-sync.db"));
            var oldProfile = new CloudConnectionProfile(
                new Uri("https://cloud-a.example.test/"),
                "team-a",
                "device-1");
            var newProfile = new CloudConnectionProfile(
                new Uri("https://cloud-b.example.test/"),
                "team-b",
                "device-2");
            var oldScope = CloudSyncMetadataScope.FromProfile(oldProfile);
            var newScope = CloudSyncMetadataScope.FromProfile(newProfile);
            await store.SaveProfileAsync(oldProfile);

            await store.SaveProfileAsync(newProfile);
            await store.SaveRevisionAsync(oldScope, "session-late", 7);

            Assert.Equal(7, await store.ReadRevisionAsync(oldScope, "session-late"));
            Assert.Null(await store.ReadRevisionAsync(newScope, "session-late"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RevisionsArePartitionedByServerOrigin()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var store = new SqliteCloudSyncMetadataStore(Path.Combine(root, "cloud-sync.db"));
            var serverA = new CloudConnectionProfile(
                new Uri("https://cloud-a.example.test/"),
                "team-1",
                "device-1");
            var serverAScope = CloudSyncMetadataScope.FromProfile(serverA);
            await store.SaveProfileAsync(serverA);
            await store.SaveRevisionAsync(serverAScope, "session-42", 9);

            var serverB = new CloudConnectionProfile(
                new Uri("https://cloud-b.example.test/"),
                "team-1",
                "device-1");
            var serverBScope = CloudSyncMetadataScope.FromProfile(serverB);
            await store.SaveProfileAsync(serverB);

            Assert.Null(await store.ReadRevisionAsync(serverBScope, "session-42"));

            await store.SaveProfileAsync(new CloudConnectionProfile(
                new Uri("https://cloud-a.example.test/"),
                "team-1",
                "device-2"));

            Assert.Equal(9, await store.ReadRevisionAsync(serverAScope, "session-42"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteRevisionIsIdempotent()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var store = new SqliteCloudSyncMetadataStore(Path.Combine(root, "cloud-sync.db"));
            var scope = new CloudSyncMetadataScope(
                new Uri("https://cloud.example.test/"),
                "team-1");
            await store.SaveRevisionAsync(scope, "session-42", 1);

            await store.DeleteRevisionAsync(scope, "session-42");
            await store.DeleteRevisionAsync(scope, "session-42");

            Assert.Null(await store.ReadRevisionAsync(scope, "session-42"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyRevisionSchemaMigratesIntoTheSavedServerAndTeamPartition()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(root, "cloud-sync.db");
            await using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Pooling = false,
                }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE cloud_profile (
                        id INTEGER PRIMARY KEY CHECK(id = 1),
                        is_local_only INTEGER NOT NULL CHECK(is_local_only IN (0, 1)),
                        base_uri TEXT NULL,
                        team_id TEXT NULL,
                        device_id TEXT NULL
                    );
                    INSERT INTO cloud_profile (
                        id, is_local_only, base_uri, team_id, device_id)
                    VALUES (
                        1, 0, 'https://cloud.example.test/', 'team-1', 'device-1');
                    CREATE TABLE cloud_revision (
                        session_id TEXT PRIMARY KEY,
                        revision INTEGER NOT NULL CHECK(revision >= 1)
                    );
                    INSERT INTO cloud_revision (session_id, revision)
                    VALUES ('session-42', 11);
                    """;
                _ = await command.ExecuteNonQueryAsync();
            }

            var store = new SqliteCloudSyncMetadataStore(databasePath);
            var scope = new CloudSyncMetadataScope(
                new Uri("https://cloud.example.test/"),
                "team-1");

            Assert.Equal(11, await store.ReadRevisionAsync(scope, "session-42"));
            await store.SaveProfileAsync(new CloudConnectionProfile(
                new Uri("https://cloud.example.test/"),
                "team-1",
                "device-2"));
            Assert.Equal(11, await store.ReadRevisionAsync(scope, "session-42"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentdesk-cloud-meta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
