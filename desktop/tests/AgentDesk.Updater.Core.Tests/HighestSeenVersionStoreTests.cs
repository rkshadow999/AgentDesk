using AgentDesk.Updater.Core;

namespace AgentDesk.Updater.Core.Tests;

public sealed class HighestSeenVersionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"AgentDesk.Updater.Tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task RecordAsyncPersistsOnlyTheHighestTrustedVersion()
    {
        var path = Path.Combine(_directory, "highest-seen.json");
        var store = new HighestSeenVersionStore(path);

        await store.RecordAsync(SemanticVersion.Parse("2.0.0"));
        await store.RecordAsync(SemanticVersion.Parse("1.5.0"));

        var reloaded = new HighestSeenVersionStore(path);
        Assert.Equal(SemanticVersion.Parse("2.0.0"), await reloaded.GetAsync());
    }

    [Fact]
    public async Task ConcurrentRecordsCannotLoseTheHighestVersion()
    {
        var path = Path.Combine(_directory, "highest-seen.json");
        var store = new HighestSeenVersionStore(path);

        await Task.WhenAll(Enumerable.Range(1, 20).Select(index =>
            store.RecordAsync(SemanticVersion.Parse($"1.0.{index}"))));

        Assert.Equal(SemanticVersion.Parse("1.0.20"), await store.GetAsync());
    }

    [Fact]
    public async Task RecordAsyncUsesAgentDeskReleaseChannelOrder()
    {
        var path = Path.Combine(_directory, "highest-seen.json");
        var store = new HighestSeenVersionStore(path);

        await store.RecordAsync(SemanticVersion.Parse("2.0.0-ci.9999"));
        await store.RecordAsync(SemanticVersion.Parse("2.0.0-alpha.0"));
        await store.RecordAsync(SemanticVersion.Parse("2.0.0-ci.9999"));

        Assert.Equal(
            SemanticVersion.Parse("2.0.0-alpha.0"),
            await store.GetAsync());
    }

    [Fact]
    public async Task RecordAsyncCannotCycleBelowAnUnrecognizedPrerelease()
    {
        var path = Path.Combine(_directory, "highest-seen.json");
        var store = new HighestSeenVersionStore(path);

        await store.RecordAsync(SemanticVersion.Parse("2.0.0-bravo.0"));
        await store.RecordAsync(SemanticVersion.Parse("2.0.0-ci.9999"));
        await store.RecordAsync(SemanticVersion.Parse("2.0.0-alpha.0"));

        Assert.Equal(
            SemanticVersion.Parse("2.0.0-bravo.0"),
            await store.GetAsync());
    }

    [Fact]
    public async Task CorruptStateFailsClosedInsteadOfResettingTheRollbackFloor()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "highest-seen.json");
        await File.WriteAllTextAsync(path, "{not-json");

        await Assert.ThrowsAsync<UpdateSecurityException>(
            () => new HighestSeenVersionStore(path).GetAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
