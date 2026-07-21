using AgentDesk.App.Settings;

namespace AgentDesk.App.Tests;

public sealed class JsonRecentWorkspaceStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "AgentDesk.RecentWorkspaces.Tests",
        Guid.NewGuid().ToString("N"));

    public JsonRecentWorkspaceStoreTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsExistingDirectoriesMostRecentFirst()
    {
        var first = Path.Combine(_directory, "repo-a");
        var second = Path.Combine(_directory, "repo-b");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var path = Path.Combine(_directory, "recent-workspaces.json");
        var store = new JsonRecentWorkspaceStore(path);

        await store.SaveAsync([second, first, second]);
        var loaded = await store.LoadAsync();

        Assert.Equal([second, first], loaded);
    }

    [Fact]
    public async Task Load_DropsMissingDirectories()
    {
        var existing = Path.Combine(_directory, "still-here");
        Directory.CreateDirectory(existing);
        var missing = Path.Combine(_directory, "gone");
        var path = Path.Combine(_directory, "recent-workspaces.json");
        await File.WriteAllTextAsync(
            path,
            $$"""
            {
              "schemaVersion": 1,
              "paths": [
                {{JsonString(missing)}},
                {{JsonString(existing)}}
              ]
            }
            """);

        var loaded = await new JsonRecentWorkspaceStore(path).LoadAsync();

        Assert.Equal([existing], loaded);
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsEmpty()
    {
        var store = new JsonRecentWorkspaceStore(Path.Combine(_directory, "missing.json"));
        Assert.Empty(await store.LoadAsync());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string JsonString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
