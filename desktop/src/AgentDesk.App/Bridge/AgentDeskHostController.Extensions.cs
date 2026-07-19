using System.Globalization;
using System.Text.Json;
using AgentDesk.App.Cloud;
using AgentDesk.Core.Engine;

namespace AgentDesk.App.Bridge;

public sealed partial class AgentDeskHostController
{
    private const string ExtensionPolicyDeniedMarker = "agentdesk-policy-denied";
    private const int MaximumExtensionApprovalTargetLength = 128;

    private string ExtensionOperationErrorMessage => Message(
        "扩展操作失败。",
        "The extension operation failed.");

    private string ExtensionConfirmationMessage => Message(
        "此操作需要再次确认。",
        "This operation requires confirmation.");

    private string ExtensionOperationSucceededMessage => Message(
        "扩展操作已完成。",
        "The extension operation completed.");

    private async Task HandleExtensionsListAsync(
        ExtensionsListWebCommand command,
        CancellationToken cancellationToken)
    {
        if (!await TryReserveExtensionRequestAsync(command.RequestId, cancellationToken).ConfigureAwait(false))
        {
            Publish(new ExtensionsErrorWebEvent(
                command.RequestId,
                command.SessionId,
                Scope: null,
                Action: null,
                ExtensionOperationErrorMessage));
            return;
        }

        WorkspaceOperationContext? context = null;
        ExtensionsCatalogWebEvent? catalog = null;
        var callerCancelled = false;
        try
        {
            context = await BeginWorkspaceOperationAsync(
                    command.WorkspaceGeneration,
                    cancellationToken,
                    command.SessionId,
                    requiresIdle: false)
                .ConfigureAwait(false);
            var client = context.Client;
            var mcp = await client
                .ListMcpServersAsync(context.SessionId, command.UseCache, context.Cancellation.Token)
                .ConfigureAwait(false);
            var skills = await client
                .ListSkillsAsync(context.EngineWorkspacePath, context.Cancellation.Token)
                .ConfigureAwait(false);
            var skillConfiguration = await client
                .GetSkillsConfigurationAsync(context.EngineWorkspacePath, context.Cancellation.Token)
                .ConfigureAwait(false);
            var hooks = await client
                .ListHooksAsync(context.SessionId, context.Cancellation.Token)
                .ConfigureAwait(false);
            var plugins = await client
                .ListPluginsAsync(context.SessionId, context.Cancellation.Token)
                .ConfigureAwait(false);
            var marketplace = await client
                .ListMarketplaceAsync(context.SessionId, context.Cancellation.Token)
                .ConfigureAwait(false);
            catalog = new ExtensionsCatalogWebEvent(
                command.RequestId,
                context.SessionId.Value,
                mcp,
                skills,
                skillConfiguration,
                hooks,
                plugins,
                marketplace);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            callerCancelled = true;
        }
        catch (Exception)
        {
            // Never project exception text: sidecar errors may contain paths or secrets.
        }

        var current = context is not null
            ? await CompleteWorkspaceOperationAsync(context).ConfigureAwait(false)
            : await IsCurrentWorkspaceGenerationAsync(command.WorkspaceGeneration).ConfigureAwait(false);
        if (callerCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (!current)
        {
            catalog = null;
        }

        if (catalog is null)
        {
            Publish(new ExtensionsErrorWebEvent(
                command.RequestId,
                command.SessionId,
                Scope: null,
                Action: null,
                ExtensionOperationErrorMessage));
        }
        else
        {
            Publish(catalog);
        }
    }

    private async Task HandleExtensionsActionAsync(
        ExtensionsActionWebCommand command,
        CancellationToken cancellationToken)
    {
        if (!await TryReserveExtensionRequestAsync(command.RequestId, cancellationToken).ConfigureAwait(false))
        {
            Publish(new ExtensionsErrorWebEvent(
                command.RequestId,
                command.SessionId,
                command.Scope,
                command.Action,
                ExtensionOperationErrorMessage));
            return;
        }

        WorkspaceOperationContext? context = null;
        ExtensionActionOutcome? outcome = null;
        var callerCancelled = false;
        try
        {
            context = await BeginWorkspaceOperationAsync(
                    command.WorkspaceGeneration,
                    cancellationToken,
                    command.SessionId,
                    requiresIdle: true)
                .ConfigureAwait(false);
            var policyVersion = _cloudPolicyGate.CaptureVersion();
            if (!ExtensionCodeLoadingPolicyAllows(command))
            {
                outcome = new ExtensionActionOutcome(
                    ExtensionActionStatus.ValidationError,
                    ExtensionPolicyDeniedMarker,
                    RequiresReload: false,
                    RequiresRestart: false);
            }
            else if (ExtensionActionRequiresNativeApproval(command) &&
                     !await RequestExtensionApprovalAsync(command, context.Cancellation.Token)
                         .ConfigureAwait(false))
            {
                outcome = new ExtensionActionOutcome(
                    ExtensionActionStatus.ConfirmationRequired,
                    ExtensionConfirmationMessage,
                    RequiresReload: false,
                    RequiresRestart: false);
            }
            else if (ExtensionActionLoadsPluginCode(command))
            {
                outcome = await _cloudPolicyGate.ExecuteIfCurrentAsync(
                        policyVersion,
                        token => ExecuteExtensionActionAsync(context, command, token),
                        context.Cancellation.Token)
                    .ConfigureAwait(false);
                outcome ??= new ExtensionActionOutcome(
                        ExtensionActionStatus.ValidationError,
                        ExtensionPolicyDeniedMarker,
                        RequiresReload: false,
                        RequiresRestart: false);
            }
            else
            {
                outcome = await ExecuteExtensionActionAsync(
                        context,
                        command,
                        context.Cancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            callerCancelled = true;
        }
        catch (Exception)
        {
            // Details are intentionally withheld from the WebView.
        }

        var current = context is not null
            ? await CompleteWorkspaceOperationAsync(context).ConfigureAwait(false)
            : await IsCurrentWorkspaceGenerationAsync(command.WorkspaceGeneration).ConfigureAwait(false);
        if (callerCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (!current)
        {
            outcome = null;
        }

        if (outcome is null)
        {
            Publish(new ExtensionsErrorWebEvent(
                command.RequestId,
                command.SessionId,
                command.Scope,
                command.Action,
                ExtensionOperationErrorMessage));
            return;
        }

        Publish(new ExtensionsActionCompletedWebEvent(
            command.RequestId,
            command.SessionId,
            command.Scope,
            command.Action,
            SanitizeOutcome(outcome)));
    }

    private async Task<bool> TryReserveExtensionRequestAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return _extensionRequestIds.Add(requestId);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private static bool ExtensionActionRequiresNativeApproval(ExtensionsActionWebCommand command) =>
        command.Scope switch
        {
            ExtensionScope.Mcp => true,
            ExtensionScope.Skills => true,
            ExtensionScope.Hooks => true,
            ExtensionScope.Plugins => true,
            ExtensionScope.Marketplace => command.Action is "install" or "update" or "uninstall",
            _ => false,
        };

    private async Task<bool> RequestExtensionApprovalAsync(
        ExtensionsActionWebCommand command,
        CancellationToken cancellationToken)
    {
        var handler = _extensionApprovalHandler;
        if (handler is null)
        {
            return false;
        }

        return await handler(
                CreateExtensionApprovalRequest(command),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static ExtensionApprovalRequest CreateExtensionApprovalRequest(
        ExtensionsActionWebCommand command)
    {
        var payload = command.Payload;
        var target = command.Scope switch
        {
            ExtensionScope.Mcp => CreateMcpApprovalTarget(command.Action, payload),
            ExtensionScope.Skills => command.Action switch
            {
                "add_path" or "remove_path" => SafeApprovalPath(
                    RequiredPayloadString(payload, "path"),
                    "skill"),
                "reset" => "skills",
                _ => throw new InvalidDataException("The skills action is not supported."),
            },
            ExtensionScope.Hooks => command.Action switch
            {
                "add" or "remove" => SafeApprovalPath(
                    RequiredPayloadString(payload, "path"),
                    "hook"),
                "enable" or "disable" => SafeApprovalIdentifier(
                    RequiredPayloadString(payload, "hookName"),
                    "hook"),
                "reload" or "trust" or "untrust" or "toggle_source" => "hooks",
                _ => throw new InvalidDataException("The hooks action is not supported."),
            },
            ExtensionScope.Plugins => command.Action switch
            {
                "add" or "remove" => SafeApprovalPath(
                    RequiredPayloadString(payload, "path"),
                    "plugin"),
                "enable" or "disable" => SafeApprovalLeaf(
                    RequiredPayloadString(payload, "pluginId"),
                    "plugin"),
                "install" => SafeApprovalSource(
                    RequiredPayloadString(payload, "source"),
                    "plugin source"),
                "update" => OptionalPayloadString(payload, "pluginId") is { } pluginId
                    ? SafeApprovalLeaf(pluginId, "plugin")
                    : "plugins",
                "reload" => "plugins",
                _ => throw new InvalidDataException("The plugins action is not supported."),
            },
            ExtensionScope.Marketplace => command.Action switch
            {
                "install" or "update" or "uninstall" => SafeApprovalIdentifier(
                    $"{SafeApprovalSource(RequiredPayloadString(payload, "source"), "marketplace source")} : " +
                    SafeApprovalPath(
                        RequiredPayloadString(payload, "relativePath"),
                        "marketplace plugin"),
                    "marketplace plugin"),
                _ => throw new InvalidDataException("The marketplace action is not supported."),
            },
            _ => throw new InvalidDataException("The extension scope is not supported."),
        };

        return new ExtensionApprovalRequest(command.Scope, command.Action, target);
    }

    private static string CreateMcpApprovalTarget(string action, JsonElement payload)
    {
        var serverName = SafeApprovalIdentifier(
            RequiredPayloadString(payload, "serverName"),
            "MCP server");
        var endpointIdentity = action switch
        {
            "upsert_stdio" => SafeApprovalExecutable(
                RequiredPayloadString(payload, "command")),
            "upsert_http" => SafeApprovalHttpOrigin(
                RequiredPayloadString(payload, "url")),
            "toggle" or "delete" => null,
            _ => throw new InvalidDataException("The MCP action is not supported."),
        };
        return endpointIdentity is null
            ? serverName
            : ComposeApprovalTarget(serverName, endpointIdentity);
    }

    private static string ComposeApprovalTarget(string name, string endpointIdentity)
    {
        const string separator = " : ";
        const int maximumNameLength = 48;
        var displayedName = AbbreviateApprovalPart(name, maximumNameLength);
        var maximumEndpointLength = MaximumExtensionApprovalTargetLength -
            displayedName.Length - separator.Length;
        var displayedEndpoint = AbbreviateApprovalPart(endpointIdentity, maximumEndpointLength);
        return $"{displayedName}{separator}{displayedEndpoint}";
    }

    private static string AbbreviateApprovalPart(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }
        return maximumLength <= 3
            ? value[..maximumLength]
            : $"{value[..(maximumLength - 3)]}...";
    }

    private static string SafeApprovalExecutable(string command)
    {
        var trimmed = command.Trim();
        string executable;
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            executable = closingQuote > 1 ? trimmed[1..closingQuote] : trimmed.Trim('"');
        }
        else if (trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            executable = trimmed;
        }
        else
        {
            executable = trimmed.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        }

        return SafeApprovalLeaf(executable, "executable");
    }

    private static string SafeApprovalHttpOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidDataException("The MCP URL is invalid.");
        }

        var host = uri.IdnHost.ToLowerInvariant();
        if (!uri.IsDefaultPort)
        {
            host = $"{host}:{uri.Port}";
        }
        return SafeApprovalIdentifier($"{uri.Scheme.ToLowerInvariant()}://{host}", "MCP endpoint");
    }

    private static string SafeApprovalSource(string value, string fallback)
    {
        var trimmed = value.Trim();
        if (TryNormalizeScpGitSource(trimmed, out var scpIdentity) ||
            TryNormalizeGitHubShorthand(trimmed, out scpIdentity))
        {
            return SafeApprovalIdentifier(scpIdentity, fallback);
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            var host = uri.IdnHost.ToLowerInvariant();
            if (!uri.IsDefaultPort)
            {
                host = $"{host}:{uri.Port}";
            }
            var path = uri
                .GetComponents(UriComponents.Path, UriFormat.UriEscaped)
                .Trim('/', '\\');
            var source = string.IsNullOrWhiteSpace(path)
                ? host
                : $"{host}/{path}";
            if (source.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                source = source[..^4];
            }
            return SafeApprovalIdentifier(source, fallback);
        }

        return SafeApprovalPath(trimmed, fallback);
    }

    private static bool TryNormalizeScpGitSource(string value, out string identity)
    {
        identity = string.Empty;
        if (!value.StartsWith("git@", StringComparison.Ordinal))
        {
            return false;
        }

        var colon = value.IndexOf(':', 4);
        if (colon <= 4 || colon == value.Length - 1)
        {
            throw new InvalidDataException("The Git source is invalid.");
        }

        string host;
        try
        {
            host = new IdnMapping().GetAscii(value[4..colon]).ToLowerInvariant();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The Git source is invalid.", exception);
        }
        var path = value[(colon + 1)..].Replace('\\', '/');
        var fragment = string.Empty;
        var fragmentIndex = path.LastIndexOf('#');
        if (fragmentIndex >= 0)
        {
            fragment = path[fragmentIndex..];
            path = path[..fragmentIndex];
        }
        var gitReference = string.Empty;
        var referenceIndex = path.LastIndexOf('@');
        if (referenceIndex >= 0)
        {
            gitReference = path[referenceIndex..];
            path = path[..referenceIndex];
        }
        path = TrimGitSuffix(path.Trim('/'));
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException("The Git source is invalid.");
        }

        identity = $"{host}/{path}{gitReference}{fragment}";
        return true;
    }

    private static bool TryNormalizeGitHubShorthand(string value, out string identity)
    {
        identity = string.Empty;
        if (value.StartsWith('/') || value.StartsWith('.') || value.StartsWith('~') ||
            value.Contains('\\') || value.Contains(':'))
        {
            return false;
        }

        var main = value;
        var fragment = string.Empty;
        var fragmentIndex = main.LastIndexOf('#');
        if (fragmentIndex >= 0)
        {
            fragment = main[fragmentIndex..];
            main = main[..fragmentIndex];
        }
        var gitReference = string.Empty;
        var referenceIndex = main.LastIndexOf('@');
        if (referenceIndex >= 0)
        {
            gitReference = main[referenceIndex..];
            main = main[..referenceIndex];
        }
        var parts = main.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        identity = $"github.com/{parts[0]}/{TrimGitSuffix(parts[1])}{gitReference}{fragment}";
        return true;
    }

    private static string TrimGitSuffix(string value) =>
        value.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;

    private static string SafeApprovalPath(string value, string fallback) =>
        SafeApprovalIdentifier(value.Replace('\\', '/'), fallback);

    private static string SafeApprovalLeaf(string value, string fallback)
    {
        var normalized = value.Trim().TrimEnd('/', '\\');
        var separator = Math.Max(normalized.LastIndexOf('/'), normalized.LastIndexOf('\\'));
        var leaf = separator >= 0 ? normalized[(separator + 1)..] : normalized;
        return SafeApprovalIdentifier(leaf, fallback);
    }

    private static string SafeApprovalIdentifier(string value, string fallback)
    {
        if (value.Any(character =>
                char.IsControl(character) ||
                char.GetUnicodeCategory(character) is
                    UnicodeCategory.Format or
                    UnicodeCategory.LineSeparator or
                    UnicodeCategory.ParagraphSeparator))
        {
            throw new InvalidDataException("The extension approval target is invalid.");
        }

        var sanitized = value.Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = fallback;
        }

        return sanitized.Length <= MaximumExtensionApprovalTargetLength
            ? sanitized
            : sanitized[..MaximumExtensionApprovalTargetLength];
    }

