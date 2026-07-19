using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentDesk.App.Workspace;

internal interface IWorkspaceFileReplacementOperations
{
    void Replace(string targetPath, string replacementPath, string? backupPath);

    void Move(string sourcePath, string targetPath);
}

internal sealed class WorkspaceFileReplacementException(
    string message,
    int nativeErrorCode) : IOException(message, new Win32Exception(nativeErrorCode))
{
    internal int NativeErrorCode { get; } = nativeErrorCode;
}

internal sealed class WindowsWorkspaceFileLease : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint WriteOwner = 0x00080000;
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
    private const uint MoveFileWriteThrough = 0x00000008;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorUnableToRemoveReplaced = 1175;
    private const int ErrorUnableToMoveReplacement = 1176;
    private const int ErrorUnableToMoveReplacement2 = 1177;
    private const int FileDispositionInfo = 4;
    private const int FileAttributeTagInfo = 9;
    private const int SeFileObject = 1;
    private const uint OwnerSecurityInformation = 0x00000001;

    private static readonly IWorkspaceFileReplacementOperations DefaultReplacementOperations =
        new NativeWorkspaceFileReplacementOperations();

    private readonly string _workspaceRoot;
    private readonly string _targetPath;
    private readonly int _maximumBytes;
    private readonly bool _writable;
    private readonly bool _requireInstructionName;
    private readonly DirectoryPathGuard _directoryGuard;
    private readonly IWorkspaceFileReplacementOperations _replacementOperations;
    private SafeFileHandle? _fileHandle;
    private FileIdentity _identity;
    private int _replaceStarted;
    private int _disposed;

    private WindowsWorkspaceFileLease(
        string workspaceRoot,
        string targetPath,
        int maximumBytes,
        bool writable,
        bool requireInstructionName,
        DirectoryPathGuard directoryGuard,
        SafeFileHandle fileHandle,
        FileIdentity identity,
        IWorkspaceFileReplacementOperations replacementOperations)
    {
        _workspaceRoot = workspaceRoot;
        _targetPath = targetPath;
        _maximumBytes = maximumBytes;
        _writable = writable;
        _requireInstructionName = requireInstructionName;
        _directoryGuard = directoryGuard;
        _fileHandle = fileHandle;
        _identity = identity;
        _replacementOperations = replacementOperations;
    }

    internal static WindowsWorkspaceFileLease OpenForRead(
        string workspacePath,
        string relativePath,
        int maximumBytes,
        bool requireInstructionName = false) => Open(
            workspacePath,
            relativePath,
            maximumBytes,
            writable: false,
            requireInstructionName);

    internal static WindowsWorkspaceFileLease OpenForWrite(
        string workspacePath,
        string relativePath,
        int maximumBytes,
        IWorkspaceFileReplacementOperations? replacementOperations = null) => Open(
            workspacePath,
            relativePath,
            maximumBytes,
            writable: true,
            requireInstructionName: true,
            replacementOperations);

    internal static async Task CreateInstructionFileAsync(
        string workspacePath,
        string relativePath,
        ReadOnlyMemory<byte> content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        if (content.Length > maximumBytes)
        {
            throw new InvalidDataException("The workspace file exceeds the writable size limit.");
        }

        var workspaceRoot = NormalizeWorkspaceRoot(workspacePath);
        var targetPath = ResolveTarget(workspaceRoot, relativePath);
        var parent = Path.GetDirectoryName(targetPath) ??
            throw new InvalidDataException("The workspace file has no parent directory.");
        using var directoryGuard = DirectoryPathGuard.Acquire(parent);
        using var handle = OpenFile(
            targetPath,
            GenericRead | GenericWrite | DeleteAccess | FileReadAttributes,
            0,
            CreateNew,
            FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagWriteThrough);

        var completed = false;
        SetDeleteDisposition(handle, delete: true);
        try
        {
            var opened = ValidateOpenedHandle(
                handle,
                targetPath,
                expectDirectory: false,
                requireInstructionName: true);
            EnsureContained(workspaceRoot, opened.FinalPath);
            await RandomAccess.WriteAsync(
                    handle,
                    content,
                    fileOffset: 0,
                    cancellationToken)
                .ConfigureAwait(false);
            RandomAccess.FlushToDisk(handle);
            cancellationToken.ThrowIfCancellationRequested();

            var validated = ValidateOpenedHandle(
                handle,
                targetPath,
                expectDirectory: false,
                requireInstructionName: true);
            if (validated.Identity != opened.Identity)
            {
                throw new InvalidDataException(
                    "The workspace file identity changed while it was being created.");
            }
            directoryGuard.Validate();
            SetDeleteDisposition(handle, delete: false);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                try
                {
                    SetDeleteDisposition(handle, delete: true);
                }
                catch (Exception exception) when (exception is IOException or Win32Exception)
                {
                }
            }
        }
    }

    internal async Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken)
    {
        var handle = GetHandle();
        var length = ValidateLength(handle);
        var bytes = new byte[checked((int)length)];
        try
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await RandomAccess.ReadAsync(
                        handle,
                        bytes.AsMemory(offset),
                        offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("The workspace file changed while it was being read.");
                }
                offset += read;
            }

            ValidateStableTarget();
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    internal async Task ReplaceAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        if (!_writable)
        {
            throw new InvalidOperationException("The workspace file lease is read-only.");
        }
        if (Interlocked.Exchange(ref _replaceStarted, 1) != 0)
        {
            throw new InvalidOperationException("The workspace file lease was already used.");
        }
        if (content.Length > _maximumBytes)
        {
            throw new InvalidDataException("The workspace file exceeds the writable size limit.");
        }

        var targetHandle = GetHandle();
        ValidateStableTarget();
        var originalIdentity = _identity;
        var originalLength = checked((int)ValidateLength(targetHandle));
        var parent = Path.GetDirectoryName(_targetPath) ??
            throw new InvalidDataException("The workspace file has no parent directory.");
        var replacementId = Guid.NewGuid().ToString("N");
        var temporaryPath = Path.Combine(
            parent,
            $".AGENTS.md.agentdesk-{replacementId}.tmp");
        var backupPath = Path.Combine(
            parent,
            $".AGENTS.md.agentdesk-{replacementId}.bak");
        var recoveryPath = Path.Combine(
            parent,
            $".AGENTS.md.agentdesk-{replacementId}.recovery");
        SafeFileHandle? temporaryHandle = OpenFile(
            temporaryPath,
            GenericWrite | DeleteAccess | WriteOwner | FileReadAttributes,
            0,
            CreateNew,
            FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagWriteThrough);
        var temporaryIdentity = default(FileIdentity);
        var temporaryValidated = false;
        var replacementSucceeded = false;
        var preserveTemporary = false;
        try
        {
            var temporary = ValidateOpenedHandle(
                temporaryHandle,
                temporaryPath,
                expectDirectory: false,
                requireInstructionName: false);
            EnsureContained(_workspaceRoot, temporary.FinalPath);
            temporaryIdentity = temporary.Identity;
            temporaryValidated = true;
            SetDeleteDisposition(temporaryHandle, delete: true);
            CopyOwner(targetHandle, temporaryHandle);
            await RandomAccess.WriteAsync(
                    temporaryHandle,
                    content,
                    fileOffset: 0,
                    cancellationToken)
                .ConfigureAwait(false);
            RandomAccess.FlushToDisk(temporaryHandle);
            cancellationToken.ThrowIfCancellationRequested();

            ValidateStableTarget();
            _directoryGuard.Validate();
            SetDeleteDisposition(temporaryHandle, delete: false);
            _ = ValidateOpenedHandle(
                temporaryHandle,
                temporaryPath,
                expectDirectory: false,
                requireInstructionName: false);
            targetHandle.Dispose();
            _fileHandle = null;
            temporaryHandle.Dispose();
            temporaryHandle = null;

            try
            {
                _replacementOperations.Replace(_targetPath, temporaryPath, backupPath);
                replacementSucceeded = true;
            }
            catch (WorkspaceFileReplacementException exception)
                when (IsPartialReplacementFailure(exception.NativeErrorCode))
            {
                preserveTemporary = true;
                try
                {
                    RecoverPartialReplacement(backupPath, originalIdentity);
                }
                catch (Exception recoveryException)
                {
                    throw new IOException(
                        "The workspace file replacement failed and the original file could not be restored.",
                        new AggregateException(exception, recoveryException));
                }
                throw;
            }

            try
            {
                ValidateCompletedReplacement(
                    backupPath,
                    temporaryIdentity,
                    originalIdentity,
                    originalLength);
            }
            catch (Exception validationException)
            {
                preserveTemporary = true;
                try
                {
                    RollBackCompletedReplacement(
                        backupPath,
                        recoveryPath,
                        temporaryIdentity,
                        originalIdentity);
                }
                catch (Exception rollbackException)
                {
                    throw new IOException(
                        "The workspace replacement could not be validated or rolled back.",
                        new AggregateException(validationException, rollbackException));
                }
                throw new IOException(
                    "The workspace replacement failed validation and was rolled back.",
                    validationException);
            }
        }
        finally
        {
            if (!replacementSucceeded && !preserveTemporary)
            {
                if (temporaryHandle is not null)
                {
                    DeleteOrEraseTemporaryFile(temporaryHandle, content.Length);
                }
                else if (temporaryValidated)
                {
                    DeleteOrEraseTemporaryPath(
                        temporaryPath,
                        temporaryIdentity,
                        content.Length);
                }
            }
            temporaryHandle?.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _fileHandle?.Dispose();
        _fileHandle = null;
        _directoryGuard.Dispose();
    }

    private static WindowsWorkspaceFileLease Open(
        string workspacePath,
        string relativePath,
        int maximumBytes,
        bool writable,
        bool requireInstructionName,
        IWorkspaceFileReplacementOperations? replacementOperations = null)
    {
        if (maximumBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var workspaceRoot = NormalizeWorkspaceRoot(workspacePath);
        var targetPath = ResolveTarget(workspaceRoot, relativePath);
        var parent = Path.GetDirectoryName(targetPath) ??
            throw new InvalidDataException("The workspace file has no parent directory.");
        var directoryGuard = DirectoryPathGuard.Acquire(parent);
        SafeFileHandle? fileHandle = null;
        try
        {
            fileHandle = OpenFile(
                targetPath,
                GenericRead | FileReadAttributes,
                FileShareRead,
                OpenExisting,
                FileAttributeNormal | FileFlagOpenReparsePoint);
            var opened = ValidateOpenedHandle(
                fileHandle,
                targetPath,
                expectDirectory: false,
                requireInstructionName);
            EnsureContained(workspaceRoot, opened.FinalPath);
            var length = RandomAccess.GetLength(fileHandle);
            if (length < 0 || length > maximumBytes)
            {
                throw new InvalidDataException(
                    "The workspace file exceeds the supported size limit.");
            }

            return new WindowsWorkspaceFileLease(
                workspaceRoot,
                targetPath,
                maximumBytes,
                writable,
                requireInstructionName,
                directoryGuard,
                fileHandle,
                opened.Identity,
                replacementOperations ?? DefaultReplacementOperations);
        }
        catch
        {
            fileHandle?.Dispose();
            directoryGuard.Dispose();
            throw;
        }
    }

    private SafeFileHandle GetHandle()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _fileHandle is { IsInvalid: false, IsClosed: false } handle
            ? handle
            : throw new InvalidOperationException("The workspace file handle is unavailable.");
    }

    private long ValidateLength(SafeFileHandle handle)
    {
        var length = RandomAccess.GetLength(handle);
        if (length < 0 || length > _maximumBytes)
        {
            throw new InvalidDataException("The workspace file exceeds the supported size limit.");
        }
        return length;
    }

    private void ValidateStableTarget()
    {
        var handle = GetHandle();
        var opened = ValidateOpenedHandle(
            handle,
            _targetPath,
            expectDirectory: false,
            _requireInstructionName);
        EnsureContained(_workspaceRoot, opened.FinalPath);
        if (opened.Identity != _identity)
        {
            throw new InvalidDataException("The workspace file identity changed during access.");
        }
        _directoryGuard.Validate();
    }

    private static string NormalizeWorkspaceRoot(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The workspace directory does not exist.");
        }
        return root;
    }

    private static string ResolveTarget(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':') ||
            relativePath.Contains('\\'))
        {
            throw new ArgumentException("The workspace file path must be relative.", nameof(relativePath));
        }
        var segments = relativePath.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException("The workspace file path is invalid.", nameof(relativePath));
        }

        var target = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        EnsureContained(root, target);
        return target;
    }

    private static void EnsureContained(string root, string path)
    {
        var prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The workspace file path leaves the workspace.");
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

        var errorCode = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw errorCode switch
        {
            ErrorFileNotFound => new FileNotFoundException(
                "The workspace file does not exist.",
                path,
                new Win32Exception(errorCode)),
            ErrorPathNotFound => new DirectoryNotFoundException(
                "The workspace file directory does not exist.",
                new Win32Exception(errorCode)),
            _ => NativeFailure("The workspace file could not be opened.", errorCode),
        };
    }

    private static OpenedFile ValidateOpenedHandle(
        SafeFileHandle handle,
        string expectedPath,
        bool expectDirectory,
        bool requireInstructionName)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfo,
                out var tagInfo,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            throw NativeFailure("The workspace file attributes could not be read.");
        }
        var attributes = (FileAttributes)tagInfo.FileAttributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            ((attributes & FileAttributes.Directory) != 0) != expectDirectory)
        {
            throw new InvalidDataException("Workspace context cannot use reparse points.");
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
            throw new InvalidDataException("The workspace path changed during access.");
        }
        if (requireInstructionName && !string.Equals(
                Path.GetFileName(finalPath),
                "AGENTS.md",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The instruction file name casing is invalid.");
        }
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw NativeFailure("The workspace file identity could not be read.");
        }
        if (!expectDirectory && information.NumberOfLinks > 1)
        {
            throw new InvalidDataException(
                "Workspace context cannot use files with multiple hard links.");
        }

        return new OpenedFile(
            finalPath,
            new FileIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow));
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
        {
            throw NativeFailure("The workspace final path could not be resolved.");
        }
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
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

    private void ValidateCompletedReplacement(
        string backupPath,
        FileIdentity replacementIdentity,
        FileIdentity originalIdentity,
        int originalLength)
    {
        SafeFileHandle? replacedHandle = null;
        try
        {
            replacedHandle = OpenValidatedWorkspaceFile(
                _targetPath,
                requireInstructionName: true,
                replacementIdentity);
            using (OpenValidatedWorkspaceFile(
                       backupPath,
                       requireInstructionName: false,
                       originalIdentity))
            {
            }
            _directoryGuard.Validate();
            _identity = replacementIdentity;
            _fileHandle = replacedHandle;
            replacedHandle = null;
        }
        finally
        {
            replacedHandle?.Dispose();
        }

        DeleteOrEraseTemporaryPath(backupPath, originalIdentity, originalLength);
    }

    private void RecoverPartialReplacement(
        string backupPath,
        FileIdentity originalIdentity)
    {
        _directoryGuard.Validate();
        var target = TryInspectWorkspaceFile(_targetPath, requireInstructionName: true);
        if (target is not null)
        {
            if (target.Value.Identity != originalIdentity)
            {
                throw new InvalidDataException(
                    "The workspace target changed while replacement recovery was running.");
            }

            AdoptTargetFile(originalIdentity);
            return;
        }

        var backup = TryInspectWorkspaceFile(backupPath, requireInstructionName: false);
        if (backup is null || backup.Value.Identity != originalIdentity)
        {
            throw new InvalidDataException(
                "The original workspace file backup is unavailable.");
        }

        _replacementOperations.Move(backupPath, _targetPath);
        _directoryGuard.Validate();
        AdoptTargetFile(originalIdentity);
    }

    private void RollBackCompletedReplacement(
        string backupPath,
        string recoveryPath,
        FileIdentity replacementIdentity,
        FileIdentity originalIdentity)
    {
        var target = TryInspectWorkspaceFile(_targetPath, requireInstructionName: true);
        var backup = TryInspectWorkspaceFile(backupPath, requireInstructionName: false);
        if (target is null || target.Value.Identity != replacementIdentity ||
            backup is null || backup.Value.Identity != originalIdentity)
        {
            throw new InvalidDataException(
                "The workspace replacement could not be safely rolled back.");
        }

        _replacementOperations.Replace(_targetPath, backupPath, recoveryPath);
        using (OpenValidatedWorkspaceFile(
                   recoveryPath,
                   requireInstructionName: false,
                   replacementIdentity))
        {
        }
        _directoryGuard.Validate();
        AdoptTargetFile(originalIdentity);
    }

    private SafeFileHandle OpenValidatedWorkspaceFile(
        string path,
        bool requireInstructionName,
        FileIdentity expectedIdentity)
    {
        SafeFileHandle? handle = OpenFile(
            path,
            GenericRead | FileReadAttributes,
            FileShareRead,
            OpenExisting,
            FileAttributeNormal | FileFlagOpenReparsePoint);
        try
        {
            var opened = ValidateOpenedHandle(
                handle,
                path,
                expectDirectory: false,
                requireInstructionName);
            EnsureContained(_workspaceRoot, opened.FinalPath);
            if (opened.Identity != expectedIdentity)
            {
                throw new InvalidDataException(
                    "The workspace file identity changed during replacement.");
            }

            var result = handle;
            handle = null;
            return result;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private OpenedFile? TryInspectWorkspaceFile(string path, bool requireInstructionName)
    {
        try
        {
            using var handle = OpenFile(
                path,
                GenericRead | FileReadAttributes,
                FileShareRead,
                OpenExisting,
                FileAttributeNormal | FileFlagOpenReparsePoint);
            var opened = ValidateOpenedHandle(
                handle,
                path,
                expectDirectory: false,
                requireInstructionName);
            EnsureContained(_workspaceRoot, opened.FinalPath);
            return opened;
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private void AdoptTargetFile(FileIdentity expectedIdentity)
    {
        var handle = OpenValidatedWorkspaceFile(
            _targetPath,
            requireInstructionName: true,
            expectedIdentity);
        _fileHandle?.Dispose();
        _fileHandle = handle;
        _identity = expectedIdentity;
    }

    private static bool IsPartialReplacementFailure(int nativeErrorCode) =>
        nativeErrorCode is ErrorUnableToRemoveReplaced or
            ErrorUnableToMoveReplacement or
            ErrorUnableToMoveReplacement2;

    private sealed class NativeWorkspaceFileReplacementOperations :
        IWorkspaceFileReplacementOperations
    {
        public void Replace(string targetPath, string replacementPath, string? backupPath)
        {
            if (!ReplaceFile(
                    targetPath,
                    replacementPath,
                    backupPath,
                    replaceFlags: 0,
                    exclude: IntPtr.Zero,
                    reserved: IntPtr.Zero))
            {
                throw new WorkspaceFileReplacementException(
                    "The workspace file could not be atomically replaced.",
                    Marshal.GetLastWin32Error());
            }
        }

        public void Move(string sourcePath, string targetPath)
        {
            if (!MoveFileEx(sourcePath, targetPath, MoveFileWriteThrough))
            {
                throw new WorkspaceFileReplacementException(
                    "The original workspace file could not be restored.",
                    Marshal.GetLastWin32Error());
            }
        }
    }

    private static void CopyOwner(
        SafeFileHandle sourceHandle,
        SafeFileHandle destinationHandle)
    {
        var result = GetSecurityInfo(
            sourceHandle,
            SeFileObject,
            OwnerSecurityInformation,
            out var owner,
            out _,
            out _,
            out _,
            out var securityDescriptor);
        if (result != 0)
        {
            throw NativeFailure(
                "The workspace file owner could not be read.",
                checked((int)result));
        }

        try
        {
            if (owner == IntPtr.Zero)
            {
                throw new InvalidDataException("The workspace file owner is missing.");
            }
            result = SetSecurityInfo(
                destinationHandle,
                SeFileObject,
                OwnerSecurityInformation,
                owner,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            if (result != 0)
            {
                throw NativeFailure(
                    "The workspace file owner could not be preserved.",
                    checked((int)result));
            }
        }
        finally
        {
            if (securityDescriptor != IntPtr.Zero)
            {
                _ = LocalFree(securityDescriptor);
            }
        }
    }

    private static void DeleteOrEraseTemporaryPath(
        string path,
        FileIdentity expectedIdentity,
        int length)
    {
        try
        {
            using var handle = OpenFile(
                path,
                GenericWrite | DeleteAccess | FileReadAttributes,
                FileShareRead,
                OpenExisting,
                FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagWriteThrough);
            var opened = ValidateOpenedHandle(
                handle,
                path,
                expectDirectory: false,
                requireInstructionName: false);
            if (opened.Identity == expectedIdentity)
            {
                DeleteOrEraseTemporaryFile(handle, length);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void DeleteOrEraseTemporaryFile(SafeFileHandle handle, int length)
    {
        try
        {
            RandomAccess.Write(handle, new byte[length], fileOffset: 0);
            RandomAccess.FlushToDisk(handle);
        }
        catch (Exception)
        {
        }

        try
        {
            SetDeleteDisposition(handle, delete: true);
        }
        catch (Exception)
        {
        }
    }

    private static void SetDeleteDisposition(SafeFileHandle handle, bool delete)
    {
        var disposition = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(disposition, delete ? (byte)1 : (byte)0);
            if (!SetFileInformationByHandle(handle, FileDispositionInfo, disposition, 1))
            {
                throw NativeFailure(delete
                    ? "The temporary workspace file could not be hidden."
                    : "The temporary workspace file could not be prepared for replacement.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(disposition);
        }
    }

    private static IOException NativeFailure(string message) =>
        new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    private static IOException NativeFailure(string message, int errorCode) =>
        new(message, new Win32Exception(errorCode));

    private sealed class DirectoryPathGuard : IDisposable
    {
        private readonly List<(SafeFileHandle Handle, string Path)> _entries;
        private int _disposed;

        private DirectoryPathGuard(List<(SafeFileHandle, string)> entries)
        {
            _entries = entries;
        }

        internal static DirectoryPathGuard Acquire(string directoryPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
            var fullPath = Path.GetFullPath(directoryPath);
            var root = Path.GetPathRoot(fullPath) ??
                throw new InvalidDataException("The workspace directory has no root.");
            var segments = fullPath[root.Length..].Split(
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

            var entries = new List<(SafeFileHandle, string)>(paths.Count);
            try
            {
                foreach (var path in paths)
                {
                    var handle = OpenDirectory(path);
                    try
                    {
                        _ = ValidateOpenedHandle(
                            handle,
                            path,
                            expectDirectory: true,
                            requireInstructionName: false);
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
                _ = ValidateOpenedHandle(
                    entry.Handle,
                    entry.Path,
                    expectDirectory: true,
                    requireInstructionName: false);
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

    private readonly record struct OpenedFile(string FinalPath, FileIdentity Identity);

    private readonly record struct FileIdentity(uint VolumeSerialNumber, ulong FileIndex);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileAttributeTagInformation
    {
        public readonly uint FileAttributes;
        public readonly uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
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
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReplaceFile(
        string replacedFileName,
        string replacementFileName,
        string? backupFileName,
        uint replaceFlags,
        IntPtr exclude,
        IntPtr reserved);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInfo,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInfo,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
