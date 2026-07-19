using System.Text.Json;
using System.Text.Json.Serialization;
using AgentDesk.Cloud.Client;

namespace AgentDesk.Platform.Windows.Cloud;

public sealed class JsonCloudConnectionProfileStore : ICloudConnectionProfileStore
{
    private const int CurrentSchemaVersion = 1;
    private const long MaximumSettingsBytes = 64 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _settingsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonCloudConnectionProfileStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentDesk",
            "cloud.json"))
    {
    }

    public JsonCloudConnectionProfileStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public async Task<CloudConnectionProfile> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new CloudConnectionProfile();
            }

            RejectReparsePoint(_settingsPath);
            var information = new FileInfo(_settingsPath);
            if (information.Length is <= 0 or > MaximumSettingsBytes)
            {
                throw new InvalidDataException("Cloud settings are invalid.");
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
                if (document is null || document.SchemaVersion != CurrentSchemaVersion)
                {
                    throw new InvalidDataException("Cloud settings are invalid.");
                }

                if (!document.Enabled)
                {
                    if (document.BaseUri is not null ||
                        document.TeamId is not null ||
                        document.DeviceId is not null)
                    {
                        throw new InvalidDataException("Cloud settings are invalid.");
                    }
                    return new CloudConnectionProfile();
                }

                if (!Uri.TryCreate(document.BaseUri, UriKind.Absolute, out var baseUri) ||
                    document.TeamId is null ||
                    document.DeviceId is null)
                {
                    throw new InvalidDataException("Cloud settings are invalid.");
                }
                return new CloudConnectionProfile(
                    baseUri,
                    document.TeamId,
                    document.DeviceId);
            }
            catch (Exception exception) when (
                exception is JsonException or ArgumentException or NotSupportedException)
            {
                throw new InvalidDataException("Cloud settings are invalid.", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        CloudConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var document = profile.IsLocalOnly
            ? new SettingsDocument(CurrentSchemaVersion, false, null, null, null)
            : new SettingsDocument(
                CurrentSchemaVersion,
                true,
                profile.BaseUri!.AbsoluteUri,
                profile.TeamId,
                profile.DeviceId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = $"{_settingsPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            if (File.Exists(_settingsPath))
            {
                RejectReparsePoint(_settingsPath);
            }
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
                await JsonSerializer
                    .SerializeAsync(stream, document, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (stream.Length > MaximumSettingsBytes)
                {
                    throw new InvalidDataException("Cloud settings are invalid.");
                }
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

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Cloud settings are invalid.");
        }
    }

    private sealed record SettingsDocument(
        int SchemaVersion,
        bool Enabled,
        string? BaseUri,
        string? TeamId,
        string? DeviceId);
}
