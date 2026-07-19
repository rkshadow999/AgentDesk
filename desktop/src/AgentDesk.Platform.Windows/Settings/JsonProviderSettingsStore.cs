using System.Text.Json;
using System.Text.Json.Serialization;
using AgentDesk.Core.Providers;

namespace AgentDesk.Platform.Windows.Settings;

public sealed class JsonProviderSettingsStore : IProviderSettingsStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _settingsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonProviderSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentDesk",
            "settings.json"))
    {
    }

    public JsonProviderSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public async Task<ProviderProfile?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return null;
            }

            try
            {
                await using var stream = new FileStream(
                    _settingsPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var document = await JsonSerializer
                    .DeserializeAsync<SettingsDocument>(
                        stream,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (document is null ||
                    document.SchemaVersion != CurrentSchemaVersion ||
                    document.Provider is null)
                {
                    throw new InvalidDataException("Provider settings are invalid.");
                }

                return document.Provider;
            }
            catch (Exception exception) when (
                exception is JsonException or ArgumentException or NotSupportedException)
            {
                throw new InvalidDataException("Provider settings are invalid.", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        ProviderProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = $"{_settingsPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(_settingsPath) ??
                throw new InvalidOperationException("The settings directory is invalid."));
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        new SettingsDocument(CurrentSchemaVersion, profile),
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            _gate.Release();
        }
    }

    private sealed record SettingsDocument(
        int SchemaVersion,
        ProviderProfile? Provider);
}
