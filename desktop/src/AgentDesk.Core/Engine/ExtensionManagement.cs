namespace AgentDesk.Core.Engine;

public enum McpServerSource
{
    Managed,
    Local,
}

public enum McpServerTransportKind
{
    Http,
    Stdio,
    ManagedGateway,
}

public enum McpSessionStatus
{
    Ready,
    Initializing,
    Unavailable,
}

public sealed record McpToolInfo(
    string Name,
    string? DisplayName,
    string? Description,
    bool Enabled);

public sealed record McpServerSessionInfo(
    bool Enabled,
    McpSessionStatus? Status,
    IReadOnlyList<McpToolInfo> Tools,
    bool AuthRequired);

public sealed record McpServerCatalogItem(
    string Name,
    string? DisplayName,
    McpServerSource Source,
    string? SourceLabel,
    McpServerTransportKind Transport,
    string? Url,
    string? Scope,
    string? ScopeId,
    string? ScopeName,
    string? Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> EnvironmentVariableNames,
    McpServerSessionInfo? Session);

/// <summary>
/// Maps a child-process environment variable to a value read from the desktop process environment.
/// The wire value is emitted as <c>${SOURCE_VARIABLE}</c>; plaintext secrets are not accepted.
/// </summary>
public sealed record McpEnvironmentReference(string Name, string SourceVariable);

/// <summary>
/// Maps an HTTP header to a value read from an environment variable.
/// </summary>
public sealed record McpHeaderEnvironmentReference(string Name, string SourceVariable);

public abstract record McpServerConfiguration(
    bool Enabled,
    ulong? StartupTimeoutSeconds,
    ulong? ToolTimeoutSeconds,
    IReadOnlyDictionary<string, ulong>? ToolTimeouts,
    bool? ExposeImageBase64);

public sealed record McpStdioServerConfiguration(
    string Command,
    IReadOnlyList<string>? Arguments = null,
    IReadOnlyList<McpEnvironmentReference>? Environment = null,
    string? WorkingDirectory = null,
    bool Enabled = true,
    ulong? StartupTimeoutSeconds = null,
    ulong? ToolTimeoutSeconds = null,
    IReadOnlyDictionary<string, ulong>? ToolTimeouts = null,
    bool? ExposeImageBase64 = null)
    : McpServerConfiguration(
        Enabled,
        StartupTimeoutSeconds,
        ToolTimeoutSeconds,
        ToolTimeouts,
        ExposeImageBase64);

public sealed record McpHttpServerConfiguration(
    string Url,
    string? BearerTokenEnvironmentVariable = null,
    IReadOnlyList<McpHeaderEnvironmentReference>? Headers = null,
    string? OAuthClientId = null,
    string? OAuthClientSecretEnvironmentVariable = null,
    IReadOnlyList<string>? OAuthScopes = null,
    bool Enabled = true,
    ulong? StartupTimeoutSeconds = null,
    ulong? ToolTimeoutSeconds = null,
    IReadOnlyDictionary<string, ulong>? ToolTimeouts = null,
    bool? ExposeImageBase64 = null)
    : McpServerConfiguration(
        Enabled,
        StartupTimeoutSeconds,
        ToolTimeoutSeconds,
        ToolTimeouts,
        ExposeImageBase64);

public sealed record McpServerUpsertRequest(
    SessionId SessionId,
    string ServerName,
    McpServerConfiguration Configuration);

public enum SkillScope
{
    Local,
    Repo,
    User,
    Server,
    Bundled,
    Plugin,
}

public sealed record SkillDescriptor(
    string Name,
    string? DisplayName,
    string Description,
    bool HasUserSpecifiedDescription,
    IReadOnlyList<string> Paths,
    string? WhenToUse,
    string? ShortDescription,
    string? Author,
    string? ArgumentHint,
    string? License,
    string? Compatibility,
    IReadOnlyDictionary<string, string> Metadata,
    string Path,
    SkillScope Scope,
    string? PluginName,
    string? PluginVersion,
    IReadOnlyList<string> AllowedTools,
    string? Model,
    string? Effort,
    bool UserInvocable,
    bool DisableModelInvocation,
    bool Enabled);

public sealed record SkillPathMutationResult(
    string? Path,
    int? AddedCount,
    int? Total,
    IReadOnlyList<SkillDescriptor> Skills,
    string Message);

public sealed record SkillsConfiguration(
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> IgnoredPaths,
    int TotalSkills,
    string Message,
    IReadOnlyList<SkillDescriptor> Skills);

public enum HookEvent
{
    SessionStart,
    SessionEnd,
    Stop,
    StopFailure,
    PreToolUse,
    PostToolUse,
    PostToolUseFailure,
    PermissionDenied,
    UserPromptSubmit,
    Notification,
    SubagentStart,
    SubagentStop,
    PreCompact,
    PostCompact,
}

public enum HookHandlerType
{
    Command,
    Http,
}

