using System.Text.Json;
using AgentDesk.Core.Engine;
using AgentDesk.Core.Execution;

namespace AgentDesk.App.Settings;

public sealed class JsonUiPreferencesStore : IUiPreferencesStore
{
    private const int SchemaVersion = 3;
    private const long MaximumSettingsFileBytes = 1024 * 1024;
    private static readonly HashSet<string> SchemaOneProperties =
    [
        "schemaVersion",
        "language",
        "composerDraft",
        "sessionMode",
        "executionProfile",
    ];
    private static readonly HashSet<string> SchemaTwoProperties =
    [
        .. SchemaOneProperties,
        "notificationsEnabled",
        "windowsAutomationEnabled",
    ];
    private static readonly HashSet<string> SchemaThreeProperties =
    [
        .. SchemaTwoProperties,
        "backgroundUpdateChecksEnabled",
    ];

    private readonly string _settingsPath;

    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentDesk",
        "ui-settings.json");

    public JsonUiPreferencesStore()
        : this(DefaultSettingsPath)
    {
    }

    public JsonUiPreferencesStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public async Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return UiPreferences.Default;
        }

        var file = new FileInfo(_settingsPath);
        if (file.Length <= 0 || file.Length > MaximumSettingsFileBytes)
        {
            throw new InvalidDataException("The UI settings file has an invalid size.");
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
                throw new InvalidDataException("The UI settings file must contain an object.");
            }
            var schemaVersion = root.GetProperty("schemaVersion").GetInt32();
            if (schemaVersion is not (1 or 2 or SchemaVersion))
            {
                throw new InvalidDataException("The UI settings schema version is not supported.");
            }
            ValidateProperties(
                root,
                schemaVersion switch
                {
                    1 => SchemaOneProperties,
                    2 => SchemaTwoProperties,
                    _ => SchemaThreeProperties,
                });

            return new UiPreferences(
                RequiredString(root, "language"),
                RequiredStringAllowEmpty(root, "composerDraft"),
                RequiredString(root, "sessionMode") switch
                {
                    "default" => SessionMode.Default,
                    "plan" => SessionMode.Plan,
                    _ => throw new InvalidDataException("The saved session mode is not supported."),
                },
                RequiredString(root, "executionProfile") switch
                {
                    "NativeProtected" => ExecutionProfile.NativeProtected,
                    "WslStrict" => ExecutionProfile.WslStrict,
                    _ => throw new InvalidDataException(
                        "The saved execution profile is not supported."),
                },
                schemaVersion >= 2 && RequiredBoolean(root, "notificationsEnabled"),
                schemaVersion >= 2 && RequiredBoolean(root, "windowsAutomationEnabled"),
                schemaVersion == SchemaVersion &&
                    RequiredBoolean(root, "backgroundUpdateChecksEnabled"))
                .Validate();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The UI settings file is not valid JSON.", exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidDataException("The UI settings file is incomplete.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("The UI settings file contains an invalid value.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The UI settings file contains invalid preferences.", exception);
        }
    }

    public async Task SaveAsync(
        UiPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _ = preferences.Validate();
        var directory = Path.GetDirectoryName(_settingsPath) ?? throw new InvalidOperationException(
            "The UI settings path has no parent directory.");
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
                        language = preferences.Language,
                        composerDraft = preferences.ComposerDraft,
                        sessionMode = preferences.SessionMode is SessionMode.Plan ? "plan" : "default",
                        executionProfile = preferences.ExecutionProfile.ToString(),
                        notificationsEnabled = preferences.NotificationsEnabled,
                        windowsAutomationEnabled = preferences.WindowsAutomationEnabled,
                        backgroundUpdateChecksEnabled =
                            preferences.BackgroundUpdateChecksEnabled,
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

    private static void ValidateProperties(
        JsonElement root,
        IReadOnlySet<string> allowedProperties)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name) || !allowedProperties.Contains(property.Name))
            {
                throw new InvalidDataException(
                    "The UI settings file contains duplicate or unsupported fields.");
            }
        }
        if (names.Count != allowedProperties.Count)
        {
            throw new InvalidDataException("The UI settings file is incomplete.");
        }
    }

    private static string RequiredString(JsonElement root, string name)
    {
        var value = RequiredStringAllowEmpty(root, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"The UI settings '{name}' value is empty.")
            : value;
    }

    private static string RequiredStringAllowEmpty(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind is JsonValueKind.String && value.GetString() is { } text
            ? text
            : throw new InvalidDataException($"The UI settings '{name}' value is invalid.");
    }

    private static bool RequiredBoolean(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidDataException($"The UI settings '{name}' value is invalid.");
    }
}
