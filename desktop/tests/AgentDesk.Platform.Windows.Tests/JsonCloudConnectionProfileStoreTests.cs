using AgentDesk.Cloud.Client;
using AgentDesk.Platform.Windows.Cloud;

namespace AgentDesk.Platform.Windows.Tests;

public sealed class JsonCloudConnectionProfileStoreTests
{
    [Fact]
    public async Task SaveAndLoadRoundTripsOnlyNonSecretCloudSettings()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "cloud.json");
        var store = new JsonCloudConnectionProfileStore(path);
        var profile = new CloudConnectionProfile(
            new Uri("https://cloud.example.test/root/"),
            "team-1",
            "device-1");

        await store.SaveAsync(profile);
        var loaded = await store.LoadAsync();

        Assert.False(loaded.IsLocalOnly);
        Assert.Equal(profile.BaseUri, loaded.BaseUri);
        Assert.Equal("team-1", loaded.TeamId);
        Assert.Equal("device-1", loaded.DeviceId);
        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recovery", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingOrExplicitLocalProfileKeepsCloudDisabled()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "cloud.json");
        var store = new JsonCloudConnectionProfileStore(path);

        Assert.True((await store.LoadAsync()).IsLocalOnly);

        await store.SaveAsync(new CloudConnectionProfile());
        Assert.True((await store.LoadAsync()).IsLocalOnly);
    }

    [Fact]
    public async Task LoadRejectsMalformedOrOversizedSettingsWithoutEchoingContent()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "cloud.json");
        var store = new JsonCloudConnectionProfileStore(path);
        await File.WriteAllTextAsync(path, "{ malformed-sensitive-cloud-content");

        var malformed = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.DoesNotContain(
            "malformed-sensitive-cloud-content",
            malformed.Message,
            StringComparison.Ordinal);

        await File.WriteAllBytesAsync(path, new byte[65 * 1024]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("agentdesk-cloud-settings-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
