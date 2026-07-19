using AgentDesk.Core.Execution;
using AgentDesk.Core.Providers;

namespace AgentDesk.Engine.Sidecar;

public sealed class SidecarCommandBuilder
{
    private readonly IWslPathConverter _wslPathConverter;
    private readonly IWslDistributionResolver _wslDistributionResolver;
    private readonly IWslEngineInstallationVerifier _wslEngineInstallationVerifier;

    public SidecarCommandBuilder(IWslPathConverter wslPathConverter)
        : this(
            wslPathConverter,
            SystemWslDistributionResolver.Instance,
            SystemWslEngineInstallationVerifier.Instance)
    {
    }

    public SidecarCommandBuilder(
        IWslPathConverter wslPathConverter,
        IWslDistributionResolver wslDistributionResolver)
        : this(
            wslPathConverter,
            wslDistributionResolver,
            SystemWslEngineInstallationVerifier.Instance)
    {
    }

    public SidecarCommandBuilder(
        IWslPathConverter wslPathConverter,
        IWslDistributionResolver wslDistributionResolver,
        IWslEngineInstallationVerifier wslEngineInstallationVerifier)
    {
        ArgumentNullException.ThrowIfNull(wslPathConverter);
        ArgumentNullException.ThrowIfNull(wslDistributionResolver);
        ArgumentNullException.ThrowIfNull(wslEngineInstallationVerifier);
        _wslPathConverter = wslPathConverter;
        _wslDistributionResolver = wslDistributionResolver;
        _wslEngineInstallationVerifier = wslEngineInstallationVerifier;
    }

    public async Task<SidecarProcessStartInfo> BuildAsync(
        SidecarLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkspacePath);

        if (!Directory.Exists(options.WorkspacePath))
        {
            throw new SidecarStartException(
                SidecarStartFailure.WorkspaceNotFound,
                $"The workspace directory does not exist: {options.WorkspacePath}");
        }

        var agentArguments = BuildAgentArguments(options.ProviderProfile);