    private async Task<ExtensionActionOutcome> ExecuteExtensionActionAsync(
        WorkspaceOperationContext context,
        ExtensionsActionWebCommand command,
        CancellationToken cancellationToken)
    {
        var payload = command.Payload;
        switch (command.Scope)
        {
            case ExtensionScope.Mcp:
                return SanitizeOutcome(await ExecuteMcpActionAsync(
                        context,
                        command.Action,
                        payload,
                        cancellationToken)
                    .ConfigureAwait(false));
            case ExtensionScope.Skills:
                return SanitizeOutcome(await ExecuteSkillsActionAsync(
                        context,
                        command.Action,
                        payload,
                        cancellationToken)
                    .ConfigureAwait(false));
            case ExtensionScope.Hooks:
                return SanitizeOutcome(await context.Client
                    .ExecuteHookActionAsync(
                        context.SessionId,
                        ParseHookAction(command.Action, payload),
                        cancellationToken)
                    .ConfigureAwait(false));
            case ExtensionScope.Plugins:
                return SanitizeOutcome(await context.Client
                    .ExecutePluginActionAsync(
                        context.SessionId,
                        ParsePluginAction(command.Action, payload),
                        cancellationToken)
                    .ConfigureAwait(false));
            case ExtensionScope.Marketplace:
                return SanitizeOutcome(await context.Client
                    .ExecuteMarketplaceActionAsync(
                        context.SessionId,
                        ParseMarketplaceAction(command.Action, payload),
                        cancellationToken)
                    .ConfigureAwait(false));
            default:
                throw new InvalidDataException("The extension scope is not supported.");
        }
    }

