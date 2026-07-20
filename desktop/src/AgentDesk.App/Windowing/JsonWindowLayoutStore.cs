using System.Text.Json;

namespace AgentDesk.App.Windowing;

public sealed class JsonWindowLayoutStore
{
    private const int SchemaVersion = 1;
    public const int MaximumFileBytes = 64 * 1024;

    private static readonly HashSet<string> SchemaProperties =
    [
        "schemaVersion",
        "inspectorPaneWidth",
    ];

    private readonly string _layoutPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonWindowLayoutStore()
        : this(DefaultLayoutPath)
    {
    }

    public JsonWindowLayoutStore(string layoutPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layoutPath);
        _layoutPath = Path.GetFullPath(layoutPath);
    }

    public static string DefaultLayoutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentDesk",
        "window-layout.json");

    public async Task<WindowLayoutState> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_layoutPath))
            {
                return WindowLayoutState.Default;
            }

            try
            {
                var file = new FileInfo(_layoutPath);
                if (file.Length is <= 0 or > MaximumFileBytes)
                {
                    return WindowLayoutState.Default;
                }

                await using var stream = new FileStream(
                    _layoutPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var document = await JsonDocument.ParseAsync(
                        stream,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return Parse(document.RootElement);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is JsonException or IOException or UnauthorizedAccessException or
                    InvalidDataException or InvalidOperationException or FormatException or
                    OverflowException)
            {
                return WindowLayoutState.Default;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        WindowLayoutState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = state.Normalize();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var directory = Path.GetDirectoryName(_layoutPath) ??
            throw new InvalidOperationException("The window layout path has no parent directory.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_layoutPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
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
                            inspectorPaneWidth = normalized.InspectorPaneWidth,
                        },
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _layoutPath, overwrite: true);
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

            _gate.Release();
        }
    }

    private static WindowLayoutState Parse(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object)
        {
            return WindowLayoutState.Default;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name) || !SchemaProperties.Contains(property.Name))
            {
                return WindowLayoutState.Default;
            }
        }
        if (names.Count != SchemaProperties.Count ||
            !root.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.ValueKind is not JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out var version) ||
            version != SchemaVersion ||
            !root.TryGetProperty("inspectorPaneWidth", out var width) ||
            width.ValueKind is not JsonValueKind.Number ||
            !width.TryGetDouble(out var inspectorPaneWidth) ||
            !double.IsFinite(inspectorPaneWidth))
        {
            return WindowLayoutState.Default;
        }

        return new WindowLayoutState(inspectorPaneWidth).Normalize();
    }
}
