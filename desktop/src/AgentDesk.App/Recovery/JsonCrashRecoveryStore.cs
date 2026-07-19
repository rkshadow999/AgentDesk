using System.Globalization;
using System.Text.Json;
using AgentDesk.Core.Engine;
using AgentDesk.Core.Execution;

namespace AgentDesk.App.Recovery;

public sealed class JsonCrashRecoveryStore : ICrashRecoveryStore
{
    private const int SchemaVersion = 2;
    private const int MaximumSessionIdCharacters = 512;
    private const int MaximumWorkspacePathCharacters = 32_767;
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumMarkerAge = TimeSpan.FromDays(30);
    private static readonly HashSet<string> AllowedProperties =
    [
        "schemaVersion",
        "sessionId",
        "workspacePath",
        "executionProfile",
        "sessionMode",
        "providerIdentity",
        "updatedAt",
    ];

    private readonly string _markerPath;
    private readonly TimeProvider _timeProvider;

    public const long MaximumFileBytes = 64 * 1024;

    public static string DefaultMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentDesk",
        "crash-recovery.json");

    public JsonCrashRecoveryStore()
        : this(DefaultMarkerPath)
    {
    }

    public JsonCrashRecoveryStore(string markerPath)
        : this(markerPath, TimeProvider.System)
    {
    }

    public JsonCrashRecoveryStore(string markerPath, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerPath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _markerPath = Path.GetFullPath(markerPath);
        _timeProvider = timeProvider;
    }

    public async Task<CrashRecoveryMarker?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        RejectReparsePoints(_markerPath);
        if (!File.Exists(_markerPath))
        {
            return null;
        }

        var information = new FileInfo(_markerPath);
        if (information.Length is <= 0 or > MaximumFileBytes)
        {
            throw new InvalidDataException("The crash recovery marker has an invalid size.");
        }

        try
        {
            await using var stream = new FileStream(
                _markerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            RejectReparsePoints(_markerPath);
            if (stream.Length != information.Length)
            {
                throw new InvalidDataException("The crash recovery marker changed while opening.");
            }

            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("The crash recovery marker changed while reading.");
            }

            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                throw new InvalidDataException("The crash recovery marker must contain an object.");
            }
            ValidateProperties(root);
            if (root.GetProperty("schemaVersion").GetInt32() != SchemaVersion)
            {
                throw new InvalidDataException(
                    "The crash recovery marker schema version is not supported.");
            }

            var marker = new CrashRecoveryMarker(
                new SessionId(RequiredBoundedString(
                    root,
                    "sessionId",
                    MaximumSessionIdCharacters)),
                ValidateWorkspacePath(RequiredBoundedString(
                    root,
                    "workspacePath",
                    MaximumWorkspacePathCharacters)),
                RequiredBoundedString(root, "executionProfile", 32) switch
                {
                    "NativeProtected" => ExecutionProfile.NativeProtected,
                    "WslStrict" => ExecutionProfile.WslStrict,
                    _ => throw new InvalidDataException(
                        "The crash recovery execution profile is not supported."),
                },
                RequiredBoundedString(root, "sessionMode", 16) switch
                {
                    "default" => SessionMode.Default,
                    "plan" => SessionMode.Plan,
                    _ => throw new InvalidDataException(
                        "The crash recovery session mode is not supported."),
                },
                ParseUpdatedAt(RequiredBoundedString(root, "updatedAt", 64)),
                RequiredBoundedString(
                    root,
                    "providerIdentity",
                    CrashRecoveryProviderIdentity.HexLength));
            return Validate(marker);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The crash recovery marker is not valid JSON.",
                exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidDataException(
                "The crash recovery marker is incomplete.",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                "The crash recovery marker contains an invalid value.",
                exception);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "The crash recovery marker contains an invalid value.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "The crash recovery marker contains invalid state.",
                exception);
        }
    }

    public async Task SaveAsync(
        CrashRecoveryMarker marker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marker);
        marker = Validate(marker);
        var directory = Path.GetDirectoryName(_markerPath) ??
            throw new InvalidOperationException(
                "The crash recovery marker path has no parent directory.");
        RejectReparsePoints(_markerPath);
        Directory.CreateDirectory(directory);
        RejectReparsePoints(_markerPath);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_markerPath)}.{Guid.NewGuid():N}.tmp");

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
                        sessionId = marker.SessionId.Value,
                        workspacePath = marker.WorkspacePath,
                        executionProfile = marker.ExecutionProfile.ToString(),
                        sessionMode = marker.SessionMode is SessionMode.Plan ? "plan" : "default",
                        providerIdentity = marker.ProviderIdentity,
                        updatedAt = marker.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (stream.Length is <= 0 or > MaximumFileBytes)
                {
                    throw new InvalidDataException(
                        "The crash recovery marker has an invalid size.");
                }
            }

            RejectReparsePoints(temporaryPath);
            RejectReparsePoints(_markerPath);
            File.Move(temporaryPath, _markerPath, overwrite: true);
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

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RejectReparsePoints(_markerPath);
        File.Delete(_markerPath);
        return Task.CompletedTask;
    }

    private CrashRecoveryMarker Validate(CrashRecoveryMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker.SessionId);
        var sessionId = marker.SessionId.Value;
        if (string.IsNullOrWhiteSpace(sessionId) ||
            sessionId.Length > MaximumSessionIdCharacters ||
            sessionId.Any(char.IsControl))
        {
            throw new InvalidDataException("The crash recovery session ID is invalid.");
        }
        if (marker.ExecutionProfile is not (
                ExecutionProfile.NativeProtected or ExecutionProfile.WslStrict))
        {
            throw new InvalidDataException(
                "The crash recovery execution profile is not supported.");
        }
        if (marker.SessionMode is not (SessionMode.Default or SessionMode.Plan))
        {
            throw new InvalidDataException("The crash recovery session mode is not supported.");
        }
        if (marker.UpdatedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("The crash recovery timestamp must use UTC.");
        }
        if (!CrashRecoveryProviderIdentity.IsValid(marker.ProviderIdentity))
        {
            throw new InvalidDataException(
                "The crash recovery provider identity is invalid.");
        }

        var now = _timeProvider.GetUtcNow();
        if (marker.UpdatedAt > now + MaximumFutureSkew ||
            marker.UpdatedAt < now - MaximumMarkerAge)
        {
            throw new InvalidDataException(
                "The crash recovery marker is outside the allowed recovery window.");
        }

        var workspacePath = ValidateWorkspacePath(marker.WorkspacePath);
        return marker with { WorkspacePath = workspacePath };
    }

    private static string ValidateWorkspacePath(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) ||
            workspacePath.Length > MaximumWorkspacePathCharacters ||
            workspacePath.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(workspacePath) ||
            workspacePath.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            workspacePath.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            workspacePath.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException("The crash recovery workspace path is invalid.");
        }

        try
        {
            var fullPath = Path.GetFullPath(workspacePath);
            if (string.IsNullOrEmpty(Path.GetPathRoot(fullPath)))
            {
                throw new InvalidDataException(
                    "The crash recovery workspace path is invalid.");
            }
            return fullPath;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(
                "The crash recovery workspace path is invalid.",
                exception);
        }
    }

    private static DateTimeOffset ParseUpdatedAt(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var updatedAt) ||
            updatedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("The crash recovery timestamp is invalid.");
        }
        return updatedAt;
    }

    private static string RequiredBoundedString(
        JsonElement root,
        string name,
        int maximumLength)
    {
        var property = root.GetProperty(name);
        if (property.ValueKind is not JsonValueKind.String ||
            property.GetString() is not { } value ||
            string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"The crash recovery '{name}' value is invalid.");
        }
        return value;
    }

    private static void ValidateProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name) || !AllowedProperties.Contains(property.Name))
            {
                throw new InvalidDataException(
                    "The crash recovery marker contains duplicate or unsupported fields.");
            }
        }
        if (names.Count != AllowedProperties.Count)
        {
            throw new InvalidDataException("The crash recovery marker is incomplete.");
        }
    }

    private static void RejectReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ??
            throw new InvalidDataException("The crash recovery marker path is invalid.");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "The crash recovery marker path contains a reparse point.");
            }
        }
    }
}