    private bool ExtensionCodeLoadingPolicyAllows(ExtensionsActionWebCommand command)
    {
        if (!ExtensionActionLoadsPluginCode(command) ||
            _cloudPolicyGate.Mode is AgentDeskCloudPolicyMode.LocalOnly)
        {
            return true;
        }

        // The WebView cannot attest which publisher signed the artifact. Managed
        // profiles therefore fail closed until installation is bound to a
        // host-verified registry record, digest, and signature.
        return false;
    }

    private static bool ExtensionActionLoadsPluginCode(ExtensionsActionWebCommand command) =>
        command.Scope switch
        {
            ExtensionScope.Plugins => command.Action is
                "reload" or "enable" or "disable" or "add" or "remove" or
                "install" or "update",
            ExtensionScope.Marketplace => command.Action is
                "install" or "update" or "uninstall",
            _ => false,
        };

    private static async Task<ExtensionActionOutcome> ExecuteMcpActionAsync(
        WorkspaceOperationContext context,
        string action,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var client = context.Client;
        var sessionId = context.SessionId;
        bool ok;
        switch (action)
        {
            case "toggle":
                ok = await client.ToggleMcpServerAsync(
                        sessionId,
                        RequiredPayloadString(payload, "serverName"),
                        RequiredPayloadBoolean(payload, "enabled"),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "upsert_stdio":
                ok = await client.UpsertMcpServerAsync(
                        new McpServerUpsertRequest(
                            sessionId,
                            RequiredPayloadString(payload, "serverName"),
                            new McpStdioServerConfiguration(
                                RequiredPayloadString(payload, "command"),
                                OptionalStringArray(payload, "args"),
                                OptionalEnvironmentReferences(payload, "environment"),
                                OptionalString(payload, "workingDirectory"),
                                OptionalBoolean(payload, "enabled") ?? true,
                                OptionalUInt64(payload, "startupTimeoutSeconds"),
                                OptionalUInt64(payload, "toolTimeoutSeconds"),
                                OptionalTimeoutMap(payload),
                                OptionalBoolean(payload, "exposeImageBase64"))),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "upsert_http":
                ok = await client.UpsertMcpServerAsync(
                        new McpServerUpsertRequest(
                            sessionId,
                            RequiredPayloadString(payload, "serverName"),
                            new McpHttpServerConfiguration(
                                RequiredPayloadString(payload, "url"),
                                OptionalString(payload, "bearerTokenEnvironmentVariable"),
                                OptionalHeaderReferences(payload, "headers"),
                                OptionalString(payload, "oauthClientId"),
                                OptionalString(payload, "oauthClientSecretEnvironmentVariable"),
                                OptionalStringArray(payload, "oauthScopes"),
                                OptionalBoolean(payload, "enabled") ?? true,
                                OptionalUInt64(payload, "startupTimeoutSeconds"),
                                OptionalUInt64(payload, "toolTimeoutSeconds"),
                                OptionalTimeoutMap(payload),
                                OptionalBoolean(payload, "exposeImageBase64"))),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "delete":
                ok = await client.DeleteMcpServerAsync(
                        sessionId,
                        RequiredPayloadString(payload, "serverName"),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                throw new InvalidDataException("The MCP action is not supported.");
        }

        return new ExtensionActionOutcome(
            ok ? ExtensionActionStatus.Success : ExtensionActionStatus.NotFound,
            ok ? "" : "",
            RequiresReload: true,
            RequiresRestart: false);
    }

    private static async Task<ExtensionActionOutcome> ExecuteSkillsActionAsync(
        WorkspaceOperationContext context,
        string action,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var result = action switch
        {
            "add_path" => await context.Client.AddSkillPathAsync(
                    RequiredPayloadString(payload, "path"),
                    context.EngineWorkspacePath,
                    cancellationToken)
                .ConfigureAwait(false),
            "remove_path" => await context.Client.RemoveSkillPathAsync(
                    RequiredPayloadString(payload, "path"),
                    context.EngineWorkspacePath,
                    cancellationToken)
                .ConfigureAwait(false),
            "reset" => await context.Client.ResetSkillsAsync(
                    context.EngineWorkspacePath,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidDataException("The skills action is not supported."),
        };
        return new ExtensionActionOutcome(
            ExtensionActionStatus.Success,
            "",
            RequiresReload: true,
            RequiresRestart: false);
    }

    private static HookAction ParseHookAction(string action, JsonElement payload) => action switch
    {
        "reload" => new HookAction.Reload(),
        "trust" => new HookAction.Trust(),
        "untrust" => new HookAction.Untrust(),
        "add" => new HookAction.Add(RequiredPayloadString(payload, "path")),
        "remove" => new HookAction.Remove(RequiredPayloadString(payload, "path")),
        "enable" => new HookAction.Enable(RequiredPayloadString(payload, "hookName")),
        "disable" => new HookAction.Disable(RequiredPayloadString(payload, "hookName")),
        "toggle_source" => new HookAction.ToggleSource(
            RequiredPayloadStringArray(payload, "hookNames"),
            RequiredPayloadBoolean(payload, "disableSource")),
        _ => throw new InvalidDataException("The hooks action is not supported."),
    };

    private static PluginAction ParsePluginAction(string action, JsonElement payload) => action switch
    {
        "reload" => new PluginAction.Reload(),
        "install" => new PluginAction.Install(RequiredPayloadString(payload, "source")),
        "update" => new PluginAction.Update(OptionalPayloadString(payload, "pluginId")),
        "add" => new PluginAction.Add(RequiredPayloadString(payload, "path")),
        "remove" => new PluginAction.Remove(RequiredPayloadString(payload, "path")),
        "enable" => new PluginAction.Enable(RequiredPayloadString(payload, "pluginId")),
        "disable" => new PluginAction.Disable(RequiredPayloadString(payload, "pluginId")),
        _ => throw new InvalidDataException("The plugins action is not supported."),
    };

    private static MarketplaceAction ParseMarketplaceAction(string action, JsonElement payload)
    {
        if (action == "refresh")
        {
            return new MarketplaceAction.Refresh(OptionalPayloadString(payload, "source"));
        }
        var target = new MarketplacePluginTarget(
            RequiredPayloadString(payload, "source"),
            RequiredPayloadString(payload, "relativePath"));
        return action switch
        {
            "install" => new MarketplaceAction.Install(target),
            "update" => new MarketplaceAction.Update(target),
            "uninstall" => new MarketplaceAction.Uninstall(target),
            _ => throw new InvalidDataException("The marketplace action is not supported."),
        };
    }

    private ExtensionActionOutcome SanitizeOutcome(ExtensionActionOutcome outcome) => new(
        outcome.Status,
        outcome.Status switch
        {
            ExtensionActionStatus.Success => ExtensionOperationSucceededMessage,
            ExtensionActionStatus.ConfirmationRequired => ExtensionConfirmationMessage,
            ExtensionActionStatus.ValidationError
                when outcome.Message == ExtensionPolicyDeniedMarker => Message(
                    "团队策略禁止加载未经宿主验证的插件。",
                    "Team policy blocks plugins that are not verified by the native host."),
            ExtensionActionStatus.ValidationError => Message(
                "扩展参数无效。",
                "The extension parameters are invalid."),
            ExtensionActionStatus.NotFound => Message(
                "未找到扩展目标。",
                "The extension target was not found."),
            ExtensionActionStatus.Unsupported => Message(
                "当前引擎不支持此扩展操作。",
                "The current engine does not support this extension operation."),
            _ => ExtensionOperationErrorMessage,
        },
        outcome.RequiresReload,
        outcome.RequiresRestart);

    private static string RequiredPayloadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new InvalidDataException($"The extension payload field '{name}' is invalid.");

    private static bool RequiredPayloadBoolean(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidDataException($"The extension payload field '{name}' is invalid.");

    private static IReadOnlyList<string> RequiredPayloadStringArray(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() == 0)
        {
            throw new InvalidDataException($"The extension payload field '{name}' is invalid.");
        }
        return value.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String && item.GetString() is { } text &&
            !string.IsNullOrWhiteSpace(text)
                ? text
                : throw new InvalidDataException(
                    $"The extension payload field '{name}' is invalid.")).ToArray();
    }

    private static string? OptionalPayloadString(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String && value.GetString() is { } text &&
            !string.IsNullOrWhiteSpace(text)
                ? text
                : throw new InvalidDataException(
                    $"The extension payload field '{name}' is invalid.");
    }

    private static string? OptionalString(JsonElement payload, string name) =>
        !payload.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.ValueKind == JsonValueKind.String && value.GetString() is { } text
                ? text
                : throw new InvalidDataException($"The extension payload field '{name}' is invalid.");

    private static bool? OptionalBoolean(JsonElement payload, string name) =>
        !payload.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : throw new InvalidDataException($"The extension payload field '{name}' is invalid.");

    private static ulong? OptionalUInt64(JsonElement payload, string name) =>
        !payload.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number)
                ? number
                : throw new InvalidDataException($"The extension payload field '{name}' is invalid.");

    private static IReadOnlyList<string>? OptionalStringArray(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"The extension payload field '{name}' is invalid.");
        }
        return value.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String && item.GetString() is { } text
                ? text
                : throw new InvalidDataException($"The extension payload field '{name}' is invalid.")).ToArray();
    }

