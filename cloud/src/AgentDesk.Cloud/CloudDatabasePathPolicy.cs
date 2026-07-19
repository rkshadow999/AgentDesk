using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

namespace AgentDesk.Cloud;

internal static class CloudDatabasePathPolicy
{
    private const int AtFileDescriptorCurrentWorkingDirectory = -100;
    private const int AtSymbolicLinkNoFollow = 0x100;
    private const uint StatxLinkCount = 0x00000004;

    public static bool IsValid(CloudOptions options)
    {
        try
        {
            _ = NormalizeAndValidate(options.DatabasePath);
            return true;
        }
        catch (Exception error) when (
            error is ArgumentException or IOException or InvalidDataException or
            PlatformNotSupportedException or
            UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }

    public static string NormalizeAndValidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException(
                "The AgentDesk Cloud database must use a fully qualified local path.");
        }

        var fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) ||
                path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The AgentDesk Cloud database must use a fully qualified local path.");
            }
            var rootLength = Path.GetPathRoot(fullPath)?.Length ?? 0;
            if (fullPath.IndexOf(':', rootLength) >= 0)
            {
                throw new InvalidDataException(
                    "Alternate data streams are not allowed for the AgentDesk Cloud database.");
            }
        }

        var probePath = File.Exists(fullPath) || Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath)!;
        while (!File.Exists(probePath) && !Directory.Exists(probePath))
        {
            probePath = Directory.GetParent(probePath)?.FullName ?? throw new InvalidDataException(
                "The AgentDesk Cloud database path has no accessible local ancestor.");
        }

        FileSystemInfo? current = File.Exists(probePath)
            ? new FileInfo(probePath)
            : new DirectoryInfo(probePath);
        while (current is not null)
        {
            current.Refresh();
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Reparse points are not allowed in the AgentDesk Cloud database path.");
            }
            current = current switch
            {
                DirectoryInfo directory => directory.Parent,
                FileInfo file => file.Directory,
                _ => null,
            };
        }

        if (File.Exists(fullPath) && GetHardLinkCount(fullPath) > 1)
        {
            throw new InvalidDataException(
                "The AgentDesk Cloud database cannot have multiple hard links.");
        }
        return fullPath;
    }

    private static uint GetHardLinkCount(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using SafeFileHandle handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return information.NumberOfLinks;
        }
        if (OperatingSystem.IsLinux())
        {
            if (Statx(
                    AtFileDescriptorCurrentWorkingDirectory,
                    path,
                    AtSymbolicLinkNoFollow,
                    StatxLinkCount,
                    out var information) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            return information.LinkCount;
        }

        throw new PlatformNotSupportedException(
            "Hard-link validation for the Cloud database is supported on Windows and Linux.");
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

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxInformation
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
        public ushort Reserved0;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public StatxTimestamp AccessTime;
        public StatxTimestamp BirthTime;
        public StatxTimestamp ChangeTime;
        public StatxTimestamp ModificationTime;
        public uint DeviceIdMajor;
        public uint DeviceIdMinor;
        public uint DeviceMajor;
        public uint DeviceMinor;
        public ulong Reserved1;
        public ulong Reserved2;
        public ulong Reserved3;
        public ulong Reserved4;
        public ulong Reserved5;
        public ulong Reserved6;
        public ulong Reserved7;
        public ulong Reserved8;
        public ulong Reserved9;
        public ulong Reserved10;
        public ulong Reserved11;
        public ulong Reserved12;
        public ulong Reserved13;
        public ulong Reserved14;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        out StatxInformation information);
}
