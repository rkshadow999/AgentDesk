using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AgentDesk.Engine.Sidecar;

public interface IWslEngineInstallationVerifier
{
    bool IsCurrent(
        string wslExecutablePath,
        string distributionName,
        string bundledEnginePath);
}

public sealed class SystemWslEngineInstallationVerifier : IWslEngineInstallationVerifier
{
    public const string InstalledEnginePath = "/usr/local/bin/agentdesk-engine";

    public static SystemWslEngineInstallationVerifier Instance { get; } = new();

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HashTimeout = TimeSpan.FromSeconds(30);

    private SystemWslEngineInstallationVerifier()
    {
    }

    public bool IsCurrent(
        string wslExecutablePath,
        string distributionName,
        string bundledEnginePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wslExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(distributionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledEnginePath);

        try
        {
            if (!IsCompatibleElf(bundledEnginePath, RuntimeInformation.ProcessArchitecture))
            {
                return false;
            }

            byte[] bundledHash;
            using (var source = new FileStream(
                bundledEnginePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                bundledHash = SHA256.HashData(source);
            }

            var executable = RunWsl(
                wslExecutablePath,
                distributionName,
                ["test", "-x", InstalledEnginePath],
                CommandTimeout);
            if (executable is not { ExitCode: 0 })
            {
                return false;
            }

            var installed = RunWsl(
                wslExecutablePath,
                distributionName,
                ["sha256sum", "--", InstalledEnginePath],
                HashTimeout);
            if (installed is not { ExitCode: 0 } ||
                installed.StandardOutput
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() is not { Length: 64 } installedHashText)
            {
                return false;
            }

            byte[] installedHash;
            try
            {
                installedHash = Convert.FromHexString(installedHashText);
            }
            catch (FormatException)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(bundledHash, installedHash);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                IOException or
                System.ComponentModel.Win32Exception or
                UnauthorizedAccessException or
                CryptographicException)
        {
            return false;
        }
    }

    public static bool IsCompatibleElf(
        string path,
        Architecture processArchitecture)
    {
        if (!File.Exists(path) ||
            processArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return false;
        }

        Span<byte> header = stackalloc byte[20];
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            stream.ReadExactly(header);
        }
        catch (Exception exception) when (
            exception is EndOfStreamException or
                IOException or
                UnauthorizedAccessException)
        {
            return false;
        }

        if (header[0] != 0x7f ||
            header[1] != (byte)'E' ||
            header[2] != (byte)'L' ||
            header[3] != (byte)'F' ||
            header[4] != 2 ||
            header[5] != 1)
        {
            return false;
        }

        var elfType = BitConverter.ToUInt16(header[16..18]);
        var machine = BitConverter.ToUInt16(header[18..20]);
        return elfType is 2 or 3 &&
            machine == (processArchitecture is Architecture.X64 ? 0x3e : 0xb7);
    }

    private static WslCommandResult? RunWsl(
        string wslExecutablePath,
        string distributionName,
        IReadOnlyList<string> commandArguments,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = wslExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("--distribution");
        startInfo.ArgumentList.Add(distributionName);
        startInfo.ArgumentList.Add("--exec");
        foreach (var argument in commandArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return null;
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            TryKill(process);
            return null;
        }

        return new WslCommandResult(
            process.ExitCode,
            output.GetAwaiter().GetResult(),
            error.GetAwaiter().GetResult());
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(500);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                NotSupportedException or
                System.ComponentModel.Win32Exception)
        {
        }
    }

    private sealed record WslCommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
