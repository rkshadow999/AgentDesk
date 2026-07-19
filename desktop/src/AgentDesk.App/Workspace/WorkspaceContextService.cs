using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentDesk.App.Workspace;

public sealed record WorkspaceContextFile(
    string RelativePath,
    long ByteLength,
    DateTimeOffset LastWriteTime);

public interface IWorkspaceContextService
{
    Task<IReadOnlyList<WorkspaceContextFile>> SearchFilesAsync(
        string workspacePath,
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceContextFile>> ListInstructionFilesAsync(
        string workspacePath,
        CancellationToken cancellationToken = default);

    Task<string> ReadTextFileAsync(
        string workspacePath,
        string relativePath,
        CancellationToken cancellationToken = default);

    Task WriteInstructionFileAsync(
        string workspacePath,
        string relativePath,
        string content,
        CancellationToken cancellationToken = default);
}

public sealed class WorkspaceContextService : IWorkspaceContextService
{
    public const int MaximumReadableFileBytes = 512 * 1024;

    private const int MaximumEnumeratedEntries = 100_000;
    private const int MaximumInstructionFiles = 64;
    private const int MaximumSearchResults = 100;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly HashSet<string> IgnoredDirectoryNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".hg",
        ".svn",
        ".vs",
        "bin",
        "node_modules",
        "obj",
        "target",
    };

    private readonly int _maximumEnumeratedEntries;
    private readonly Func<string, IEnumerable<string>> _enumerateEntries;

    public WorkspaceContextService()
        : this(
            MaximumEnumeratedEntries,
            static directory => Directory.EnumerateFileSystemEntries(directory))
    {
    }

