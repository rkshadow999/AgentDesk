using System.ComponentModel;
using System.Diagnostics;

namespace AgentDesk.Engine.Sidecar;

public sealed class SystemSidecarProcessFactory : ISidecarProcessFactory
{
    private readonly IWindowsJobObjectApi? _windowsJobObjectApi;

    private static readonly string[] SensitiveNameFragments =
    [
        "API_KEY",
        "ACCESS_KEY",
        "PRIVATE_KEY",
        "SECRET",
        "TOKEN",
        "PASSWORD",
        "PASSWD",
        "CREDENTIAL",
    ];

    private static readonly HashSet<string> SensitiveNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "DOCKER_AUTH_CONFIG",
        "GOOGLE_APPLICATION_CREDENTIALS",
        "GPG_AGENT_INFO",
        "KUBECONFIG",
        "SSH_AUTH_SOCK",
    };

    public SystemSidecarProcessFactory()
        : this(OperatingSystem.IsWindows() ? WindowsJobObjectApi.Instance : null)
    {
    }

    internal SystemSidecarProcessFactory(IWindowsJobObjectApi? windowsJobObjectApi)
    {
        _windowsJobObjectApi = windowsJobObjectApi;
    }

    public Task<ISidecarProcess> StartAsync(
        SidecarProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        cancellationToken.ThrowIfCancellationRequested();

        var processStartInfo = new ProcessStartInfo
        {
            FileName = startInfo.FileName,
            WorkingDirectory = startInfo.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in startInfo.Arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        foreach (var name in processStartInfo.Environment.Keys.ToArray())
        {
            if (IsSensitiveEnvironmentVariable(name))
            {
                processStartInfo.Environment.Remove(name);
            }
        }

        foreach (var (name, value) in startInfo.Environment)
        {
            if (IsDesktopCredentialVariable(name))
            {
                processStartInfo.Environment.Remove(name);
                continue;
            }

            if (value is null)
            {
                processStartInfo.Environment.Remove(name);
            }
            else
            {
                processStartInfo.Environment[name] = value;
            }
        }

        try
        {
            if (_windowsJobObjectApi is not null)
            {
                var launcher = new WindowsSuspendedProcessLauncher(_windowsJobObjectApi);
                return Task.FromResult(launcher.Start(processStartInfo));
            }

            var process = new Process
            {
                StartInfo = processStartInfo,
                EnableRaisingEvents = true,
            };
            var processStarted = false;
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException(
                        $"The sidecar process could not be started: {startInfo.FileName}");
                }

                processStarted = true;
                return Task.FromResult<ISidecarProcess>(
                    new SystemSidecarProcess(process));
            }
            catch
            {
                if (processStarted)
                {
                    TryKill(process);
                }

                process.Dispose();
                throw;
            }
        }
        finally
        {
            processStartInfo.Environment.Remove("XAI_API_KEY");
            processStartInfo.Environment.Remove("GROK_CODE_XAI_API_KEY");
        }
    }

    private static bool IsSensitiveEnvironmentVariable(string name)
    {
        if (name.StartsWith("GROK_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("XAI_", StringComparison.OrdinalIgnoreCase) ||
            SensitiveNames.Contains(name))
        {
            return true;
        }

        return SensitiveNameFragments.Any(
            fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDesktopCredentialVariable(string name) =>
        name.Equals("XAI_API_KEY", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GROK_CODE_XAI_API_KEY", StringComparison.OrdinalIgnoreCase);

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // Preserve the startup failure that triggered cleanup.
        }
    }

    private sealed class SystemSidecarProcess : ISidecarProcess
    {
        private readonly Process _process;

        public SystemSidecarProcess(Process process)
        {
            _process = process;
            _process.Exited += OnExited;
        }

        public event EventHandler? Exited;

        public Stream StandardInput => _process.StandardInput.BaseStream;

        public Stream StandardOutput => _process.StandardOutput.BaseStream;

        public Stream StandardError => _process.StandardError.BaseStream;

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _process.WaitForExitAsync(cancellationToken);

        public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);

        public ValueTask DisposeAsync()
        {
            _process.Exited -= OnExited;
            _process.Dispose();
            return ValueTask.CompletedTask;
        }

        private void OnExited(object? sender, EventArgs args)
        {
            Exited?.Invoke(this, EventArgs.Empty);
        }
    }
}