        var engineDataPath = options.EngineDataPath is null
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentDesk",
                "Engine")
            : NormalizeEngineDataPath(options.EngineDataPath);
        if (options.ExecutionProfile is ExecutionProfile.NativeProtected)
        {
            var nativeEnvironment = BuildEnvironment(
                engineDataPath,
                sandboxProfile: "off",
                requireSandboxEnforcement: false);
            var enginePath = options.EnginePath ??
                Path.Combine(AppContext.BaseDirectory, "agentdesk-engine.exe");
            if (!File.Exists(enginePath))
            {
                throw new SidecarStartException(
                    SidecarStartFailure.EngineNotFound,
                    $"The AgentDesk engine executable does not exist: {enginePath}");
            }

            return new SidecarProcessStartInfo(
                enginePath,
                agentArguments,
                options.WorkspacePath,
                options.WorkspacePath,
                nativeEnvironment);
        }

        const string wslExecutablePath = "wsl.exe";
        var distributionName = _wslDistributionResolver.Resolve(wslExecutablePath);
        if (!WslDistributionSelector.IsSafeName(distributionName))
        {
            throw new SidecarStartException(
                SidecarStartFailure.WslUnavailable,
                "AgentDesk requires exactly one non-Docker WSL distribution, or an installed " +
                $"distribution named by {SystemWslDistributionResolver.ConfigurationEnvironmentVariable}.");
        }
        if (string.IsNullOrWhiteSpace(options.EnginePath) ||
            !_wslEngineInstallationVerifier.IsCurrent(
                wslExecutablePath,
                distributionName,
                options.EnginePath))
        {
            throw new SidecarStartException(
                SidecarStartFailure.EngineNotFound,
                "The installed AgentDesk WSL engine is missing, incompatible, or does not " +
                "match the bundled Linux payload.");
        }

        string engineWorkspacePath;
        try
        {
            engineWorkspacePath = await _wslPathConverter
                .ConvertAsync(options.WorkspacePath, distributionName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SidecarStartException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new SidecarStartException(
                SidecarStartFailure.WorkspacePathConversionFailed,
                $"The workspace path could not be converted for WSL: {options.WorkspacePath}",
                exception);
        }

        if (string.IsNullOrWhiteSpace(engineWorkspacePath))
        {
            throw new SidecarStartException(
                SidecarStartFailure.WorkspacePathConversionFailed,
                $"The workspace path converter returned an empty path: {options.WorkspacePath}");
        }

        string engineDataPathInWsl;
        try
        {
            engineDataPathInWsl = await _wslPathConverter
                .ConvertAsync(engineDataPath, distributionName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SidecarStartException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new SidecarStartException(
                SidecarStartFailure.WorkspacePathConversionFailed,
                $"The AgentDesk engine data path could not be converted for WSL: {engineDataPath}",
                exception);
        }

        if (string.IsNullOrWhiteSpace(engineDataPathInWsl))
        {
            throw new SidecarStartException(
                SidecarStartFailure.WorkspacePathConversionFailed,
                $"The WSL path converter returned an empty engine data path: {engineDataPath}");
        }

        const string wslEnginePath =
            SystemWslEngineInstallationVerifier.InstalledEnginePath;
        var wslEnvironment = BuildEnvironment(
            engineDataPathInWsl,
            sandboxProfile: "strict",
            requireSandboxEnforcement: true);
        wslEnvironment["WSLENV"] = BuildWslEnvironment(
            System.Environment.GetEnvironmentVariable("WSLENV"));
        return new SidecarProcessStartInfo(
            wslExecutablePath,
            [
                "--distribution",
                distributionName,
                "--cd",
                engineWorkspacePath,
                "--exec",
                wslEnginePath,
                .. agentArguments,
            ],
            options.WorkspacePath,
            engineWorkspacePath,
            wslEnvironment);
    }

    private static IReadOnlyList<string> BuildAgentArguments(ProviderProfile? provider)
    {
        var arguments = new List<string>
        {
            "--no-auto-update",
            "agent",
            "--no-leader",
        };
        if (provider is not null)
        {
            var backend = provider.Backend switch
            {
                ProviderBackend.ChatCompletions => "chat_completions",
                ProviderBackend.Responses => "responses",
                _ => throw new NotSupportedException(
                    "The configured AgentDesk provider backend is unsupported."),
            };

            arguments.Add("--model");
            arguments.Add(provider.Model);
            arguments.Add("--agentdesk-openai-base-url");
            arguments.Add(provider.BaseUrl);
            arguments.Add("--agentdesk-openai-backend");
            arguments.Add(backend);
        }

        arguments.Add("stdio");
        return arguments;
    }

    private static string NormalizeEngineDataPath(string engineDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineDataPath);
        return Path.GetFullPath(engineDataPath);
    }

    private static Dictionary<string, string?> BuildEnvironment(
        string engineDataPath,
        string sandboxProfile,
        bool requireSandboxEnforcement) =>
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GROK_HOME"] = engineDataPath,
            ["GROK_SANDBOX"] = sandboxProfile,
            ["GROK_SANDBOX_REQUIRE_ENFORCEMENT"] = requireSandboxEnforcement ? "1" : null,
            ["GROK_DISABLE_API_KEY_PERSIST"] = "1",
            ["GROK_DISABLE_AUTOUPDATER"] = "1",
            ["DISABLE_TELEMETRY"] = "1",
            ["GROK_TELEMETRY_ENABLED"] = "false",
            ["GROK_TELEMETRY_TRACE_UPLOAD"] = "false",
            ["GROK_FEEDBACK_ENABLED"] = "false",
            ["DISABLE_ERROR_REPORTING"] = "1",
            ["AGENTDESK_SUBAGENT_WORKTREE_MODE"] = "strict",
            ["RUST_LOG"] = null,
            ["GROK_LOG_SAMPLING"] = null,
            ["GROK_DEBUG_LOG"] = null,
            ["GROK_LOG_FILE"] = null,
            ["XAI_API_KEY"] = null,
            ["GROK_CODE_XAI_API_KEY"] = null,
        };

    private static string BuildWslEnvironment(string? inheritedValue)
    {
        string[] requiredVariables =
        [
            "GROK_HOME",
            "GROK_SANDBOX",
            "GROK_SANDBOX_REQUIRE_ENFORCEMENT",
            "GROK_DISABLE_API_KEY_PERSIST",
            "GROK_DISABLE_AUTOUPDATER",
            "DISABLE_TELEMETRY",
            "GROK_TELEMETRY_ENABLED",
            "GROK_TELEMETRY_TRACE_UPLOAD",
            "GROK_FEEDBACK_ENABLED",
            "DISABLE_ERROR_REPORTING",
            "AGENTDESK_SUBAGENT_WORKTREE_MODE",
        ];
        var blockedVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "XAI_API_KEY",
            "GROK_CODE_XAI_API_KEY",
            "RUST_LOG",
            "GROK_LOG_SAMPLING",
            "GROK_DEBUG_LOG",
            "GROK_LOG_FILE",
        };
        var entries = string.IsNullOrWhiteSpace(inheritedValue)
            ? []
            : inheritedValue
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Where(entry => !blockedVariables.Contains(entry.Split('/', 2)[0]))
                .ToList();
        var existingNames = entries
            .Select(static entry => entry.Split('/', 2)[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in requiredVariables)
        {
            if (existingNames.Add(variable))
            {
                entries.Add(variable);
            }
        }

        return string.Join(':', entries);
    }
}
