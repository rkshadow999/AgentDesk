using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentDesk.Updater.Core;

public sealed class HighestSeenVersionStore : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HighestSeenVersionStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<SemanticVersion?> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            return await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordAsync(
        SemanticVersion version,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var existing = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null && existing.Value.CompareAgentDeskReleaseTo(version) >= 0)
            {
                return;
            }

            await WriteCoreAsync(version, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<SemanticVersion?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        EnsureNotReparsePoint(_path);
        try
        {
            var bytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length is 0 or > 4096)
            {
                throw new UpdateSecurityException("The highest-seen update state is invalid.");
            }

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 3 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new UpdateSecurityException("The highest-seen update state is invalid.");
            }

            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 2 ||
                properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
                !root.TryGetProperty("schemaVersion", out var schema) ||
                schema.GetInt32() != SchemaVersion ||
                !root.TryGetProperty("highestSeen", out var highest) ||
                !SemanticVersion.TryParse(highest.GetString(), out var version))
            {
                throw new UpdateSecurityException("The highest-seen update state is invalid.");
            }

            return version;
        }
        catch (UpdateSecurityException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or IOException)
        {
            throw new UpdateSecurityException("The highest-seen update state is invalid.", exception);
        }
    }

    private async Task WriteCoreAsync(
        SemanticVersion version,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        EnsureNotReparsePoint(directory);
        var temporaryPath = $"{_path}.tmp-{Guid.NewGuid():N}";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new StateDocument(
                SchemaVersion,
                version.ToString()));
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private async Task<FileStream> AcquireProcessLockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var lockPath = $"{_path}.lock";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UpdateSecurityException("Update state cannot be stored through a reparse point.");
        }
    }

    private sealed record StateDocument(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("highestSeen")] string HighestSeen);
}
