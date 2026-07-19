using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace AgentDesk.Engine.Sidecar;

public interface IWslDistributionResolver
{
    string? Resolve(string wslExecutablePath);
}

public sealed class SystemWslDistributionResolver : IWslDistributionResolver
{
    public const string ConfigurationEnvironmentVariable =
        "AGENTDESK_WSL_DISTRIBUTION";

    public static SystemWslDistributionResolver Instance { get; } = new();

    private SystemWslDistributionResolver()
    {
    }

    public string? Resolve(string wslExecutablePath) =>
        WslDistributionSelector.TryResolveInstalled(
            wslExecutablePath,
            Environment.GetEnvironmentVariable(ConfigurationEnvironmentVariable));
}

public static class WslDistributionSelector
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);

    public static string? Select(
        IEnumerable<string> installedDistributions,
        string? configuredName)
    {
        ArgumentNullException.ThrowIfNull(installedDistributions);

        var eligible = installedDistributions
            .Select(static name => name?.Trim())
            .Where(static name => IsSafeName(name) && !IsDockerDesktop(name!))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(configuredName))
        {
            var requested = configuredName.Trim();
            if (!IsSafeName(requested) || IsDockerDesktop(requested))
            {
                return null;
            }

            return eligible.SingleOrDefault(
                name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
        }

        return eligible.Length == 1 ? eligible[0] : null;
    }

    public static bool IsSafeName([NotNullWhen(true)] string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        !name.Any(char.IsControl);

    internal static string? TryResolveInstalled(
        string wslExecutablePath,
        string? configuredName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wslExecutablePath);
        if (!File.Exists(wslExecutablePath) &&
            !string.Equals(wslExecutablePath, "wsl.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = wslExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Unicode,
            StandardErrorEncoding = Encoding.Unicode,
        };
        startInfo.ArgumentList.Add("--list");
        startInfo.ArgumentList.Add("--quiet");

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)QueryTimeout.TotalMilliseconds))
            {
                TryKill(process);
                return null;
            }

            _ = error.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                return null;
            }

            var installed = output
                .GetAwaiter()
                .GetResult()
                .Split(
                    ['\r', '\n', '\0'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Select(installed, configuredName);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                IOException or
                System.ComponentModel.Win32Exception or
                UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsDockerDesktop(string name) =>
        name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase);

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
}
