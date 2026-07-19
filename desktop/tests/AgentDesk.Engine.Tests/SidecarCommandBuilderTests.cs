using AgentDesk.Core.Execution;
using AgentDesk.Core.Providers;
using AgentDesk.Engine.Sidecar;

namespace AgentDesk.Engine.Tests;

public sealed class SidecarCommandBuilderTests
{
    [Fact]
    public void Constructor_NullWslPathConverterThrows()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new SidecarCommandBuilder(null!));

        Assert.Equal("wslPathConverter", exception.ParamName);
    }

    [Fact]
    public async Task BuildAsync_NativeProtectedUsesCanonicalAgentArgumentsAndPrivateEnvironment()
    {
        using var workspace = new TemporaryDirectory();
        var enginePath = workspace.CreateFile("agentdesk-engine.exe");
        var converter = new FakeWslPathConverter();
        var builder = new SidecarCommandBuilder(
            converter,
            new FixedWslDistributionResolver("Ubuntu"),
            new FixedWslEngineInstallationVerifier(isCurrent: true));

        var command = await builder.BuildAsync(new SidecarLaunchOptions(
            workspace.Path,
            ExecutionProfile.NativeProtected)
        {
            EnginePath = enginePath,
            ApiKey = "xai-secret",
        });

        Assert.Equal(enginePath, command.FileName);
        Assert.Equal(["--no-auto-update", "agent", "--no-leader", "stdio"], command.Arguments);
        Assert.Equal(workspace.Path, command.WorkingDirectory);
        Assert.Equal(workspace.Path, command.EngineWorkspacePath);
        Assert.Equal("1", command.Environment["GROK_DISABLE_API_KEY_PERSIST"]);
        Assert.Equal("1", command.Environment["GROK_DISABLE_AUTOUPDATER"]);
        Assert.Equal("1", command.Environment["DISABLE_TELEMETRY"]);
        Assert.Equal("false", command.Environment["GROK_TELEMETRY_ENABLED"]);
        Assert.Equal("false", command.Environment["GROK_TELEMETRY_TRACE_UPLOAD"]);
        Assert.Equal("false", command.Environment["GROK_FEEDBACK_ENABLED"]);
        Assert.Equal("1", command.Environment["DISABLE_ERROR_REPORTING"]);
        Assert.Equal("strict", command.Environment["AGENTDESK_SUBAGENT_WORKTREE_MODE"]);
        Assert.Null(command.Environment["XAI_API_KEY"]);
        Assert.Null(command.Environment["GROK_CODE_XAI_API_KEY"]);
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentDesk",
                "Engine"),
            command.Environment["GROK_HOME"]);
        Assert.Equal("off", command.Environment["GROK_SANDBOX"]);
        Assert.Null(command.Environment["GROK_SANDBOX_REQUIRE_ENFORCEMENT"]);
        Assert.Empty(converter.ConvertedPaths);
    }

    [Fact]
    public async Task BuildAsync_WithoutApiKeyRemovesInheritedCredentialVariables()
    {
        using var workspace = new TemporaryDirectory();
        var enginePath = workspace.CreateFile("agentdesk-engine.exe");
        var builder = new SidecarCommandBuilder(new FakeWslPathConverter());

        var command = await builder.BuildAsync(new SidecarLaunchOptions(
            workspace.Path,
            ExecutionProfile.NativeProtected)
        {
            EnginePath = enginePath,
        });

        Assert.Null(command.Environment["XAI_API_KEY"]);
        Assert.Null(command.Environment["GROK_CODE_XAI_API_KEY"]);
    }

    [Fact]
    public async Task BuildAsync_NativeProtectedPassesOpenAiCompatibleProviderAsSeparateArguments()
    {
        using var workspace = new TemporaryDirectory();
        var enginePath = workspace.CreateFile("agentdesk-engine.exe");
        var builder = new SidecarCommandBuilder(new FakeWslPathConverter());
        var provider = new ProviderProfile(
            "https://example.com/v1/",
            " grok-4.5 ",
            ProviderBackend.ChatCompletions);

        var command = await builder.BuildAsync(new SidecarLaunchOptions(
            workspace.Path,
            ExecutionProfile.NativeProtected)
        {
            EnginePath = enginePath,
            ApiKey = "provider-secret",
            ProviderProfile = provider,
        });

        Assert.Equal(
            [
                "--no-auto-update",
                "agent",
                "--no-leader",
                "--model",
                "grok-4.5",
                "--agentdesk-openai-base-url",
                "https://example.com/v1",
                "--agentdesk-openai-backend",
                "chat_completions",
                "stdio",
            ],
            command.Arguments);
        Assert.DoesNotContain("provider-secret", command.Arguments);
        Assert.DoesNotContain(
            command.Environment,
            entry => string.Equals(entry.Value, "provider-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_WslStrictPassesOpenAiCompatibleProviderAfterWslExecBoundary()
    {
        using var workspace = new TemporaryDirectory();
        var converter = new FakeWslPathConverter(path =>
            string.Equals(path, workspace.Path, StringComparison.Ordinal)
                ? "/mnt/c/work/project"
                : "/mnt/c/users/test/appdata/local/agentdesk/engine");
        var builder = new SidecarCommandBuilder(
            converter,
            new FixedWslDistributionResolver("Ubuntu"),
            new FixedWslEngineInstallationVerifier(isCurrent: true));

        var command = await builder.BuildAsync(new SidecarLaunchOptions(
            workspace.Path,
            ExecutionProfile.WslStrict)
        {
            EnginePath = "/opt/agentdesk/agentdesk-engine",
            ProviderProfile = new ProviderProfile("https://example.com/v1", "grok-4.5"),
        });

        Assert.Equal(
            [
                "--distribution",
                "Ubuntu",
                "--cd",
                "/mnt/c/work/project",
                "--exec",
                "/usr/local/bin/agentdesk-engine",
                "--no-auto-update",
                "agent",
                "--no-leader",
                "--model",
                "grok-4.5",
                "--agentdesk-openai-base-url",
                "https://example.com/v1",
                "--agentdesk-openai-backend",
                "chat_completions",
                "stdio",
            ],
            command.Arguments);
    }

    [Fact]
    public async Task BuildAsync_WslStrictUsesTheVerifiedInstalledEngine()
    {
        using var workspace = new TemporaryDirectory();
        var bundledEngine = workspace.CreateFile("agentdesk-engine-linux");
        var converter = new FakeWslPathConverter(path => path switch
        {
            var value when value == workspace.Path => "/mnt/c/work/project",
            var value when value == bundledEngine => "/mnt/c/app/wsl/agentdesk-engine",
            _ => "/mnt/c/users/test/appdata/local/agentdesk/engine",
        });
        var builder = new SidecarCommandBuilder(
            converter,
            new FixedWslDistributionResolver("Ubuntu"),
            new FixedWslEngineInstallationVerifier(isCurrent: true));

        var command = await builder.BuildAsync(new SidecarLaunchOptions(
            workspace.Path,
            ExecutionProfile.WslStrict)
        {
            EnginePath = bundledEngine,
        });

        Assert.Equal("/usr/local/bin/agentdesk-engine", command.Arguments[5]);
        Assert.DoesNotContain(bundledEngine, converter.ConvertedPaths);
    }

    [Fact]
    public async Task BuildAsync_ResponsesProviderPassesBackendAsASeparateArgument()
    {
        using var workspace = new TemporaryDirectory();
        var enginePath = workspace.CreateFile("agentdesk-engine.exe");
        var builder = new SidecarCommandBuilder(new FakeWslPathConverter());

        var command = await builder.BuildAsync(new SidecarLaunchOptions(
                workspace.Path,
                ExecutionProfile.NativeProtected)
        {
            EnginePath = enginePath,
            ProviderProfile = new ProviderProfile(
                    "https://example.com/v1",
                    "grok-4.5",
                    ProviderBackend.Responses),
        });

        Assert.Equal(
            [
                "--no-auto-update",
                "agent",
                "--no-leader",
                "--model",
                "grok-4.5",
                "--agentdesk-openai-base-url",
                "https://example.com/v1",
                "--agentdesk-openai-backend",
                "responses",
                "stdio",
            ],
            command.Arguments);
    }

    [Fact]
    public async Task BuildAsync_WslStrictConvertsWorkspaceAndUsesWslExecWithoutShellParsing()
    {
        using var workspace = new TemporaryDirectory();
        var engineDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentDesk",
            "Engine");
        var converter = new FakeWslPathConverter(path =>
            string.Equals(path, workspace.Path, StringComparison.Ordinal)
                ? "/mnt/c/work/project"
                : "/mnt/c/users/test/appdata/local/agentdesk/engine");
        var builder = new SidecarCommandBuilder(
            converter,
            new FixedWslDistributionResolver("Ubuntu"),
            new FixedWslEngineInstallationVerifier(isCurrent: true));

        var command = await builder.BuildAsync(new SidecarLaunchOptions(
            workspace.Path,
            ExecutionProfile.WslStrict)
        {
            EnginePath = "/opt/agentdesk/agentdesk-engine",
            ApiKey = "xai-secret",
        });

        Assert.Equal("wsl.exe", command.FileName);
        Assert.Equal(
            [
                "--distribution",
                "Ubuntu",
                "--cd",
                "/mnt/c/work/project",
                "--exec",
                "/usr/local/bin/agentdesk-engine",
                "--no-auto-update",
                "agent",
                "--no-leader",
                "stdio",
            ],
            command.Arguments);
        Assert.Equal(workspace.Path, command.WorkingDirectory);
        Assert.Equal("/mnt/c/work/project", command.EngineWorkspacePath);
        Assert.Equal([workspace.Path, engineDataPath], converter.ConvertedPaths);
        Assert.All(
            converter.DistributionNames,
            distribution => Assert.Equal("Ubuntu", distribution));
        Assert.Equal(
            "/mnt/c/users/test/appdata/local/agentdesk/engine",
            command.Environment["GROK_HOME"]);
        Assert.Equal("strict", command.Environment["GROK_SANDBOX"]);
        Assert.Equal("1", command.Environment["GROK_SANDBOX_REQUIRE_ENFORCEMENT"]);
        var wslEnvironment = command.Environment["WSLENV"];
        Assert.NotNull(wslEnvironment);
        Assert.DoesNotContain("XAI_API_KEY", wslEnvironment, StringComparison.Ordinal);
        Assert.Null(command.Environment["XAI_API_KEY"]);
        Assert.Null(command.Environment["GROK_CODE_XAI_API_KEY"]);
        Assert.Contains("GROK_HOME", wslEnvironment, StringComparison.Ordinal);
        Assert.Contains("GROK_SANDBOX", wslEnvironment, StringComparison.Ordinal);
        Assert.Contains(
            "GROK_SANDBOX_REQUIRE_ENFORCEMENT",
            wslEnvironment,
            StringComparison.Ordinal);
        Assert.Contains("GROK_DISABLE_API_KEY_PERSIST", wslEnvironment, StringComparison.Ordinal);
        Assert.Contains("GROK_DISABLE_AUTOUPDATER", wslEnvironment, StringComparison.Ordinal);
        Assert.Contains("DISABLE_TELEMETRY", wslEnvironment, StringComparison.Ordinal);
        Assert.Contains(
            "AGENTDESK_SUBAGENT_WORKTREE_MODE",
            wslEnvironment,
            StringComparison.Ordinal);
        Assert.Equal("strict", command.Environment["AGENTDESK_SUBAGENT_WORKTREE_MODE"]);
        Assert.DoesNotContain("xai-secret", command.Arguments);
        Assert.DoesNotContain(
            command.Environment,
            entry => string.Equals(entry.Value, "xai-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_ConfiguredNativeEnginePathMissingReturnsStructuredError()
    {
        using var workspace = new TemporaryDirectory();
        var missingPath = System.IO.Path.Combine(workspace.Path, "missing.exe");
        var builder = new SidecarCommandBuilder(new FakeWslPathConverter());

        var exception = await Assert.ThrowsAsync<SidecarStartException>(() =>
            builder.BuildAsync(new SidecarLaunchOptions(
                workspace.Path,
                ExecutionProfile.NativeProtected)
            {
                EnginePath = missingPath,
            }));

        Assert.Equal(SidecarStartFailure.EngineNotFound, exception.Failure);
        Assert.Contains(missingPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_WslConversionFailureReturnsStructuredError()
    {
        using var workspace = new TemporaryDirectory();
        var converter = new FakeWslPathConverter(exception: new InvalidOperationException("bad path"));
        var builder = new SidecarCommandBuilder(
            converter,
            new FixedWslDistributionResolver("Ubuntu"),
            new FixedWslEngineInstallationVerifier(isCurrent: true));

        var exception = await Assert.ThrowsAsync<SidecarStartException>(() =>
            builder.BuildAsync(new SidecarLaunchOptions(
                workspace.Path,
                ExecutionProfile.WslStrict)
            {
                EnginePath = "/opt/agentdesk/agentdesk-engine",
            }));

        Assert.Equal(SidecarStartFailure.WorkspacePathConversionFailed, exception.Failure);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task BuildAsync_WslStrictWithoutAnEligibleDistributionFailsClosed()
    {
        using var workspace = new TemporaryDirectory();
        var converter = new FakeWslPathConverter();
        var builder = new SidecarCommandBuilder(
            converter,
            new FixedWslDistributionResolver(distributionName: null));

        var exception = await Assert.ThrowsAsync<SidecarStartException>(() =>
            builder.BuildAsync(new SidecarLaunchOptions(
                workspace.Path,
                ExecutionProfile.WslStrict)
            {
                EnginePath = "/opt/agentdesk/agentdesk-engine",
            }));

        Assert.Equal(SidecarStartFailure.WslUnavailable, exception.Failure);
        Assert.Empty(converter.ConvertedPaths);
    }

    [Fact]
    public async Task BuildAsync_WslStrictRejectsAStaleInstalledEngineBeforePathConversion()
    {
        using var workspace = new TemporaryDirectory();
        var bundledEngine = workspace.CreateFile("agentdesk-engine-linux");
        var converter = new FakeWslPathConverter();
        var builder = new SidecarCommandBuilder(
            converter,
            new FixedWslDistributionResolver("Ubuntu"),
            new FixedWslEngineInstallationVerifier(isCurrent: false));

        var exception = await Assert.ThrowsAsync<SidecarStartException>(() =>
            builder.BuildAsync(new SidecarLaunchOptions(
                workspace.Path,
                ExecutionProfile.WslStrict)
            {
                EnginePath = bundledEngine,
            }));

        Assert.Equal(SidecarStartFailure.EngineNotFound, exception.Failure);
        Assert.Empty(converter.ConvertedPaths);
    }

    private sealed class FakeWslPathConverter(
        string convertedPath = "/mnt/c/workspace",
        Exception? exception = null,
        Func<string, string>? convert = null) : IWslPathConverter
    {
        public FakeWslPathConverter(Func<string, string> convert)
            : this("/mnt/c/workspace", null, convert)
        {
        }

        public List<string> ConvertedPaths { get; } = [];

        public List<string> DistributionNames { get; } = [];

        public Task<string> ConvertAsync(
            string windowsPath,
            string distributionName,
            CancellationToken cancellationToken)
        {
            ConvertedPaths.Add(windowsPath);
            DistributionNames.Add(distributionName);
            return exception is null
                ? Task.FromResult(convert?.Invoke(windowsPath) ?? convertedPath)
                : Task.FromException<string>(exception);
        }
    }

    private sealed class FixedWslDistributionResolver(string? distributionName)
        : IWslDistributionResolver
    {
        public string? Resolve(string wslExecutablePath) => distributionName;
    }

    private sealed class FixedWslEngineInstallationVerifier(bool isCurrent)
        : IWslEngineInstallationVerifier
    {
        public bool IsCurrent(
            string wslExecutablePath,
            string distributionName,
            string bundledEnginePath) =>
            isCurrent;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"agentdesk-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateFile(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(path, []);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
