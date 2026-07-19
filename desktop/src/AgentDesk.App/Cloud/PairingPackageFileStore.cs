using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentDesk.App.Cloud;

public sealed class PairingPackageFileStore
{
    public const string PackageExtension = ".agentdesk-pairing";

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const int FileDispositionInfo = 4;
    private const int FileAttributeTagInfo = 9;
    private const int FileRenameInfo = 3;
    private const int FileRenameReplaceIfExists = 0x00000001;

    private static readonly HashSet<string> WindowsReservedNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly int _maximumBytes;

    public PairingPackageFileStore(int maximumBytes = 4 * 1024)
    {
        if (maximumBytes is < 1 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        _maximumBytes = maximumBytes;
    }

    public Task WriteAsync(
        string path,
        ReadOnlyMemory<byte> package,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (package.Length is < 1 || package.Length > _maximumBytes)
        {
            throw InvalidPackage();
        }

        var targetPath = ValidatePath(path, requireExistingFile: false);
        var directoryPath = Path.GetDirectoryName(targetPath) ?? throw InvalidPackage();
        var targetName = Path.GetFileName(targetPath);
        var temporaryName = $".{targetName}.{Guid.NewGuid():N}.tmp";
        var temporaryPath = Path.Combine(directoryPath, temporaryName);
        using var directoryGuard = DirectoryPathGuard.Acquire(directoryPath);
        var directoryHandle = directoryGuard.LeafHandle;

        using var temporaryHandle = OpenFile(
            temporaryPath,
            GenericWrite | DeleteAccess | FileReadAttributes,
            FileShareRead,
            CreateNew,
            FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagWriteThrough);
        var renamed = false;
        try
        {
            ValidateOpenedHandle(temporaryHandle, temporaryPath, expectDirectory: false);
            RandomAccess.Write(temporaryHandle, package.Span, fileOffset: 0);
            RandomAccess.FlushToDisk(temporaryHandle);
            cancellationToken.ThrowIfCancellationRequested();

            ValidateOpenedHandle(directoryHandle, directoryPath, expectDirectory: true);
            RenameToPath(temporaryHandle, targetPath);
            renamed = true;
            ValidateOpenedHandle(directoryHandle, directoryPath, expectDirectory: true);
            return Task.CompletedTask;
        }
        finally
        {
            if (!renamed)
            {
                DeleteOrEraseTemporaryFile(temporaryHandle, package.Length);
            }
        }
    }

    public Task<byte[]> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourcePath = ValidatePath(path, requireExistingFile: true);
        var directoryPath = Path.GetDirectoryName(sourcePath) ?? throw InvalidPackage();
        using var directoryGuard = DirectoryPathGuard.Acquire(directoryPath);
        var directoryHandle = directoryGuard.LeafHandle;
        using var sourceHandle = OpenFile(
            sourcePath,
            GenericRead | FileReadAttributes,
            FileShareRead,
            OpenExisting,
            FileAttributeNormal | FileFlagOpenReparsePoint);
        ValidateOpenedHandle(sourceHandle, sourcePath, expectDirectory: false);
        ValidateOpenedHandle(directoryHandle, directoryPath, expectDirectory: true);

        var length = RandomAccess.GetLength(sourceHandle);
        if (length is < 1 || length > _maximumBytes)
        {
            throw InvalidPackage();
        }

        var package = new byte[checked((int)length)];
        var offset = 0;
        while (offset < package.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(
                sourceHandle,
                package.AsSpan(offset),
                fileOffset: offset);
            if (read == 0)
            {
                throw InvalidPackage();
            }
            offset += read;
        }
        return Task.FromResult(package);
    }

