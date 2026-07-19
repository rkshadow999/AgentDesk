using AgentDesk.Core.Providers;
using AgentDesk.Platform.Windows.Settings;

namespace AgentDesk.Platform.Windows.Tests;

public sealed class JsonProviderSettingsStoreTests
{
    [Fact]
    public async Task SaveAndLoadRoundTripsOnlyNonSecretProviderSettings()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var store = new JsonProviderSettingsStore(path);
        var profile = new ProviderProfile(
            "https://example.com/v1/",
            "grok-4.5",
            ProviderBackend.ChatCompletions);

        await store.SaveAsync(profile);
        var loaded = await store.LoadAsync();

        Assert.Equal(profile, loaded);
        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("{", json.TrimStart()[..1]);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task SaveAndLoadRoundTripsResponsesBackend()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var store = new JsonProviderSettingsStore(path);
        var profile = new ProviderProfile(
            "https://example.com/v1",
            "grok-4.5",
            ProviderBackend.Responses);

        await store.SaveAsync(profile);

        Assert.Equal(profile, await store.LoadAsync());
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"backend\": \"responses\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadDefaultsLegacyDocumentWithoutBackendToChatCompletions()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "provider": {
                "baseUrl": "https://example.com/v1",
                "model": "legacy-model"
              }
            }
            """);
        var store = new JsonProviderSettingsStore(path);

        var profile = Assert.IsType<ProviderProfile>(await store.LoadAsync());

        Assert.Equal(ProviderBackend.ChatCompletions, profile.Backend);
        Assert.False(profile.AllowInsecureTransport);
    }

    [Fact]
    public async Task LoadReturnsNullForMissingSettings()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonProviderSettingsStore(
            Path.Combine(directory.Path, "settings.json"));

        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task LoadRejectsMalformedSettingsWithoutEchoingTheirContents()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, "{ malformed-secret-content");
        var store = new JsonProviderSettingsStore(path);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

        Assert.DoesNotContain("malformed-secret-content", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("agentdesk-settings-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
