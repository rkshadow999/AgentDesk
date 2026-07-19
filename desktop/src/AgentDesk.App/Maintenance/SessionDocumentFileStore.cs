using System.Security.Cryptography;
using AgentDesk.Core.Engine;
using AgentDesk.Platform.Windows.IO;

namespace AgentDesk.App.Maintenance;

public sealed class SessionDocumentFileStore
{
    private static readonly HashSet<string> WindowsReservedNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    public async Task SaveAsync(
        string destinationPath,
        EngineSessionDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var destination = ValidatePath(destinationPath);
        var directory = Path.GetDirectoryName(destination) ??
            throw new InvalidDataException("The session export destination is invalid.");
        using var directoryGuard = WindowsHandleFileSystem.DirectoryPathGuard.Acquire(
            directory,
            createIfMissing: true);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.tmp-{Guid.NewGuid():N}");
        var bytes = document.ExportUtf8Json();
        using var temporaryHandle = WindowsHandleFileSystem.CreateTemporaryFile(temporaryPath);
        var renamed = false;
        try
        {
            await RandomAccess.WriteAsync(
                    temporaryHandle,
                    bytes,
                    fileOffset: 0,
                    cancellationToken)
                .ConfigureAwait(false);
            RandomAccess.FlushToDisk(temporaryHandle);
            if (RandomAccess.GetLength(temporaryHandle) != bytes.Length)
            {
                throw new IOException("The session export could not be written completely.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            directoryGuard.Validate();
            WindowsHandleFileSystem.AtomicReplace(
                temporaryHandle,
                temporaryPath,
                destination);
            renamed = true;
            directoryGuard.Validate();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (!renamed)
            {
                try
                {
                    WindowsHandleFileSystem.DeleteTemporaryFile(temporaryHandle);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    public async Task<EngineSessionDocument> LoadAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var source = ValidatePath(sourcePath);
        var directory = Path.GetDirectoryName(source) ??
            throw new InvalidDataException("The session import source is invalid.");
        using var directoryGuard = WindowsHandleFileSystem.DirectoryPathGuard.Acquire(
            directory,
            createIfMissing: false);
        using var sourceHandle = WindowsHandleFileSystem.OpenExistingFileForRead(
            source,
            asynchronous: true);
        directoryGuard.Validate();
        var length = RandomAccess.GetLength(sourceHandle);
        if (length is <= 0 or > EngineSessionDocument.MaximumBytes)
        {
            throw new InvalidDataException("The AgentDesk session export size is invalid.");
        }

        var bytes = new byte[checked((int)length)];
        try
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await RandomAccess.ReadAsync(
                        sourceHandle,
                        bytes.AsMemory(offset),
                        fileOffset: offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new InvalidDataException(
                        "The AgentDesk session export changed while reading.");
                }
                offset += read;
            }
            directoryGuard.Validate();

            try
            {
                return EngineSessionDocument.FromUtf8Json(bytes);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The AgentDesk session export is invalid.", exception);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("The AgentDesk session file path is invalid.", exception);
        }

        if (fullPath.Length > 32_767 ||
            fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            fullPath.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The AgentDesk session file path is invalid.");
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new InvalidDataException("The AgentDesk session file path is invalid.");
        }
        var relative = fullPath[root.Length..];
        if (relative.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException("NTFS alternate data streams are not supported.");
        }

        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            ValidateSegment(segment);
        }
        return WindowsHandleFileSystem.ValidateLocalPath(fullPath);
    }

    private static void ValidateSegment(string segment)
    {
        if (segment.Length is 0 or > 255 ||
            segment is "." or ".." ||
            segment.EndsWith(' ') ||
            segment.EndsWith('.') ||
            segment.Any(character => char.IsControl(character) ||
                Path.GetInvalidFileNameChars().Contains(character)))
        {
            throw new InvalidDataException("The AgentDesk session file path is invalid.");
        }

        var deviceName = segment.Split('.', 2)[0].TrimEnd(' ', '.');
        if (WindowsReservedNames.Contains(deviceName))
        {
            throw new InvalidDataException("Windows reserved device names are not supported.");
        }
    }

}
