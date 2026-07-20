using System.Text.Json;
using AgentDesk.App.Settings;
using AgentDesk.Core.Engine;
using AgentDesk.Core.Execution;

namespace AgentDesk.App.Tests;

public sealed class JsonUiPreferencesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "AgentDesk.UiPreferences.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_RoundTripsVersionedNonSecretUiState()
    {
        var path = Path.Combine(_directory, "ui-settings.json");
        var store = new JsonUiPreferencesStore(path);
        var preferences = new UiPreferences(
            "en-US",
            "continue the review",
            SessionMode.Plan,
            ExecutionProfile.WslStrict,
            NotificationsEnabled: true,
            WindowsAutomationEnabled: true,
            BackgroundUpdateChecksEnabled: true,
            FullAccessEnabled: true,
            FontScalePercent: 125);

        await store.SaveAsync(preferences);
        var loaded = await store.LoadAsync();

        Assert.Equal(preferences, loaded);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(4, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(document.RootElement.GetProperty("notificationsEnabled").GetBoolean());
        Assert.True(document.RootElement.GetProperty("windowsAutomationEnabled").GetBoolean());
        Assert.True(document.RootElement.GetProperty("backgroundUpdateChecksEnabled").GetBoolean());
        Assert.True(document.RootElement.GetProperty("fullAccessEnabled").GetBoolean());
        Assert.Equal(125, document.RootElement.GetProperty("fontScalePercent").GetInt32());
        Assert.DoesNotContain("secret", await File.ReadAllTextAsync(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_MigratesSchemaOneWithSensitiveFeaturesDisabled()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "ui-settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "language": "zh-CN",
              "composerDraft": "",
              "sessionMode": "default",
              "executionProfile": "NativeProtected"
            }
            """);

        var loaded = await new JsonUiPreferencesStore(path).LoadAsync();

        Assert.False(loaded.NotificationsEnabled);
        Assert.False(loaded.WindowsAutomationEnabled);
        Assert.False(loaded.BackgroundUpdateChecksEnabled);
        Assert.False(loaded.FullAccessEnabled);
        Assert.Equal(110, loaded.FontScalePercent);
    }

    [Fact]
    public async Task Load_MigratesSchemaTwoWithBackgroundUpdateChecksDisabled()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "ui-settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 2,
              "language": "zh-CN",
              "composerDraft": "",
              "sessionMode": "default",
              "executionProfile": "NativeProtected",
              "notificationsEnabled": true,
              "windowsAutomationEnabled": true
            }
            """);

        var loaded = await new JsonUiPreferencesStore(path).LoadAsync();

        Assert.True(loaded.NotificationsEnabled);
        Assert.True(loaded.WindowsAutomationEnabled);
        Assert.False(loaded.BackgroundUpdateChecksEnabled);
        Assert.False(loaded.FullAccessEnabled);
        Assert.Equal(110, loaded.FontScalePercent);
    }

    [Fact]
    public async Task Load_MigratesSchemaThreeWithFullAccessDisabledAndComfortableFonts()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "ui-settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 3,
              "language": "zh-CN",
              "composerDraft": "",
              "sessionMode": "default",
              "executionProfile": "NativeProtected",
              "notificationsEnabled": true,
              "windowsAutomationEnabled": false,
              "backgroundUpdateChecksEnabled": true
            }
            """);

        var loaded = await new JsonUiPreferencesStore(path).LoadAsync();

        Assert.True(loaded.BackgroundUpdateChecksEnabled);
        Assert.False(loaded.FullAccessEnabled);
        Assert.Equal(110, loaded.FontScalePercent);
    }

    [Theory]
    [InlineData(89)]
    [InlineData(105)]
    [InlineData(141)]
    public async Task Save_RejectsUnsupportedFontScales(int fontScalePercent)
    {
        var store = new JsonUiPreferencesStore(Path.Combine(_directory, "ui-settings.json"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveAsync(UiPreferences.Default with { FontScalePercent = fontScalePercent }));
    }

    [Fact]
    public async Task Load_RejectsUnknownFieldsAndOverlongDrafts()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "ui-settings.json");
        await File.WriteAllTextAsync(
            path,
            $$"""
            {
              "schemaVersion": 1,
              "language": "zh-CN",
              "composerDraft": "{{new string('x', UiPreferences.MaximumComposerDraftLength + 1)}}",
              "sessionMode": "default",
              "executionProfile": "NativeProtected",
              "unknown": true
            }
            """);

        var store = new JsonUiPreferencesStore(path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