public sealed record HookDescriptor(
    string Name,
    HookEvent Event,
    HookHandlerType HandlerType,
    string? Matcher,
    string? Command,
    string? Url,
    TimeSpan Timeout,
    string SourceDirectory,
    bool Disabled);

public sealed record HookCatalog(
    IReadOnlyList<HookDescriptor> Hooks,
    bool ProjectTrusted,
    IReadOnlyList<string> LoadErrors);

public abstract record HookAction
{
    public sealed record Reload : HookAction;

    public sealed record Trust : HookAction;

    public sealed record Untrust : HookAction;

    public sealed record Add(string Path) : HookAction;

    public sealed record Remove(string Path) : HookAction;

    public sealed record Enable(string HookName) : HookAction;

    public sealed record Disable(string HookName) : HookAction;

    public sealed record ToggleSource(IReadOnlyList<string> HookNames, bool DisableSource) : HookAction;
}

public enum PluginScope
{
    Cli,
    Project,
    User,
    Config,
}

public enum PluginHookStatus
{
    Active,
    ActiveInline,
    Blocked,
    None,
}

public enum PluginMcpStatus
{
    Active,
    ActiveInline,
    Blocked,
    None,
}

public enum PluginOriginKind
{
    CliOverride,
    ProjectGrok,
    ProjectClaude,
    UserGrok,
    UserClaude,
    ClaudeMarketplace,
    ClaudeInstalled,
    MarketplaceInstall,
    ConfigPath,
    Unknown,
}

public sealed record PluginOrigin(
    PluginOriginKind Kind,
    string? Marketplace = null,
    string? SourceName = null,
    string? GitUrl = null);

public sealed record PluginDescriptor(
    string Name,
    string Id,
    string Root,
    PluginScope Scope,
    bool Trusted,
    bool Enabled,
    string? Version,
    string? Description,
    int SkillCount,
    IReadOnlyList<string> SkillNames,
    int AgentCount,
    IReadOnlyList<string> AgentNames,
    PluginHookStatus HookStatus,
    int HookCount,
    int McpServerCount,
    PluginMcpStatus McpStatus,
    string? MarketplaceSource,
    PluginOrigin? Origin,
    string? Conflict);

public abstract record PluginAction
{
    public sealed record Reload : PluginAction;

    public sealed record Install(string Source) : PluginAction;

    public sealed record Uninstall(string PluginId, bool Confirmed = false) : PluginAction;

    public sealed record Update(string? PluginId = null) : PluginAction;

    public sealed record Add(string Path) : PluginAction;

    public sealed record Remove(string Path) : PluginAction;

    public sealed record Enable(string PluginId) : PluginAction;

    public sealed record Disable(string PluginId) : PluginAction;
}

public enum ExtensionActionStatus
{
    Success,
    ValidationError,
    ConfirmationRequired,
    NotFound,
    InternalError,
    Unsupported,
}

public sealed record ExtensionActionOutcome(
    ExtensionActionStatus Status,
    string Message,
    bool RequiresReload,
    bool RequiresRestart);

public enum MarketplaceSourceKind
{
    Git,
    Local,
    Failed,
}

public enum MarketplaceInstallStatus
{
    NotInstalled,
    Installed,
    UpdateAvailable,
}

public sealed record MarketplaceComponent(string Name, string? Description);

public sealed record MarketplacePluginComponents(
    IReadOnlyList<MarketplaceComponent> Skills,
    IReadOnlyList<MarketplaceComponent> Commands,
    IReadOnlyList<MarketplaceComponent> Agents,
    IReadOnlyList<MarketplaceComponent> McpServers,
    IReadOnlyList<MarketplaceComponent> Hooks,
    IReadOnlyList<MarketplaceComponent> LspServers);

public sealed record MarketplacePluginTarget(string Source, string RelativePath);

public sealed record MarketplacePluginDescriptor(
    string Name,
    string? Version,
    string? Description,
    string? Category,
    string? Author,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Domains,
    string? Homepage,
    MarketplacePluginTarget Target,
    int SkillCount,
    bool HasHooks,
    bool HasAgents,
    bool HasMcp,
    MarketplaceInstallStatus InstallStatus,
    string? InstalledVersion,
    MarketplacePluginComponents? Components);

public sealed record MarketplaceSourceDescriptor(
    string Name,
    MarketplaceSourceKind Kind,
    string Source,
    IReadOnlyList<MarketplacePluginDescriptor> Plugins,
    string? Error);

public sealed record MarketplaceCatalog(IReadOnlyList<MarketplaceSourceDescriptor> Sources);

public abstract record MarketplaceAction
{
    public sealed record Refresh(string? Source = null) : MarketplaceAction;

    public sealed record Install(MarketplacePluginTarget Target) : MarketplaceAction;

    public sealed record Update(MarketplacePluginTarget Target) : MarketplaceAction;

    public sealed record Uninstall(MarketplacePluginTarget Target) : MarketplaceAction;
}
