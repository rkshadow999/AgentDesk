using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentDesk.Engine.Sidecar;

internal sealed class WindowsSuspendedProcessLauncher
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint ProcThreadAttributeHandleList = 0x00020002;
    private const uint ResumeThreadFailed = uint.MaxValue;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint WaitTimeoutMilliseconds = 5_000;

    private readonly IWindowsJobObjectApi _jobObjectApi;

    public WindowsSuspendedProcessLauncher(IWindowsJobObjectApi jobObjectApi)
    {
        _jobObjectApi = jobObjectApi;
    }

    public ISidecarProcess Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        AnonymousPipeServerStream? standardInput = null;
        AnonymousPipeServerStream? standardOutput = null;
        AnonymousPipeServerStream? standardError = null;
        SafeHandle? jobObject = null;
        Process? process = null;
        nint environmentBlock = 0;
        nint attributeList = 0;
        nint inheritedHandles = 0;
        var processInformation = default(ProcessInformation);

        try
        {
            standardInput = CreatePipe(PipeDirection.Out);
            standardOutput = CreatePipe(PipeDirection.In);
            standardError = CreatePipe(PipeDirection.In);
            ConfigurePipeInheritance(standardInput);
            ConfigurePipeInheritance(standardOutput);
            ConfigurePipeInheritance(standardError);

            jobObject = _jobObjectApi.CreateJobObject();
            _jobObjectApi.ConfigureKillOnClose(jobObject);

            inheritedHandles = BuildInheritedHandleList(
                standardInput.ClientSafePipeHandle,
                standardOutput.ClientSafePipeHandle,
                standardError.ClientSafePipeHandle);
            attributeList = BuildAttributeList(inheritedHandles, handleCount: 3);
            environmentBlock = BuildEnvironmentBlock(startInfo.Environment);

            var executablePath = ResolveExecutablePath(
                startInfo.FileName,
                startInfo.Environment);
            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StandardInput = standardInput.ClientSafePipeHandle.DangerousGetHandle(),
                    StandardOutput = standardOutput.ClientSafePipeHandle.DangerousGetHandle(),
                    StandardError = standardError.ClientSafePipeHandle.DangerousGetHandle(),
                },
                AttributeList = attributeList,
            };
            var commandLine = new StringBuilder(
                BuildCommandLine(executablePath, startInfo.ArgumentList));
            var creationFlags = CreateSuspended |
                CreateUnicodeEnvironment |
                ExtendedStartupInfoPresent |
                CreateNoWindow;

            bool created;
            try
            {
                created = CreateProcessW(
                    executablePath,
                    commandLine,
                    0,
                    0,
                    inheritHandles: true,
                    creationFlags,
                    environmentBlock,
                    startInfo.WorkingDirectory,
                    ref startupInfo,
                    out processInformation);
            }
            finally
            {
                standardInput.DisposeLocalCopyOfClientHandle();
                standardOutput.DisposeLocalCopyOfClientHandle();
                standardError.DisposeLocalCopyOfClientHandle();
            }

            if (!created)
            {
                throw CreateWin32Exception($"start suspended process '{startInfo.FileName}'");
            }

            var processId = checked((int)processInformation.ProcessId);
            process = Process.GetProcessById(processId);
            process.EnableRaisingEvents = true;
            _ = process.SafeHandle;

            using (var borrowedProcessHandle = new SafeFileHandle(
                       processInformation.ProcessHandle,
                       ownsHandle: false))
            {
                _jobObjectApi.AssignProcess(jobObject, borrowedProcessHandle, processId);
            }

            if (ResumeThread(processInformation.ThreadHandle) == ResumeThreadFailed)
            {
                throw CreateWin32Exception($"resume sidecar process {processId}");
            }

            var result = new WindowsSidecarProcess(
                process,
                standardInput,
                standardOutput,
                standardError,
                jobObject);
            process = null;
            standardInput = null;
            standardOutput = null;
            standardError = null;
            jobObject = null;
            return result;
        }
        catch
        {
            if (processInformation.ProcessHandle != 0)
            {
                TryTerminateProcess(processInformation.ProcessHandle);
            }

            throw;
        }
        finally
        {
            process?.Dispose();
            jobObject?.Dispose();
            standardInput?.Dispose();
            standardOutput?.Dispose();
            standardError?.Dispose();
            if (processInformation.ThreadHandle != 0)
            {
                _ = CloseHandle(processInformation.ThreadHandle);
            }
            if (processInformation.ProcessHandle != 0)
            {
                _ = CloseHandle(processInformation.ProcessHandle);
            }
            if (attributeList != 0)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (inheritedHandles != 0)
            {
                Marshal.FreeHGlobal(inheritedHandles);
            }
            if (environmentBlock != 0)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    private static AnonymousPipeServerStream CreatePipe(PipeDirection direction) =>
        new(direction, HandleInheritability.Inheritable);

    private static void ConfigurePipeInheritance(AnonymousPipeServerStream pipe)
    {
        SetHandleInheritance(pipe.ClientSafePipeHandle, inheritable: true);
        SetHandleInheritance(pipe.SafePipeHandle, inheritable: false);
    }

    private static void SetHandleInheritance(SafeHandle handle, bool inheritable)
    {
        var flags = inheritable ? HandleFlagInherit : 0;
        if (!SetHandleInformation(
                handle.DangerousGetHandle(),
                HandleFlagInherit,
                flags))
        {
            throw CreateWin32Exception("configure redirected pipe inheritance");
        }
    }

    private static nint BuildInheritedHandleList(
        SafeHandle standardInput,
        SafeHandle standardOutput,
        SafeHandle standardError)
    {
        var pointer = Marshal.AllocHGlobal(IntPtr.Size * 3);
        Marshal.WriteIntPtr(pointer, 0, standardInput.DangerousGetHandle());
        Marshal.WriteIntPtr(pointer, IntPtr.Size, standardOutput.DangerousGetHandle());
        Marshal.WriteIntPtr(pointer, IntPtr.Size * 2, standardError.DangerousGetHandle());
        return pointer;
    }

    private static nint BuildAttributeList(nint inheritedHandles, int handleCount)
    {
        nint size = 0;
        _ = InitializeProcThreadAttributeList(0, 1, 0, ref size);
        if (size == 0)
        {
            throw CreateWin32Exception("size the sidecar process attribute list");
        }

        var attributeList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(attributeList);
            throw CreateWin32Exception("initialize the sidecar process attribute list");
        }

        if (!UpdateProcThreadAttribute(
                attributeList,
                0,
                ProcThreadAttributeHandleList,
                inheritedHandles,
                (nint)(IntPtr.Size * handleCount),
                0,
                0))
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
            throw CreateWin32Exception("restrict inherited sidecar process handles");
        }

        return attributeList;
    }

    private static nint BuildEnvironmentBlock(
        IEnumerable<KeyValuePair<string, string?>> environment)
    {
        var builder = new StringBuilder();
        foreach (var (name, value) in environment
                     .Where(static pair => pair.Value is not null)
                     .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (name.Contains('\0') || value!.Contains('\0'))
            {
                throw new InvalidOperationException(
                    "Process environment names and values must not contain null characters.");
            }

            builder.Append(name).Append('=').Append(value).Append('\0');
        }

        if (builder.Length == 0)
        {
            builder.Append('\0');
        }
        builder.Append('\0');

        var bytes = Encoding.Unicode.GetBytes(builder.ToString());
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return pointer;
    }

    internal static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        return BuildCommandLine(startInfo.FileName, startInfo.ArgumentList);
    }

    private static string BuildCommandLine(
        string executablePath,
        IEnumerable<string> arguments)
    {
        var commandLine = new StringBuilder();
        AppendQuotedArgument(commandLine, executablePath);
        foreach (var argument in arguments)
        {
            commandLine.Append(' ');
            AppendQuotedArgument(commandLine, argument);
        }
        return commandLine.ToString();
    }

    private static string ResolveExecutablePath(
        string fileName,
        IEnumerable<KeyValuePair<string, string?>> environment)
    {
        if (Path.IsPathFullyQualified(fileName) ||
            fileName.Contains(Path.DirectorySeparatorChar) ||
            fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            return Path.GetFullPath(fileName);
        }

        var searchDirectories = new List<string> { Environment.SystemDirectory };
        var path = environment.FirstOrDefault(
            static pair => pair.Key.Equals("PATH", StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrEmpty(path))
        {
            searchDirectories.AddRange(path.Split(Path.PathSeparator));
        }

        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawDirectory in searchDirectories)
        {
            var directory = Environment
                .ExpandEnvironmentVariables(rawDirectory.Trim().Trim('"'));
            if (directory.Length == 0 ||
                !Path.IsPathFullyQualified(directory) ||
                !visitedDirectories.Add(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return fileName;
    }

    private static void AppendQuotedArgument(StringBuilder commandLine, string argument)
    {
        if (argument.Length > 0 &&
            !argument.Any(static character => char.IsWhiteSpace(character) || character == '"'))
        {
            commandLine.Append(argument);
            return;
        }

        commandLine.Append('"');
        var backslashCount = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                commandLine.Append('\\', backslashCount * 2 + 1);
                commandLine.Append('"');
                backslashCount = 0;
                continue;
            }

            commandLine.Append('\\', backslashCount);
            backslashCount = 0;
            commandLine.Append(character);
        }

        commandLine.Append('\\', backslashCount * 2);
        commandLine.Append('"');
    }

    private static Win32Exception CreateWin32Exception(string operation)
    {
        var errorCode = Marshal.GetLastWin32Error();
        return new Win32Exception(
            errorCode,
            $"Windows could not {operation} (error {errorCode}).");
    }

    private static void TryTerminateProcess(nint processHandle)
    {
        if (TerminateProcess(processHandle, 1))
        {
            _ = WaitForSingleObject(processHandle, WaitTimeoutMilliseconds);
        }
    }

    private sealed class WindowsSidecarProcess : ISidecarProcess
    {
        private readonly Process _process;
        private readonly Stream _standardInput;
        private readonly Stream _standardOutput;
        private readonly Stream _standardError;
        private readonly SafeHandle _jobObject;
        private int _disposed;

        public WindowsSidecarProcess(
            Process process,
            Stream standardInput,
            Stream standardOutput,
            Stream standardError,
            SafeHandle jobObject)
        {
            _process = process;
            _standardInput = standardInput;
            _standardOutput = standardOutput;
            _standardError = standardError;
            _jobObject = jobObject;
            _process.Exited += OnExited;
        }

        public event EventHandler? Exited;

        public Stream StandardInput => _standardInput;

        public Stream StandardOutput => _standardOutput;

        public Stream StandardError => _standardError;

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _process.WaitForExitAsync(cancellationToken);

        public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _process.Exited -= OnExited;
            try
            {
                _jobObject.Dispose();
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(WaitTimeoutMilliseconds));
                try
                {
                    await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                }
            }
            finally
            {
                _standardInput.Dispose();
                _standardOutput.Dispose();
                _standardError.Dispose();
                _process.Dispose();
            }
        }

        private void OnExited(object? sender, EventArgs args)
        {
            Exited?.Invoke(this, EventArgs.Empty);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short ReservedSize;
        public nint ReservedPointer;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint ProcessHandle;
        public nint ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(
        nint objectHandle,
        uint mask,
        uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        nint attributeList,
        int attributeCount,
        int flags,
        ref nint size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nuint attribute,
        nint value,
        nint size,
        nint previousValue,
        nint returnSize);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void DeleteProcThreadAttributeList(nint attributeList);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint ResumeThread(nint threadHandle);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint processHandle, uint exitCode);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
