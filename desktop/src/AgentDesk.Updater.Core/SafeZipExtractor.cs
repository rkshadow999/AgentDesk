using System.Buffers;
using System.IO.Compression;

namespace AgentDesk.Updater.Core;

public sealed record ZipExtractionLimits(
    int MaximumEntries = 20_000,
    long MaximumEntryBytes = 512L * 1024 * 1024,
    long MaximumTotalBytes = 2L * 1024 * 1024 * 1024)
{
    public void Validate()
    {
        if (MaximumEntries <= 0 ||
            MaximumEntryBytes <= 0 ||
            MaximumTotalBytes <= 0 ||
            MaximumEntryBytes > MaximumTotalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(ZipExtractionLimits));
        }
    }
}

public sealed class SafeZipExtractor
{
    private const int MaximumRelativePathLength = 512;
    private static readonly HashSet<string> ReservedDeviceNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly ZipExtractionLimits _limits;

    public SafeZipExtractor(ZipExtractionLimits? limits = null)
    {
        _limits = limits ?? new ZipExtractionLimits();
        _limits.Validate();
    }

    public async Task ExtractAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var destination = Path.GetFullPath(destinationDirectory);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new UpdateSecurityException("The extraction destination must not already exist.");
        }

        var createdFiles = new List<string>();
        var createdDirectories = new List<string>();
        try
        {
            await using var archiveStream = new FileStream(
                Path.GetFullPath(archivePath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            var entries = BuildExtractionPlan(archive);

            Directory.CreateDirectory(destination);
            createdDirectories.Add(destination);
            foreach (var planned in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outputPath = Path.Combine(destination, planned.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                EnsureContained(destination, outputPath);
                if (planned.IsDirectory)
                {
                    if (!Directory.Exists(outputPath))
                    {
                        Directory.CreateDirectory(outputPath);
                        createdDirectories.Add(outputPath);
                    }

                    continue;
                }

                var parent = Path.GetDirectoryName(outputPath)!;
                CreateDirectories(parent, destination, createdDirectories);
                await using var input = await planned.Entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(
                    outputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                createdFiles.Add(outputPath);
                var copied = await CopyBoundedAsync(
                    input,
                    output,
                    planned.Entry.Length,
                    cancellationToken).ConfigureAwait(false);
                if (copied != planned.Entry.Length)
                {
                    throw new UpdateSecurityException("A ZIP entry length does not match its directory record.");
                }
            }
        }
        catch (UpdateSecurityException)
        {
            CleanupCreatedPaths(createdFiles, createdDirectories);
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            CleanupCreatedPaths(createdFiles, createdDirectories);
            throw new UpdateSecurityException("The update ZIP archive is invalid or unsafe.", exception);
        }
    }

    private IReadOnlyList<PlannedEntry> BuildExtractionPlan(ZipArchive archive)
    {
        if (archive.Entries.Count is 0 || archive.Entries.Count > _limits.MaximumEntries)
        {
            throw new UpdateSecurityException("The update ZIP entry count exceeds its permitted limit.");
        }

        var planned = new List<PlannedEntry>(archive.Entries.Count);
        var paths = new Dictionary<string, PathRecord>(StringComparer.OrdinalIgnoreCase);
        long totalLength = 0;
        foreach (var entry in archive.Entries)
        {
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            EnsureRegularEntry(entry, isDirectory);
            if (isDirectory && entry.Length != 0)
            {
                throw new UpdateSecurityException("A ZIP directory entry contains unexpected data.");
            }

            var relativePath = NormalizeRelativePath(entry.FullName, isDirectory);
            if (!isDirectory)
            {
                if (entry.Length < 0 || entry.Length > _limits.MaximumEntryBytes)
                {
                    throw new UpdateSecurityException("A ZIP entry exceeds its permitted size.");
                }

                totalLength = checked(totalLength + entry.Length);
                if (totalLength > _limits.MaximumTotalBytes)
                {
                    throw new UpdateSecurityException("The update ZIP exceeds its total extraction limit.");
                }
            }

            RegisterPath(paths, relativePath, isDirectory);
            planned.Add(new PlannedEntry(entry, relativePath, isDirectory));
        }

        return planned;
    }

    private static string NormalizeRelativePath(string value, bool isDirectory)
    {
        var normalized = value.Replace('\\', '/');
        if (isDirectory)
        {
            normalized = normalized.TrimEnd('/');
        }

        if (normalized.Length is 0 or > MaximumRelativePathLength ||
            normalized.StartsWith('/') ||
            Path.IsPathRooted(normalized) ||
            normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new UpdateSecurityException("A ZIP entry path is unsafe.");
        }

        var segments = normalized.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length is 0 or > 255 ||
                segment is "." or ".." ||
                segment.EndsWith('.') ||
                segment.EndsWith(' ') ||
                segment.Any(character =>
                    char.IsControl(character) || character is '<' or '>' or '"' or '|' or '?' or '*'))
            {
                throw new UpdateSecurityException("A ZIP entry path is unsafe.");
            }

            var deviceCandidate = segment.Split('.')[0];
            if (ReservedDeviceNames.Contains(deviceCandidate))
            {
                throw new UpdateSecurityException("A ZIP entry uses a reserved Windows device name.");
            }
        }

        return string.Join('/', segments);
    }

    private static void RegisterPath(
        IDictionary<string, PathRecord> paths,
        string path,
        bool isDirectory)
    {
        var segments = path.Split('/');
        var current = string.Empty;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            current = current.Length == 0 ? segments[index] : $"{current}/{segments[index]}";
            if (paths.TryGetValue(current, out var existing))
            {
                if (!existing.IsDirectory || existing.OriginalPath != current)
                {
                    throw new UpdateSecurityException("The ZIP contains colliding Windows paths.");
                }
            }
            else
            {
                paths.Add(current, new PathRecord(current, IsDirectory: true, IsImplicit: true));
            }
        }

        if (paths.TryGetValue(path, out var record))
        {
            if (!isDirectory || !record.IsDirectory || record.OriginalPath != path || !record.IsImplicit)
            {
                throw new UpdateSecurityException("The ZIP contains colliding Windows paths.");
            }

            paths[path] = record with { IsImplicit = false };
        }
        else
        {
            paths.Add(path, new PathRecord(path, isDirectory, IsImplicit: false));
        }
    }

    private static void EnsureRegularEntry(ZipArchiveEntry entry, bool isDirectory)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        var expectedUnixType = isDirectory ? 0x4000 : 0x8000;
        if ((windowsAttributes & FileAttributes.ReparsePoint) != 0 ||
            unixType != 0 && unixType != expectedUnixType)
        {
            throw new UpdateSecurityException("The update ZIP contains a link or special file.");
        }
    }

    private static async Task<long> CopyBoundedAsync(
        Stream input,
        Stream output,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return total;
                }

                total = checked(total + read);
                if (total > maximumBytes)
                {
                    throw new UpdateSecurityException("A ZIP entry exceeds its declared size.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void CreateDirectories(
        string directory,
        string root,
        ICollection<string> createdDirectories)
    {
        var missing = new Stack<string>();
        var current = directory;
        while (!Directory.Exists(current))
        {
            EnsureContained(root, current);
            missing.Push(current);
            current = Path.GetDirectoryName(current)!;
        }

        while (missing.TryPop(out var path))
        {
            Directory.CreateDirectory(path);
            createdDirectories.Add(path);
        }
    }

    private static void EnsureContained(string root, string candidate)
    {
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateSecurityException("A ZIP entry escaped the extraction root.");
        }
    }

    private static void CleanupCreatedPaths(
        IEnumerable<string> createdFiles,
        IEnumerable<string> createdDirectories)
    {
        foreach (var file in createdFiles.Reverse())
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (var directory in createdDirectories.Reverse())
        {
            try
            {
                Directory.Delete(directory, recursive: false);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record PlannedEntry(
        ZipArchiveEntry Entry,
        string RelativePath,
        bool IsDirectory);

    private sealed record PathRecord(
        string OriginalPath,
        bool IsDirectory,
        bool IsImplicit);
}
