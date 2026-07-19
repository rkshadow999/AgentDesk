using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentDesk.Platform.Windows.IO;

internal static class WindowsHandleFileSystem
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint FileNotifyChangeFileName = 0x00000001;
    private const uint FileNotifyChangeDirectoryName = 0x00000002;
    private const uint FileNotifyChangeAttributes = 0x00000004;
    private const uint FileNotifyChangeSize = 0x00000008;
    private const uint FileNotifyChangeLastWrite = 0x00000010;
    private const uint FileNotifyChangeCreation = 0x00000040;
    private const uint FileNotifyChangeSecurity = 0x00000100;
    private const uint FileNotifyChangeStreamName = 0x00000200;
    private const uint FileNotifyChangeStreamSize = 0x00000400;
    private const uint FileNotifyChangeStreamWrite = 0x00000800;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint DriveRemote = 4;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorHandleEof = 38;
    private const int ErrorOperationAborted = 995;
    private const int ErrorIoPending = 997;
    private const int ErrorNotFound = 1168;
    private const int FileDispositionInfo = 4;
    private const int FileAttributeTagInfo = 9;
    private const int FileIdInfo = 18;
    private const int FileRenameInfoEx = 22;
    private const int FileRenameReplaceIfExists = 0x00000001;
    private const int FileRenamePosixSemantics = 0x00000002;
    private const int MaximumRenameRecoveryAttempts = 8;
    private const int DirectoryChangeBufferSize = 64 * 1024;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private static readonly HashSet<string> WindowsReservedNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    internal static string ValidateLocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 32_767 ||
            !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw InvalidPath();
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw InvalidPath(exception);
        }

        var root = Path.GetPathRoot(fullPath);
        if (root is null || root.Length != 3 ||
            !char.IsAsciiLetter(root[0]) || root[1] != ':' ||
            root[2] != Path.DirectorySeparatorChar)
        {
            throw InvalidPath();
        }
        if (GetDriveType(root) == DriveRemote)
        {
            throw InvalidPath();
        }

        var relative = fullPath[root.Length..];
        if (relative.Contains(':', StringComparison.Ordinal))
        {
            throw InvalidPath();
        }
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            ValidateSegment(segment);
        }
        return fullPath;
    }

    internal static SafeFileHandle CreateTemporaryFile(string path)
    {
        var fullPath = ValidateLocalPath(path);
        var handle = OpenFile(
            fullPath,
            GenericRead | GenericWrite | DeleteAccess | FileReadAttributes,
            FileShareRead,
            CreateNew,
            FileAttributeNormal | FileFlagOpenReparsePoint |
                FileFlagOverlapped | FileFlagWriteThrough);
        try
        {
            _ = ValidateOpenedHandle(handle, fullPath, expectDirectory: false);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static SafeFileHandle OpenExistingFileForRead(
        string path,
        bool asynchronous = false)
    {
        var fullPath = ValidateLocalPath(path);
        var handle = OpenFile(
            fullPath,
            GenericRead | FileReadAttributes,
            FileShareRead,
            OpenExisting,
            FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagBackupSemantics |
                (asynchronous ? FileFlagOverlapped : 0));
        try
        {
            _ = ValidateOpenedHandle(handle, fullPath, expectDirectory: false);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static OpenedEntry OpenExistingEntryForRead(string path)
    {
        var fullPath = ValidateLocalPath(path);
        var handle = OpenFile(
            fullPath,
            GenericRead | FileReadAttributes,
            FileShareRead,
            OpenExisting,
            FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagBackupSemantics);
        try
        {
            var attributes = ValidateOpenedHandle(handle, fullPath, expectDirectory: null);
            return new OpenedEntry(
                handle,
                fullPath,
                (attributes & FileAttributes.Directory) != 0);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static SafeFileHandle OpenExistingReplacementTarget(string path)
    {
        var fullPath = ValidateLocalPath(path);
        var handle = OpenFile(
            fullPath,
            DeleteAccess | FileReadAttributes,
            shareMode: 0,
            OpenExisting,
            FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagBackupSemantics);
        try
        {
            _ = ValidateOpenedHandle(handle, fullPath, expectDirectory: false);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static SafeFileHandle OpenExistingDirectoryForReplacement(
        string path,
        FileIdentity? expectedIdentity = null)
    {
        var fullPath = ValidateLocalPath(path);
        var handle = OpenFile(
            fullPath,
            DeleteAccess | FileListDirectory | FileReadAttributes,
            FileShareRead | FileShareWrite,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics);
        try
        {
            _ = ValidateOpenedHandle(handle, fullPath, expectDirectory: true);
            if (expectedIdentity is not null && Identity(handle) != expectedIdentity.Value)
            {
                throw new InvalidDataException(
                    "The local directory was substituted during access.");
            }
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static SafeFileHandle OpenExistingDirectoryForPublication(string path)
    {
        var fullPath = ValidateLocalPath(path);
        var handle = OpenFile(
            fullPath,
            FileListDirectory | FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics);
        try
        {
            _ = ValidateOpenedHandle(handle, fullPath, expectDirectory: true);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static FileIdentity GetIdentity(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return Identity(handle);
    }

    internal static void CreateHardLinkPin(string pinPath, string existingPath)
    {
        var pin = ValidateLocalPath(pinPath);
        var existing = ValidateLocalPath(existingPath);
        if (!string.Equals(
                Path.GetPathRoot(pin),
                Path.GetPathRoot(existing),
                StringComparison.OrdinalIgnoreCase) ||
            !CreateHardLink(pin, existing, IntPtr.Zero))
        {
            throw NativeFailure("The verified restore file could not be pinned.");
        }
    }

    internal static SafeFileHandle OpenPinnedFile(
        string path,
        FileIdentity expectedIdentity)
    {
        var fullPath = ValidateLocalPath(path);
        var handle = OpenFile(
            fullPath,
            GenericRead | FileReadAttributes,
            FileShareRead,
            OpenExisting,
            FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagBackupSemantics);
        try
        {
            _ = ValidateOpenedHandle(handle, fullPath, expectDirectory: false);
            if (Identity(handle) != expectedIdentity)
            {
                throw new InvalidDataException(
                    "The verified restore file pin was substituted.");
            }
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static FileIdentity GetDirectoryIdentity(string path)
    {
        using var handle = OpenExistingDirectoryForPublication(path);
        return Identity(handle);
    }

    internal static void ValidatePublishedFileIdentity(
        SafeFileHandle pinnedHandle,
        string expectedPath)
    {
        ArgumentNullException.ThrowIfNull(pinnedHandle);
        var fullPath = ValidateLocalPath(expectedPath);
        using var publishedHandle = OpenFile(
            fullPath,
            GenericRead | FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            OpenExisting,
            FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagBackupSemantics);
        _ = ValidateOpenedHandle(publishedHandle, fullPath, expectDirectory: false);
        if (Identity(publishedHandle) != Identity(pinnedHandle))
        {
            throw new InvalidDataException(
                "The verified restore file was substituted before publication.");
        }
    }

    internal static void ValidatePublishedDirectoryIdentity(
        string expectedPath,
        FileIdentity expectedIdentity)
    {
        using var handle = OpenExistingDirectoryForPublication(expectedPath);
        if (Identity(handle) != expectedIdentity)
        {
            throw new InvalidDataException(
                "The verified restore directory was substituted before publication.");
        }
    }

    internal static SafeFileHandle OpenPublishedFileLease(
        SafeFileHandle pinnedHandle,
        string expectedPath)
    {
        ArgumentNullException.ThrowIfNull(pinnedHandle);
        var fullPath = ValidateLocalPath(expectedPath);
        var handle = OpenFile(
            fullPath,
            GenericRead | FileReadAttributes,
            FileShareRead,
            OpenExisting,
            FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagBackupSemantics);
        try
        {
            _ = ValidateOpenedHandle(handle, fullPath, expectDirectory: false);
            if (Identity(handle) != Identity(pinnedHandle))
            {
                throw new InvalidDataException(
                    "The verified restore file was substituted before publication.");
            }
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static void ValidateNoAlternateDataStreams(string path)
    {
        var fullPath = ValidateLocalPath(path);
        var findHandle = FindFirstStream(
            fullPath,
            streamInfoLevel: 0,
            out var streamData,
            flags: 0);
        if (findHandle == InvalidHandleValue)
        {
            if (Marshal.GetLastWin32Error() == ErrorHandleEof)
            {
                return;
            }
            throw NativeFailure(
                "The local file streams could not be enumerated.");
        }

        try
        {
            while (true)
            {
                if (!string.Equals(
                        streamData.StreamName,
                        "::$DATA",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Restored files and directories cannot contain alternate data streams.");
                }
                if (FindNextStream(findHandle, out streamData))
                {
                    continue;
                }
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorHandleEof)
                {
                    throw NativeFailure(
                        "The local file streams could not be enumerated.",
                        error);
                }
                return;
            }
        }
        finally
        {
            _ = FindClose(findHandle);
        }
    }

    internal static DirectoryChangeMonitor WatchDirectoryTree(string path)
    {
        var fullPath = ValidateLocalPath(path);
        var handle = OpenFile(
            fullPath,
            FileListDirectory | FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics | FileFlagOverlapped);
        try
        {
            _ = ValidateOpenedHandle(handle, fullPath, expectDirectory: true);
            return DirectoryChangeMonitor.Arm(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static void AtomicReplace(
        SafeFileHandle replacementHandle,
        string replacementPath,
        string targetPath,
        Action? beforeReplacementRenameForTesting = null)
    {
        ArgumentNullException.ThrowIfNull(replacementHandle);
        var replacement = ValidateLocalPath(replacementPath);
        var target = ValidateLocalPath(targetPath);
        if (!string.Equals(
                Path.GetDirectoryName(replacement),
                Path.GetDirectoryName(target),
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidPath();
        }

        _ = ValidateOpenedHandle(replacementHandle, replacement, expectDirectory: false);
        using var targetHandle = TryOpenExistingReplacementTarget(target);
        beforeReplacementRenameForTesting?.Invoke();
        RenameToPath(
            replacementHandle,
            target,
            FileRenameReplaceIfExists | FileRenamePosixSemantics);
        _ = ValidateOpenedHandle(replacementHandle, target, expectDirectory: false);
    }

    internal static DisplacedDirectory? AtomicReplaceDirectory(
        SafeFileHandle replacementHandle,
        string replacementPath,
        string targetPath,
        Action? beforeReplacementRenameForTesting = null,
        Func<IDisposable?>? acquireReplacementValidationLease = null)
    {
        ArgumentNullException.ThrowIfNull(replacementHandle);
        var replacement = ValidateLocalPath(replacementPath);
        var target = ValidateLocalPath(targetPath);
        if (!string.Equals(
                Path.GetDirectoryName(replacement),
                Path.GetDirectoryName(target),
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidPath();
        }

        _ = ValidateOpenedHandle(replacementHandle, replacement, expectDirectory: true);
        var targetHandle = TryOpenExistingReplacementDirectory(target);
        string? displacedPath = null;
        var quarantinedEntries = new List<DisplacedEntry>();
        var targetDisplaced = false;
        var replacementMoved = false;
        IDisposable? validationLease = null;
        string? failedReplacementPath = null;
        try
        {
            if (targetHandle is not null)
            {
                displacedPath = Path.Combine(
                    Path.GetDirectoryName(target)!,
                    $".agentdesk-previous-{Guid.NewGuid():N}");
                RenameToPath(targetHandle, displacedPath, flags: 0);
                targetDisplaced = true;
            }
            beforeReplacementRenameForTesting?.Invoke();
            RenameWithOccupantRecovery(replacementHandle, target, quarantinedEntries);
            replacementMoved = true;
            _ = ValidateOpenedHandle(replacementHandle, target, expectDirectory: true);
            validationLease = acquireReplacementValidationLease?.Invoke();
            _ = ValidateOpenedHandle(replacementHandle, target, expectDirectory: true);
            DeleteQuarantinedEntries(quarantinedEntries);
            validationLease?.Dispose();
            validationLease = null;
            return targetHandle is null
                ? null
                : new DisplacedDirectory(targetHandle, displacedPath!);
        }
        catch
        {
            try
            {
                validationLease?.Dispose();
            }
            catch (Exception)
            {
            }
            if (replacementMoved)
            {
                try
                {
                    failedReplacementPath = Path.Combine(
                        Path.GetDirectoryName(target)!,
                        $".agentdesk-failed-restore-{Guid.NewGuid():N}");
                    RenameWithOccupantRecovery(
                        replacementHandle,
                        failedReplacementPath,
                        quarantinedEntries);
                    replacementMoved = false;
                }
                catch (Exception)
                {
                }
            }
            if (targetDisplaced && !replacementMoved && targetHandle is not null)
            {
                try
                {
                    RenameWithOccupantRecovery(targetHandle, target, quarantinedEntries);
                    targetDisplaced = false;
                }
                catch (Exception)
                {
                }
            }
            if (!replacementMoved && failedReplacementPath is not null)
            {
                try
                {
                    DeleteDirectoryTree(replacementHandle, failedReplacementPath);
                }
                catch (Exception)
                {
                }
            }
            DeleteQuarantinedEntries(quarantinedEntries);
            targetHandle?.Dispose();
            if (replacementMoved || targetDisplaced)
            {
                throw new IOException(
                    "The restore transaction failed and the previous directory could not be fully recovered.");
            }
            throw;
        }
    }

    internal static void DeleteDirectoryTree(
        SafeFileHandle directoryHandle,
        string directoryPath)
    {
        ArgumentNullException.ThrowIfNull(directoryHandle);
        var path = ValidateLocalPath(directoryPath);
        _ = ValidateOpenedHandle(directoryHandle, path, expectDirectory: true);
        DeleteDirectoryContents(directoryHandle, path);
        SetDeleteDisposition(directoryHandle, delete: true);
    }

    internal static void DeleteTemporaryFile(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        SetDeleteDisposition(handle, delete: true);
    }

    private static void SetDeleteDisposition(SafeFileHandle handle, bool delete)
    {
        var disposition = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(disposition, delete ? (byte)1 : (byte)0);
            if (!SetFileInformationByHandle(
                    handle,
                    FileDispositionInfo,
                    disposition,
                    1))
            {
                throw NativeFailure("The local file system object could not be deleted.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(disposition);
        }
    }

    private static SafeFileHandle? TryOpenExistingReplacementTarget(string path)
    {
        var handle = CreateFile(
            path,
            DeleteAccess | FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            try
            {
                _ = ValidateOpenedHandle(handle, path, expectDirectory: false);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is ErrorFileNotFound or ErrorPathNotFound)
        {
            return null;
        }
        throw NativeFailure("The replacement target could not be opened.", error);
    }

    private static SafeFileHandle? TryOpenExistingReplacementDirectory(string path)
    {
        var handle = CreateFile(
            path,
            DeleteAccess | FileListDirectory | FileReadAttributes,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            try
            {
                _ = ValidateOpenedHandle(handle, path, expectDirectory: true);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is ErrorFileNotFound or ErrorPathNotFound)
        {
            return null;
        }
        throw NativeFailure("The replacement directory could not be opened.", error);
    }

    private static void RenameWithOccupantRecovery(
        SafeFileHandle handle,
        string targetPath,
        List<DisplacedEntry> quarantinedEntries)
    {
        IOException? lastFailure = null;
        for (var attempt = 0; attempt < MaximumRenameRecoveryAttempts; attempt++)
        {
            try
            {
                RenameToPath(handle, targetPath, flags: 0);
                return;
            }
            catch (IOException exception)
            {
                lastFailure = exception;
                var quarantined = TryQuarantineTargetEntry(targetPath);
                if (quarantined is null)
                {
                    throw;
                }
                quarantinedEntries.Add(quarantined);
            }
        }

        throw new IOException(
            "The target path was repeatedly occupied during replacement.",
            lastFailure);
    }

    private static DisplacedEntry? TryQuarantineTargetEntry(string path)
    {
        var handle = CreateFile(
            path,
            GenericRead | DeleteAccess | FileReadAttributes,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return null;
            }
            throw NativeFailure("The raced replacement target could not be opened.", error);
        }

        try
        {
            var attributes = ValidateOpenedHandle(
                handle,
                path,
                expectDirectory: null,
                rejectReparsePoints: false);
            var quarantinePath = Path.Combine(
                Path.GetDirectoryName(path)!,
                $".agentdesk-raced-{Guid.NewGuid():N}");
            RenameToPath(handle, quarantinePath, flags: 0);
            return new DisplacedEntry(
                handle,
                quarantinePath,
                (attributes & FileAttributes.Directory) != 0,
                (attributes & FileAttributes.ReparsePoint) != 0);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void DeleteQuarantinedEntries(List<DisplacedEntry> entries)
    {
        foreach (var entry in entries)
        {
            try
            {
                if (entry.IsDirectory && !entry.IsReparsePoint)
                {
                    DeleteDirectoryTree(entry.Handle, entry.Path);
                }
                else
                {
                    SetDeleteDisposition(entry.Handle, delete: true);
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                entry.Dispose();
            }
        }
        entries.Clear();
    }

    private static OpenedEntry OpenExistingEntryForDeletion(string path)
    {
        var fullPath = ValidateLocalPath(path);
        var handle = OpenFile(
            fullPath,
            GenericRead | DeleteAccess | FileReadAttributes,
            FileShareRead | FileShareWrite,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics);
        try
        {
            var attributes = ValidateOpenedHandle(handle, fullPath, expectDirectory: null);
            return new OpenedEntry(
                handle,
                fullPath,
                (attributes & FileAttributes.Directory) != 0);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void DeleteDirectoryContents(
        SafeFileHandle directoryHandle,
        string directoryPath)
    {
        _ = ValidateOpenedHandle(directoryHandle, directoryPath, expectDirectory: true);
        foreach (var itemPath in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            using var opened = OpenExistingEntryForDeletion(itemPath);
            if (opened.IsDirectory)
            {
                DeleteDirectoryContents(opened.Handle, itemPath);
            }
            SetDeleteDisposition(opened.Handle, delete: true);
        }
        _ = ValidateOpenedHandle(directoryHandle, directoryPath, expectDirectory: true);
    }

    private static void ValidateSegment(string segment)
    {
        if (segment.Length is 0 or > 255 ||
            segment is "." or ".." ||
            segment.EndsWith(' ') || segment.EndsWith('.') ||
            segment.Any(character => char.IsControl(character) ||
                Path.GetInvalidFileNameChars().Contains(character)))
        {
            throw InvalidPath();
        }

        var deviceName = segment.Split('.', 2)[0].TrimEnd(' ', '.');
        if (WindowsReservedNames.Contains(deviceName))
        {
            throw InvalidPath();
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

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is ErrorFileNotFound or ErrorPathNotFound)
        {
            throw new DirectoryNotFoundException("The local file system path does not exist.");
        }
        throw NativeFailure("The local file system path could not be opened.", error);
    }

    private static FileAttributes ValidateOpenedHandle(
        SafeFileHandle handle,
        string expectedPath,
        bool? expectDirectory,
        bool rejectReparsePoints = true)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfo,
                out var tagInfo,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            throw NativeFailure("The local file attributes could not be read.");
        }
        var attributes = (FileAttributes)tagInfo.FileAttributes;
        if ((rejectReparsePoints && (attributes & FileAttributes.ReparsePoint) != 0) ||
            (expectDirectory is not null &&
             ((attributes & FileAttributes.Directory) != 0) != expectDirectory.Value))
        {
            throw new InvalidDataException("Local file operations cannot use reparse points.");
        }

        var finalPath = FinalPath(handle);
        var expected = ValidateLocalPath(expectedPath);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            finalPath = Path.TrimEndingDirectorySeparator(finalPath);
            expected = Path.TrimEndingDirectorySeparator(expected);
        }
        if (!string.Equals(finalPath, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The local file system path changed during access.");
        }
        ValidateFileSystem(handle);
        return attributes;
    }

    private static FileIdentity Identity(SafeFileHandle handle)
    {
        if (!GetFileIdentityByHandleEx(
                handle,
                FileIdInfo,
                out var information,
                (uint)Marshal.SizeOf<FileIdInformation>()))
        {
            throw NativeFailure("The local file identity could not be read.");
        }
        return new FileIdentity(
            information.VolumeSerialNumber,
            information.FileId.LowPart,
            information.FileId.HighPart);
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
        {
            throw NativeFailure("The final local file path could not be resolved.");
        }
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
            {
                throw NativeFailure("The final local file path could not be resolved.");
            }
        }

        var value = buffer.ToString();
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidPath();
        }
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            value = value[4..];
        }
        return ValidateLocalPath(value);
    }

    private static void ValidateFileSystem(SafeFileHandle handle)
    {
        var fileSystemName = new StringBuilder(32);
        if (!GetVolumeInformationByHandleW(
                handle,
                null,
                0,
                out _,
                out _,
                out _,
                fileSystemName,
                (uint)fileSystemName.Capacity))
        {
            throw NativeFailure("The local file system type could not be read.");
        }
        if (!string.Equals(fileSystemName.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fileSystemName.ToString(), "ReFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only local NTFS or ReFS paths are supported.");
        }
    }

    private static void RenameToPath(
        SafeFileHandle fileHandle,
        string targetPath,
        int flags)
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
            Marshal.WriteInt32(buffer, flags);
            Marshal.WriteIntPtr(buffer, rootDirectoryOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, fileNameLengthOffset, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, fileNameOffset), nameBytes.Length);
            if (!SetFileInformationByHandle(
                    fileHandle,
                    FileRenameInfoEx,
                    buffer,
                    (uint)bufferSize))
            {
                throw NativeFailure("The file could not be atomically replaced.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static InvalidDataException InvalidPath(Exception? inner = null) =>
        new("Only fully-qualified local NTFS or ReFS paths are supported.", inner);

    private static IOException NativeFailure(string message) =>
        new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    private static IOException NativeFailure(string message, int error) =>
        new(message, new Win32Exception(error));

    internal sealed class DirectoryChangeMonitor : IDisposable
    {
        private static readonly System.Collections.Concurrent.ConcurrentBag<DirectoryChangeMonitor>
            LeakedMonitors = [];
        private SafeFileHandle? _directoryHandle;
        private SafeWaitHandle? _eventHandle;
        private IntPtr _buffer;
        private IntPtr _overlapped;
        private bool _leakResources;
        private bool _operationStarted;
        private int _stopped;

        private DirectoryChangeMonitor(
            SafeFileHandle directoryHandle,
            SafeWaitHandle eventHandle,
            IntPtr buffer,
            IntPtr overlapped)
        {
            _directoryHandle = directoryHandle;
            _eventHandle = eventHandle;
            _buffer = buffer;
            _overlapped = overlapped;
        }

        internal static DirectoryChangeMonitor Arm(SafeFileHandle directoryHandle)
        {
            SafeWaitHandle? eventHandle = null;
            var buffer = IntPtr.Zero;
            var overlapped = IntPtr.Zero;
            try
            {
                eventHandle = CreateEvent(
                    IntPtr.Zero,
                    manualReset: true,
                    initialState: false,
                    name: null);
                if (eventHandle.IsInvalid)
                {
                    throw NativeFailure(
                        "The restore directory change monitor could not create an event.");
                }

                buffer = Marshal.AllocHGlobal(DirectoryChangeBufferSize);
                overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<OverlappedState>());
                Marshal.StructureToPtr(
                    new OverlappedState { EventHandle = eventHandle.DangerousGetHandle() },
                    overlapped,
                    fDeleteOld: false);
                var monitor = new DirectoryChangeMonitor(
                    directoryHandle,
                    eventHandle,
                    buffer,
                    overlapped);
                eventHandle = null;
                buffer = IntPtr.Zero;
                overlapped = IntPtr.Zero;
                try
                {
                    monitor.Start();
                    return monitor;
                }
                catch
                {
                    monitor.Dispose();
                    throw;
                }
            }
            catch
            {
                if (overlapped != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(overlapped);
                }
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
                eventHandle?.Dispose();
                directoryHandle.Dispose();
                throw;
            }
        }

        private void Start()
        {
            var directoryHandle = _directoryHandle ??
                throw new ObjectDisposedException(nameof(DirectoryChangeMonitor));
            var started = ReadDirectoryChangesW(
                    directoryHandle,
                    _buffer,
                    DirectoryChangeBufferSize,
                    watchSubtree: true,
                    FileNotifyChangeFileName |
                        FileNotifyChangeDirectoryName |
                        FileNotifyChangeAttributes |
                        FileNotifyChangeSize |
                        FileNotifyChangeLastWrite |
                        FileNotifyChangeCreation |
                        FileNotifyChangeSecurity |
                        FileNotifyChangeStreamName |
                        FileNotifyChangeStreamSize |
                        FileNotifyChangeStreamWrite,
                    out _,
                    _overlapped,
                    IntPtr.Zero);
            if (started)
            {
                _operationStarted = true;
                return;
            }
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorIoPending)
            {
                _operationStarted = true;
                return;
            }
            throw NativeFailure(
                "The restore directory change monitor could not be armed.",
                error);
        }

        internal void ValidateNoChangesAndDispose()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                throw new ObjectDisposedException(nameof(DirectoryChangeMonitor));
            }

            Exception? failure = null;
            var changed = false;
            try
            {
                changed = CompleteOperation();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                ReleaseResources();
            }

            if (failure is not null)
            {
                throw new InvalidDataException(
                    "The restore directory change monitor failed closed.",
                    failure);
            }
            if (changed)
            {
                throw new InvalidDataException(
                    "The verified restore tree changed during publication.");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }
            try
            {
                _ = CompleteOperation();
            }
            catch (Exception)
            {
            }
            finally
            {
                ReleaseResources();
            }
        }

        private bool CompleteOperation()
        {
            if (!_operationStarted)
            {
                return false;
            }
            var directoryHandle = _directoryHandle ??
                throw new ObjectDisposedException(nameof(DirectoryChangeMonitor));
            if (!CancelIoEx(directoryHandle, _overlapped))
            {
                var cancelError = Marshal.GetLastWin32Error();
                if (cancelError != ErrorNotFound)
                {
                    _leakResources = true;
                    throw NativeFailure(
                        "The restore directory change monitor could not be stopped.",
                        cancelError);
                }
            }

            if (GetOverlappedResult(
                    directoryHandle,
                    _overlapped,
                    out _,
                    wait: true))
            {
                _operationStarted = false;
                return true;
            }
            var completionError = Marshal.GetLastWin32Error();
            if (completionError == ErrorOperationAborted)
            {
                _operationStarted = false;
                return false;
            }
            _operationStarted = false;
            throw NativeFailure(
                "The restore directory change monitor could not confirm a stable tree.",
                completionError);
        }

        private void ReleaseResources()
        {
            if (_leakResources)
            {
                LeakedMonitors.Add(this);
                return;
            }
            _directoryHandle?.Dispose();
            _directoryHandle = null;
            _eventHandle?.Dispose();
            _eventHandle = null;
            if (_overlapped != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_overlapped);
                _overlapped = IntPtr.Zero;
            }
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }
        }
    }

    internal sealed class OpenedEntry : IDisposable
    {
        private int _disposed;

        internal OpenedEntry(SafeFileHandle handle, string path, bool isDirectory)
        {
            Handle = handle;
            Path = path;
            IsDirectory = isDirectory;
        }

        internal SafeFileHandle Handle { get; }

        internal string Path { get; }

        internal bool IsDirectory { get; }

        internal void Validate()
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _ = ValidateOpenedHandle(Handle, Path, expectDirectory: IsDirectory);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Handle.Dispose();
            }
        }
    }

    internal sealed class DisplacedDirectory : IDisposable
    {
        private int _disposed;

        internal DisplacedDirectory(SafeFileHandle handle, string path)
        {
            Handle = handle;
            Path = path;
        }

        internal SafeFileHandle Handle { get; }

        internal string Path { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Handle.Dispose();
            }
        }
    }

    private sealed class DisplacedEntry : IDisposable
    {
        private int _disposed;

        internal DisplacedEntry(
            SafeFileHandle handle,
            string path,
            bool isDirectory,
            bool isReparsePoint)
        {
            Handle = handle;
            Path = path;
            IsDirectory = isDirectory;
            IsReparsePoint = isReparsePoint;
        }

        internal SafeFileHandle Handle { get; }

        internal string Path { get; }

        internal bool IsDirectory { get; }

        internal bool IsReparsePoint { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Handle.Dispose();
            }
        }
    }

    internal sealed class DirectoryPathGuard : IDisposable
    {
        private readonly List<(SafeFileHandle Handle, string Path)> _entries;
        private int _disposed;

        private DirectoryPathGuard(List<(SafeFileHandle, string)> entries)
        {
            _entries = entries;
        }

        internal SafeFileHandle LeafHandle => _disposed == 0 && _entries.Count > 0
            ? _entries[^1].Handle
            : throw new ObjectDisposedException(nameof(DirectoryPathGuard));

        internal FileIdentity LeafIdentity => _disposed == 0 && _entries.Count > 0
            ? Identity(_entries[^1].Handle)
            : throw new ObjectDisposedException(nameof(DirectoryPathGuard));

        internal static DirectoryPathGuard Acquire(
            string directoryPath,
            bool createIfMissing)
        {
            var fullPath = ValidateLocalPath(directoryPath);
            var root = Path.GetPathRoot(fullPath) ?? throw InvalidPath();
            var segments = fullPath[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            var paths = new List<string>(segments.Length + 1) { root };
            var current = root;
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                paths.Add(current);
            }

            var entries = new List<(SafeFileHandle, string)>(paths.Count);
            try
            {
                foreach (var path in paths)
                {
                    SafeFileHandle handle;
                    try
                    {
                        handle = OpenDirectory(path);
                    }
                    catch (DirectoryNotFoundException) when (createIfMissing)
                    {
                        Directory.CreateDirectory(path);
                        handle = OpenDirectory(path);
                    }

                    try
                    {
                        _ = ValidateOpenedHandle(handle, path, expectDirectory: true);
                        entries.Add((handle, path));
                    }
                    catch
                    {
                        handle.Dispose();
                        throw;
                    }
                }
                return new DirectoryPathGuard(entries);
            }
            catch
            {
                for (var index = entries.Count - 1; index >= 0; index--)
                {
                    entries[index].Item1.Dispose();
                }
                throw;
            }
        }

        internal void Validate()
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            foreach (var entry in _entries)
            {
                _ = ValidateOpenedHandle(entry.Handle, entry.Path, expectDirectory: true);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            for (var index = _entries.Count - 1; index >= 0; index--)
            {
                _entries[index].Handle.Dispose();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileAttributeTagInformation
    {
        public readonly uint FileAttributes;
        public readonly uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OverlappedState
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr EventHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        public long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string StreamName;
    }

    internal readonly record struct FileIdentity(
        ulong VolumeSerialNumber,
        ulong FileIdLow,
        ulong FileIdHigh);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong LowPart;
        public ulong HighPart;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStream(
        string fileName,
        int streamInfoLevel,
        out Win32FindStreamData findStreamData,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(
        IntPtr findStreamHandle,
        out Win32FindStreamData findStreamData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findHandle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEvent(
        IntPtr eventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState,
        string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadDirectoryChangesW(
        SafeFileHandle directoryHandle,
        IntPtr buffer,
        int bufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter,
        out uint bytesReturned,
        IntPtr overlapped,
        IntPtr completionRoutine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(
        SafeFileHandle fileHandle,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle fileHandle,
        IntPtr overlapped,
        out uint bytesTransferred,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        SafeWaitHandle handle,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileIdentityByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileIdInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle fileHandle,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(
        SafeFileHandle fileHandle,
        StringBuilder? volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        uint fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);
}
