using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentDesk.Engine.Sidecar;

internal sealed class WindowsJobObjectApi : IWindowsJobObjectApi
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    public static WindowsJobObjectApi Instance { get; } = new();

    private WindowsJobObjectApi()
    {
    }

    public SafeHandle CreateJobObject()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            throw CreateWin32Exception("create the sidecar Job Object");
        }

        return new SafeJobObjectHandle(handle);
    }

    public void ConfigureKillOnClose(SafeHandle jobObject)
    {
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        if (!SetInformationJobObject(
                jobObject,
                JobObjectExtendedLimitInformationClass,
                ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            throw CreateWin32Exception("configure the sidecar Job Object");
        }
    }

    public void AssignProcess(
        SafeHandle jobObject,
        SafeHandle processHandle,
        int processId)
    {
        if (!AssignProcessToJobObject(jobObject, processHandle))
        {
            throw CreateWin32Exception(
                $"assign sidecar process {processId} to its Job Object");
        }
    }

    private static Win32Exception CreateWin32Exception(string operation)
    {
        var errorCode = Marshal.GetLastWin32Error();
        return new Win32Exception(
            errorCode,
            $"Windows could not {operation} (error {errorCode}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobObjectHandle(IntPtr handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeHandle jobObject,
        int jobObjectInformationClass,
        ref JobObjectExtendedLimitInformation jobObjectInformation,
        uint jobObjectInformationLength);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeHandle jobObject,
        SafeHandle process);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
