using System.Text.Json;
using AgentDesk.Core.Engine;

namespace AgentDesk.App.Bridge;

/// <summary>
/// The stable, versioned surface exposed to the WebView for extension management.
/// Values in <see cref="ExtensionsActionWebCommand.Payload"/> are validated by the
/// protocol parser before they reach the host controller.
/// </summary>
public enum ExtensionScope
{
    Mcp,
    Skills,
    Hooks,
    Plugins,
    Marketplace,
}

public sealed record ExtensionApprovalRequest(
    ExtensionScope Scope,
    string Action,
    string Target);

public delegate Task<bool> ExtensionApprovalHandler(
    ExtensionApprovalRequest request,
    CancellationToken cancellationToken);

public sealed record ExtensionsListWebCommand(
    string RequestId,
    int WorkspaceGeneration,
    string? SessionId,
    bool UseCache = true) : WebCommand;

public sealed record ExtensionsActionWebCommand(
    string RequestId,
    int WorkspaceGeneration,
    string SessionId,
    ExtensionScope Scope,
    string Action,
    bool Confirmed,
    JsonElement Payload) : WebCommand;

public sealed record ExtensionsCatalogWebEvent(
    string RequestId,
    string SessionId,
    IReadOnlyList<McpServerCatalogItem> McpServers,
    IReadOnlyList<SkillDescriptor> Skills,
    SkillsConfiguration? SkillsConfiguration,
    HookCatalog Hooks,
    IReadOnlyList<PluginDescriptor> Plugins,
    MarketplaceCatalog Marketplace) : WebEvent;

public sealed record ExtensionsActionCompletedWebEvent(
    string RequestId,
    string SessionId,
    ExtensionScope Scope,
    string Action,
    ExtensionActionOutcome Outcome) : WebEvent;

public sealed record ExtensionsErrorWebEvent(
    string RequestId,
    string? SessionId,
    ExtensionScope? Scope,
    string? Action,
    string Message) : WebEvent;