    private static string ValidatePath(string path, bool requireExistingFile)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_767 ||
            !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw InvalidPackage();
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw InvalidPackage();
        }

        if (!string.Equals(
                Path.GetExtension(fullPath),
                PackageExtension,
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidPackage();
        }

        var root = Path.GetPathRoot(fullPath) ?? throw InvalidPackage();
        var relative = fullPath[root.Length..];
        if (relative.Contains(':'))
        {
            throw new InvalidDataException("NTFS alternate data streams are not supported.");
        }

        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            ValidateSegment(segment);
        }

        var directory = Path.GetDirectoryName(fullPath) ?? throw InvalidPackage();
        if (!Directory.Exists(directory) || Directory.Exists(fullPath) ||
            (requireExistingFile && !File.Exists(fullPath)))
        {
            throw InvalidPackage();
        }
        return fullPath;
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
            throw InvalidPackage();
        }

        var deviceName = segment.Split('.', 2)[0].TrimEnd(' ', '.');
        if (WindowsReservedNames.Contains(deviceName))
        {
            throw InvalidPackage();
        }
    }

    private static SafeFileHandle OpenDirectory(string path) => OpenFile(
        path,
        FileListDirectory | FileReadAttributes,
        FileShareRead | FileShareWrite,
        OpenExisting,
        FileFlagBackupSemantics | FileFlagOpenReparsePoint);

    private static SafeFileHandle OpenFile(
        string path,
        uint desiredAccess,
        uint shareMode,
        uint creationDisposition,
        uint flagsAndAttributes)
    {
        var handle = CreateFile(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            creationDisposition,
            flagsAndAttributes,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        handle.Dispose();
        throw new IOException(
            "The pairing package file could not be opened.",
            new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private static void ValidateOpenedHandle(
        SafeFileHandle handle,
        string expectedPath,
        bool expectDirectory)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfo,
                out var tagInfo,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            throw NativeFailure("The pairing package file attributes could not be read.");
        }
        var attributes = (FileAttributes)tagInfo.FileAttributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            ((attributes & FileAttributes.Directory) != 0) != expectDirectory)
        {
            throw new InvalidDataException("Pairing packages cannot use reparse points.");
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
            throw new InvalidDataException("The pairing package path changed during access.");
        }
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
        {
            throw NativeFailure("The pairing package final path could not be resolved.");
        }
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
            {
                throw NativeFailure("The pairing package final path could not be resolved.");
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

    private static void RenameToPath(SafeFileHandle fileHandle, string targetPath)
    {
        var nameBytes = Encoding.Unicode.GetBytes(targetPath);
        var rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
        var fileNameLengthOffset = rootDirectoryOffset + IntPtr.Size;
        var fileNameOffset = fileNameLengthOffset + sizeof(int);
        var bufferSize = checked(fileNameOffset + nameBytes.Length + sizeof(char));
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (var offset = 0; offset < bufferSize; offset++)
            {
                Marshal.WriteByte(buffer, offset, 0);
            }
            Marshal.WriteInt32(buffer, FileRenameReplaceIfExists);
            Marshal.WriteIntPtr(buffer, rootDirectoryOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, fileNameLengthOffset, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, fileNameOffset), nameBytes.Length);
            if (!SetFileInformationByHandle(
                    fileHandle,
                    FileRenameInfo,
                    buffer,
                    (uint)bufferSize))
            {
                throw NativeFailure("The pairing package could not be atomically replaced.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void DeleteOrEraseTemporaryFile(SafeFileHandle handle, int length)
    {
        var disposition = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(disposition, 1);
            if (SetFileInformationByHandle(
                    handle,
                    FileDispositionInfo,
                    disposition,
                    1))
            {
                return;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(disposition);
        }

        try
        {
            RandomAccess.Write(handle, new byte[length], fileOffset: 0);
            RandomAccess.FlushToDisk(handle);
        }
        catch (Exception)
        {
            // The encrypted package is already passphrase-protected; cleanup stays best-effort.
        }
    }

    private static IOException NativeFailure(string message) =>
        new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    private static InvalidDataException InvalidPackage() =>
        new("The pairing package file is invalid.");

    internal sealed class DirectoryPathGuard : IDisposable
    {
        private readonly List<SafeFileHandle> _handles;
        private int _disposed;

        private DirectoryPathGuard(List<SafeFileHandle> handles)
        {
            _handles = handles;
        }

        internal SafeFileHandle LeafHandle => _disposed == 0 && _handles.Count > 0
            ? _handles[^1]
            : throw new ObjectDisposedException(nameof(DirectoryPathGuard));

        internal static DirectoryPathGuard Acquire(string directoryPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
            var fullPath = Path.GetFullPath(directoryPath);
            var root = Path.GetPathRoot(fullPath) ?? throw InvalidPackage();
            var relative = fullPath[root.Length..];
            var segments = relative.Split(
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

            var handles = new List<SafeFileHandle>(paths.Count);
            try
            {
                for (var index = 0; index < paths.Count; index++)
                {
                    var path = paths[index];
                    var handle = OpenDirectory(path);
                    try
                    {
                        ValidateOpenedHandle(handle, path, expectDirectory: true);
                        handles.Add(handle);
                    }
                    catch
                    {
                        handle.Dispose();
                        throw;
                    }
                }
                return new DirectoryPathGuard(handles);
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
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileAttributeTagInformation
    {
        public readonly uint FileAttributes;
        public readonly uint ReparseTag;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle fileHandle,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);
}