    private static IReadOnlyList<McpEnvironmentReference>? OptionalEnvironmentReferences(JsonElement payload, string name) =>
        OptionalEnvironment(payload, name).Select(item => new McpEnvironmentReference(item.Name, item.SourceVariable)).ToArray();

    private static IReadOnlyList<McpHeaderEnvironmentReference>? OptionalHeaderReferences(JsonElement payload, string name) =>
        OptionalEnvironment(payload, name).Select(item => new McpHeaderEnvironmentReference(item.Name, item.SourceVariable)).ToArray();

    private static IReadOnlyList<(string Name, string SourceVariable)> OptionalEnvironment(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"The extension payload field '{name}' is invalid.");
        }
        return value.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("name", out var envName) ||
                !item.TryGetProperty("sourceVariable", out var source) ||
                envName.ValueKind != JsonValueKind.String || source.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"The extension payload field '{name}' is invalid.");
            }
            return (envName.GetString()!, source.GetString()!);
        }).ToArray();
    }

    private static IReadOnlyDictionary<string, ulong>? OptionalTimeoutMap(JsonElement payload)
    {
        if (!payload.TryGetProperty("toolTimeouts", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The extension tool timeout map is invalid.");
        }
        return value.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetUInt64(out var seconds)
                ? seconds
                : throw new InvalidDataException("The extension tool timeout map is invalid."),
            StringComparer.Ordinal);
    }
}
