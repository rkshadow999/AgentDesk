using System.Text.Json;

namespace AgentDesk.App.Settings;

public sealed class JsonRecentWorkspaceStore : IRecentWorkspaceStore
{
    public const int SchemaVersion = 1;
    public const int MaximumEntries = 12;
    public const int MaximumPathCharacters = 32_767;
    private const long MaximumSettingsFileBytes = 256 * 1024;

    private static readonly HashSet<string> AllowedProperties =
    [
        "schemaVersion",
        "paths",
    ];

    private readonly string _settingsPath;

    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentDesk",
        "recent-workspaces.json");

    public JsonRecentWorkspaceStore()
        : this(DefaultSettingsPath)
    {
    }

    public JsonRecentWorkspaceStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public async Task<IReadOnlyList<string>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return [];
        }

        var file = new FileInfo(_settingsPath);
        if (file.Length <= 0 || file.Length > MaximumSettingsFileBytes)
        {
            throw new InvalidDataException("The recent workspaces file has an invalid size.");
        }

        try
        {
            await using var stream = new FileStream(
                _settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                throw new InvalidDataException("The recent workspaces file must contain an object.");
            }

            ValidateProperties(root);
            if (root.GetProperty("schemaVersion").GetInt32() != SchemaVersion)
            {
                throw new InvalidDataException(
                    "The recent workspaces schema version is not supported.");
            }

            if (root.GetProperty("paths").ValueKind is not JsonValueKind.Array)
            {
                throw new InvalidDataException("The recent workspaces path list is invalid.");
            }

            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root.GetProperty("paths").EnumerateArray())
            {
                if (paths.Count >= MaximumEntries)
                {
                    break;
                }

                if (item.ValueKind is not JsonValueKind.String ||
                    item.GetString() is not { Length: > 0 } raw ||
                    raw.Length > MaximumPathCharacters ||
                    raw.Any(char.IsControl))
                {
                    throw new InvalidDataException("A recent workspace path is invalid.");
                }

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(raw);
                }
                catch (Exception exception) when (exception is ArgumentException or
                    NotSupportedException or PathTooLongException)
                {
                    throw new InvalidDataException("A recent workspace path is invalid.", exception);
                }

                if (!seen.Add(fullPath))
                {
                    continue;
                }

                // Drop entries that no longer exist so the UI never offers dead paths.
                if (!Directory.Exists(fullPath))
                {
                    continue;
                }

                paths.Add(fullPath);
            }

            return paths;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The recent workspaces file is not valid JSON.",
                exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidDataException(
                "The recent workspaces file is incomplete.",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                "The recent workspaces file contains an invalid value.",
                exception);
        }
    }

    public async Task SaveAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var normalized = Normalize(paths);
        var directory = Path.GetDirectoryName(_settingsPath) ??
            throw new InvalidOperationException("The recent workspaces path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new
                    {
                        schemaVersion = SchemaVersion,
                        paths = normalized,
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static IReadOnlyList<string> Normalize(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (normalized.Count >= MaximumEntries)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(path) ||
                path.Length > MaximumPathCharacters ||
                path.Any(char.IsControl))
            {
                throw new ArgumentException("A workspace path is invalid.", nameof(paths));
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception exception) when (exception is ArgumentException or
                NotSupportedException or PathTooLongException)
            {
                throw new ArgumentException("A workspace path is invalid.", nameof(paths), exception);
            }

            if (seen.Add(fullPath))
            {
                normalized.Add(fullPath);
            }
        }

        return normalized;
    }

    private static void ValidateProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name) || !AllowedProperties.Contains(property.Name))
            {
                throw new InvalidDataException(
                    "The recent workspaces file contains duplicate or unsupported fields.");
            }
        }

        if (names.Count != AllowedProperties.Count)
        {
            throw new InvalidDataException("The recent workspaces file is incomplete.");
        }
    }
}