    internal WorkspaceContextService(
        int maximumEnumeratedEntries,
        Func<string, IEnumerable<string>> enumerateEntries)
    {
        if (maximumEnumeratedEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEnumeratedEntries));
        }

        _maximumEnumeratedEntries = maximumEnumeratedEntries;
        _enumerateEntries = enumerateEntries ??
            throw new ArgumentNullException(nameof(enumerateEntries));
    }

    public Task<IReadOnlyList<WorkspaceContextFile>> SearchFilesAsync(
        string workspacePath,
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }
        if (query.Length > 512 || query.Any(char.IsControl))
        {
            throw new ArgumentException("The workspace file query is invalid.", nameof(query));
        }
        if (limit is < 1 or > MaximumSearchResults)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var root = ValidateWorkspace(workspacePath);
        return Task.Run<IReadOnlyList<WorkspaceContextFile>>(
            () => EnumerateFiles(root, cancellationToken)
                .Where(item => item.RelativePath.Contains(
                    query,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => PathDepth(item.RelativePath))
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToArray(),
            cancellationToken);
    }

    public Task<IReadOnlyList<WorkspaceContextFile>> ListInstructionFilesAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var root = ValidateWorkspace(workspacePath);
        return Task.Run<IReadOnlyList<WorkspaceContextFile>>(
            () => EnumerateFiles(root, cancellationToken)
                .Where(item => string.Equals(
                    Path.GetFileName(item.RelativePath),
                    "AGENTS.md",
                    StringComparison.Ordinal))
                .OrderBy(item => PathDepth(item.RelativePath))
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumInstructionFiles)
                .ToArray(),
            cancellationToken);
    }

    public async Task<string> ReadTextFileAsync(
        string workspacePath,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var root = ValidateWorkspace(workspacePath);
        using var lease = WindowsWorkspaceFileLease.OpenForRead(
            root,
            relativePath,
            MaximumReadableFileBytes,
            requireInstructionName: string.Equals(
                Path.GetFileName(relativePath),
                "AGENTS.md",
                StringComparison.Ordinal));
        var bytes = await lease.ReadAllBytesAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string content;
            try
            {
                content = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("The workspace file is not valid UTF-8 text.", exception);
            }
            if (content.Length > 0 && content[0] == '\uFEFF')
            {
                content = content[1..];
            }
            if (content.Contains('\0', StringComparison.Ordinal))
            {
                throw new InvalidDataException("The workspace file contains binary data.");
            }
            return content;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task WriteInstructionFileAsync(
        string workspacePath,
        string relativePath,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!string.Equals(
                Path.GetFileName(relativePath),
                "AGENTS.md",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Only an AGENTS.md file can be edited through workspace context.",
                nameof(relativePath));
        }
        if (content.Any(character =>
                character == '\0' ||
                (char.IsControl(character) && character is not ('\r' or '\n' or '\t'))))
        {
            throw new ArgumentException("The instruction content is invalid.", nameof(content));
        }

        var root = ValidateWorkspace(workspacePath);
        var bytes = StrictUtf8.GetBytes(content);
        if (bytes.Length > MaximumReadableFileBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException(
                "The instruction content exceeds the writable size limit.",
                nameof(content));
        }

        try
        {
            try
            {
                using var lease = WindowsWorkspaceFileLease.OpenForWrite(
                    root,
                    relativePath,
                    MaximumReadableFileBytes);
                await lease.ReplaceAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                await WindowsWorkspaceFileLease.CreateInstructionFileAsync(
                        root,
                        relativePath,
                        bytes,
                        MaximumReadableFileBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal IEnumerable<WorkspaceContextFile> EnumerateFiles(
        string root,
        CancellationToken cancellationToken)
    {
        var rootCursor = OpenRootCursor(root);
        if (rootCursor is null)
        {
            yield break;
        }

        var directories = new Stack<WorkspaceDirectoryCursor>();
        directories.Push(rootCursor);
        var visitedEntries = 0;
        try
        {
            while (directories.TryPeek(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (visitedEntries >= _maximumEnumeratedEntries)
                {
                    yield break;
                }
                if (!directory.TryMoveNext(out var entry))
                {
                    directories.Pop().Dispose();
                    continue;
                }
                visitedEntries++;

                string fullEntry;
                FileAttributes attributes;
                try
                {
                    fullEntry = Path.GetFullPath(entry);
                    if (!IsDirectChild(directory.Path, fullEntry))
                    {
                        continue;
                    }
                    attributes = File.GetAttributes(fullEntry);
                }
                catch (Exception exception)
                    when (exception is UnauthorizedAccessException or IOException)
                {
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (IgnoredDirectoryNames.Contains(Path.GetFileName(fullEntry)))
                    {
                        continue;
                    }

                    var child = WorkspaceDirectoryCursor.TryOpenChild(
                        fullEntry,
                        _enumerateEntries);
                    if (child is not null)
                    {
                        directories.Push(child);
                    }
                    continue;
                }

                if (!WorkspaceNativeFileSystem.TryReadFileMetadata(
                        fullEntry,
                        out var byteLength,
                        out var lastWriteTime))
                {
                    continue;
                }
                yield return new WorkspaceContextFile(
                    NormalizeRelativePath(root, fullEntry),
                    byteLength,
                    lastWriteTime);
            }
        }
        finally
        {
            while (directories.TryPop(out var directory))
            {
                directory.Dispose();
            }
        }
    }

    private WorkspaceDirectoryCursor? OpenRootCursor(string root)
    {
        try
        {
            return WorkspaceDirectoryCursor.OpenRoot(root, _enumerateEntries);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static bool IsDirectChild(string directory, string entry)
    {
        var parent = Path.GetDirectoryName(entry);
        return parent is not null && string.Equals(
            Path.TrimEndingDirectorySeparator(parent),
            directory,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidateWorkspace(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The workspace directory does not exist.");
        }
        return root;
    }

    private static string NormalizeRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static int PathDepth(string relativePath) =>
        relativePath.Count(character => character == '/');

    private sealed class WorkspaceDirectoryCursor : IDisposable
    {
        private readonly WorkspaceDirectoryLease _lease;
        private readonly IEnumerator<string> _entries;
        private int _disposed;

        private WorkspaceDirectoryCursor(
            string path,
            WorkspaceDirectoryLease lease,
            IEnumerator<string> entries)
        {
            Path = path;
            _lease = lease;
            _entries = entries;
        }

        internal string Path { get; }

        internal static WorkspaceDirectoryCursor OpenRoot(
            string path,
            Func<string, IEnumerable<string>> enumerateEntries) =>
            Open(path, enumerateEntries, includeAncestors: true);

        internal static WorkspaceDirectoryCursor? TryOpenChild(
            string path,
            Func<string, IEnumerable<string>> enumerateEntries)
        {
            try
            {
                return Open(path, enumerateEntries, includeAncestors: false);
            }
            catch (Exception exception)
                when (exception is UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }

        internal bool TryMoveNext(out string entry)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            try
            {
                if (_entries.MoveNext())
                {
                    entry = _entries.Current;
                    return true;
                }
            }
            catch (Exception exception)
                when (exception is UnauthorizedAccessException or IOException)
            {
            }

            entry = string.Empty;
            return false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _entries.Dispose();
            _lease.Dispose();
        }

        private static WorkspaceDirectoryCursor Open(
            string path,
            Func<string, IEnumerable<string>> enumerateEntries,
            bool includeAncestors)
        {
            var fullPath = System.IO.Path.TrimEndingDirectorySeparator(
                System.IO.Path.GetFullPath(path));
            var lease = includeAncestors
                ? WorkspaceDirectoryLease.AcquireRoot(fullPath)
                : WorkspaceDirectoryLease.AcquireChild(fullPath);
            try
            {
                var entries = enumerateEntries(fullPath).GetEnumerator();
                return new WorkspaceDirectoryCursor(fullPath, lease, entries);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
    }

    private sealed class WorkspaceDirectoryLease : IDisposable
    {
        private readonly List<SafeFileHandle> _handles;
        private int _disposed;

        private WorkspaceDirectoryLease(List<SafeFileHandle> handles)
        {
            _handles = handles;
        }

        internal static WorkspaceDirectoryLease AcquireRoot(string path)
        {
            var root = Path.GetPathRoot(path) ??
                throw new InvalidDataException("The workspace directory path is invalid.");
            var segments = path[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            var paths = new List<string>(Math.Max(segments.Length, 1));
            var current = root;
            if (segments.Length == 0)
            {
                paths.Add(root);
            }
            else
            {
                foreach (var segment in segments)
                {
                    current = Path.Combine(current, segment);
                    paths.Add(current);
                }
            }

            return Acquire(paths);
        }

        internal static WorkspaceDirectoryLease AcquireChild(string path) =>
            Acquire([path]);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            for (var index = _handles.Count - 1; index >= 0; index--)
            {
                _handles[index].Dispose();
            }
        }

        private static WorkspaceDirectoryLease Acquire(IEnumerable<string> paths)
        {
            var handles = new List<SafeFileHandle>();
            try
            {
                foreach (var path in paths)
                {
                    var handle = WorkspaceNativeFileSystem.OpenDirectory(path);
                    try
                    {
                        WorkspaceNativeFileSystem.ValidateOpenedPath(
                            handle,
                            path,
                            expectDirectory: true);
                        handles.Add(handle);
                    }
                    catch
                    {
                        handle.Dispose();
                        throw;
                    }
                }

                return new WorkspaceDirectoryLease(handles);
            }
            catch
            {
                for (var index = handles.Count - 1; index >= 0; index--)
                {
                    handles[index].Dispose();
                }
                throw;
            }
        }
    }

    private static class WorkspaceNativeFileSystem
    {
        private const uint FileListDirectory = 0x00000001;
        private const uint FileReadAttributes = 0x00000080;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x00000080;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int FileAttributeTagInfo = 9;

        internal static SafeFileHandle OpenDirectory(string path) => OpenPath(
            path,
            FileListDirectory | FileReadAttributes,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint);

        internal static void ValidateOpenedPath(
            SafeFileHandle handle,
            string expectedPath,
            bool expectDirectory)
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileAttributeTagInfo,
                    out var tagInformation,
                    (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
            {
                throw NativeFailure("The workspace path attributes could not be read.");
            }

            var attributes = (FileAttributes)tagInformation.FileAttributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                ((attributes & FileAttributes.Directory) != 0) != expectDirectory)
            {
                throw new InvalidDataException(
                    "Workspace context cannot enumerate reparse points.");
            }

            var finalPath = FinalPath(handle);
            var expected = Path.GetFullPath(expectedPath);
            if (expectDirectory)
            {
                finalPath = Path.TrimEndingDirectorySeparator(finalPath);
                expected = Path.TrimEndingDirectorySeparator(expected);
            }
            if (!string.Equals(finalPath, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The workspace directory changed during enumeration.");
            }
        }

        internal static bool TryReadFileMetadata(
            string path,
            out long byteLength,
            out DateTimeOffset lastWriteTime)
        {
            try
            {
                using var handle = OpenPath(
                    path,
                    FileReadAttributes,
                    FileAttributeNormal | FileFlagOpenReparsePoint);
                ValidateOpenedPath(handle, path, expectDirectory: false);
                if (!GetFileInformationByHandle(handle, out var information))
                {
                    throw NativeFailure("The workspace file metadata could not be read.");
                }
                if (information.NumberOfLinks > 1)
                {
                    byteLength = 0;
                    lastWriteTime = default;
                    return false;
                }

                var unsignedLength =
                    ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
                var unsignedFileTime =
                    ((ulong)information.LastWriteTime.High << 32) |
                    information.LastWriteTime.Low;
                if (unsignedLength > long.MaxValue || unsignedFileTime > long.MaxValue)
                {
                    byteLength = 0;
                    lastWriteTime = default;
                    return false;
                }

                byteLength = (long)unsignedLength;
                lastWriteTime = new DateTimeOffset(
                    DateTime.FromFileTimeUtc((long)unsignedFileTime));
                return true;
            }
            catch (Exception exception)
                when (exception is UnauthorizedAccessException or IOException or
                      ArgumentOutOfRangeException)
            {
                byteLength = 0;
                lastWriteTime = default;
                return false;
            }
        }

        private static SafeFileHandle OpenPath(
            string path,
            uint desiredAccess,
            uint flagsAndAttributes)
        {
            var handle = CreateFile(
                path,
                desiredAccess,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                flagsAndAttributes,
                IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                return handle;
            }

            var errorCode = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw errorCode switch
            {
                ErrorFileNotFound => new FileNotFoundException(
                    "The workspace path does not exist.",
                    path,
                    new Win32Exception(errorCode)),
                ErrorPathNotFound => new DirectoryNotFoundException(
                    "The workspace directory does not exist.",
                    new Win32Exception(errorCode)),
                _ => NativeFailure("The workspace path could not be opened.", errorCode),
            };
        }

        private static string FinalPath(SafeFileHandle handle)
        {
            var buffer = new StringBuilder(512);
            var length = GetFinalPathNameByHandle(
                handle,
                buffer,
                (uint)buffer.Capacity,
                fileNameFlags: 0);
            if (length == 0)
            {
                throw NativeFailure("The workspace final path could not be resolved.");
            }
            if (length >= buffer.Capacity)
            {
                buffer = new StringBuilder(checked((int)length + 1));
                length = GetFinalPathNameByHandle(
                    handle,
                    buffer,
                    (uint)buffer.Capacity,
                    fileNameFlags: 0);
                if (length == 0 || length >= buffer.Capacity)
                {
                    throw NativeFailure("The workspace final path could not be resolved.");
                }
            }

            var value = buffer.ToString();
            if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                value = @"\\" + value[8..];
            }
            else if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                value = value[4..];
            }
            return Path.GetFullPath(value);
        }

        private static IOException NativeFailure(string message) =>
            new(message, new Win32Exception(Marshal.GetLastWin32Error()));

        private static IOException NativeFailure(string message, int errorCode) =>
            new(message, new Win32Exception(errorCode));

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct FileAttributeTagInformation
        {
            public readonly uint FileAttributes;
            public readonly uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct NativeFileTime
        {
            public readonly uint Low;
            public readonly uint High;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct ByHandleFileInformation
        {
            public readonly uint FileAttributes;
            public readonly NativeFileTime CreationTime;
            public readonly NativeFileTime LastAccessTime;
            public readonly NativeFileTime LastWriteTime;
            public readonly uint VolumeSerialNumber;
            public readonly uint FileSizeHigh;
            public readonly uint FileSizeLow;
            public readonly uint NumberOfLinks;
            public readonly uint FileIndexHigh;
            public readonly uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle fileHandle,
            int fileInformationClass,
            out FileAttributeTagInformation fileInformation,
            uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle fileHandle,
            out ByHandleFileInformation fileInformation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle fileHandle,
            StringBuilder filePath,
            uint filePathLength,
            uint fileNameFlags);
    }
}
