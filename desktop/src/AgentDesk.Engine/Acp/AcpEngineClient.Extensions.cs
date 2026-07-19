using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentDesk.Core.Engine;

namespace AgentDesk.Engine.Acp;

/// <summary>
/// ACP management calls for the local extension catalog.  These calls are kept in a
/// separate partial so the session transport remains readable and so every value
/// crossing the desktop boundary has one validation path.
/// </summary>
public sealed partial class AcpEngineClient
{
    private const int MaximumExtensionNameLength = 512;
    private const int MaximumExtensionPathLength = 32767;
    private const int MaximumExtensionTextLength = 64 * 1024;
    private const int MaximumExtensionListLength = 4096;
    private const int MaximumExtensionArgumentCount = 4096;
    private const int MemorySchemaVersion = 1;
    private const int MaximumMemoryFileCount = 512;
    private const int MaximumMemoryFileIdLength = 263;
    private const int MaximumMemoryFileNameLength = 255;
    private const int MaximumMemoryContentBytes = 64 * 1024;
    private const int MaximumMemoryMessageLength = 4096;

    public async Task<MemoryFileListing> ListMemoryFilesAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateMemorySession(sessionId, nameof(sessionId));
        var response = await _connection.SendRequestAsync(
            "agentdesk/v1/memory/list",
            new { sessionId = sessionId.Value },
            cancellationToken).ConfigureAwait(false);
        ExtensionOnly(response, "memory list result", "schemaVersion", "files", "truncated");
        ValidateMemorySchema(response);
        var entries = ExtensionArray(response, "files", MaximumMemoryFileCount);
        var files = new List<MemoryFileDescriptor>(entries.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            var file = ParseMemoryFile(entry);
            if (!ids.Add(file.Id.Value))
            {
                throw new InvalidDataException(
                    "The memory list response contained a duplicate file ID.");
            }
            files.Add(file);
        }
        return new MemoryFileListing(files, ExtensionBool(response, "truncated"));
    }

    public async Task<MemoryFileDocument> ReadMemoryFileAsync(
        SessionId sessionId,
        MemoryFileId fileId,
        CancellationToken cancellationToken = default)
    {
        ValidateMemorySession(sessionId, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(fileId);
        var response = await _connection.SendRequestAsync(
            "agentdesk/v1/memory/read",
            new { sessionId = sessionId.Value, fileId = fileId.Value },
            cancellationToken).ConfigureAwait(false);
        ExtensionOnly(response, "memory read result", "schemaVersion", "file", "content");
        ValidateMemorySchema(response);
        if (!response.TryGetProperty("file", out var fileElement))
        {
            throw new InvalidDataException("The memory read result did not contain file metadata.");
        }
        var file = ParseMemoryFile(fileElement);
        if (file.Id != fileId)
        {
            throw new InvalidDataException(
                "The memory read result was bound to another file ID.");
        }
        var content = MemoryContent(response, "content");
        if (file.ByteLength != (ulong)Encoding.UTF8.GetByteCount(content))
        {
            throw new InvalidDataException(
                "The memory read result contained inconsistent byte length metadata.");
        }
        return new MemoryFileDocument(file, content);
    }

    public async Task<MemoryMutationResult> WriteMemoryFileAsync(
        SessionId sessionId,
        MemoryFileId fileId,
        string content,
        bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        ValidateMemorySession(sessionId, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(fileId);
        ValidateMemoryContentInput(content, nameof(content));
        var response = await _connection.SendRequestAsync(
            "agentdesk/v1/memory/write",
            new
            {
                sessionId = sessionId.Value,
                fileId = fileId.Value,
                content,
                confirmed,
            },
            cancellationToken).ConfigureAwait(false);
        return ParseMemoryMutation(response, fileId);
    }

    public async Task<MemoryMutationResult> DeleteMemoryFileAsync(
        SessionId sessionId,
        MemoryFileId fileId,
        bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        ValidateMemorySession(sessionId, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(fileId);
        var response = await _connection.SendRequestAsync(
            "agentdesk/v1/memory/delete",
            new
            {
                sessionId = sessionId.Value,
                fileId = fileId.Value,
                confirmed,
            },
            cancellationToken).ConfigureAwait(false);
        return ParseMemoryMutation(response, fileId);
    }

    private static void ValidateMemorySession(SessionId? sessionId, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(sessionId, parameterName);
        ValidateSessionId(sessionId.Value, parameterName);
    }

    private static void ValidateMemoryContentInput(string content, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(content, parameterName);
        if (Encoding.UTF8.GetByteCount(content) > MaximumMemoryContentBytes)
        {
            throw new ArgumentException(
                "The memory content exceeds the 64 KiB UTF-8 limit.",
                parameterName);
        }
    }

    private static void ValidateMemorySchema(JsonElement response)
    {
        if (ExtensionInt(response, "schemaVersion", 0, int.MaxValue) != MemorySchemaVersion)
        {
            throw new NotSupportedException("The AgentDesk Memory schema is not supported.");
        }
    }

    private static MemoryFileDescriptor ParseMemoryFile(JsonElement item)
    {
        ExtensionOnly(
            item,
            "memory file",
            "id",
            "scope",
            "name",
            "byteLen",
            "modifiedAt",
            "writable");

        MemoryFileId id;
        try
        {
            id = new MemoryFileId(
                ExtensionText(item, "id", MaximumMemoryFileIdLength, allowEmpty: false));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "The memory file response contained an invalid opaque ID.",
                exception);
        }

        var scope = ExtensionEnum(
            item,
            "scope",
            "memory file scope",
            new Dictionary<string, MemoryFileScope>(StringComparer.Ordinal)
            {
                ["global"] = MemoryFileScope.Global,
                ["workspace"] = MemoryFileScope.Workspace,
                ["session"] = MemoryFileScope.Session,
            });
        var expectedScope = id.Value switch
        {
            "global" => MemoryFileScope.Global,
            "workspace" => MemoryFileScope.Workspace,
            _ => MemoryFileScope.Session,
        };
        if (scope != expectedScope)
        {
            throw new InvalidDataException(
                "The memory file response contained inconsistent scope metadata.");
        }

        return new MemoryFileDescriptor(
            id,
            scope,
            ExtensionText(item, "name", MaximumMemoryFileNameLength, allowEmpty: false),
            (ulong)ExtensionInt(item, "byteLen", 0, MaximumMemoryContentBytes),
            MemoryModifiedAt(item),
            ExtensionBool(item, "writable"));
    }

    private static DateTimeOffset? MemoryModifiedAt(JsonElement item)
    {
        if (!item.TryGetProperty("modifiedAt", out var value))
        {
            throw new InvalidDataException(
                "The memory file response did not contain modifiedAt metadata.");
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } raw &&
            raw.Length <= 64 &&
            DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            return timestamp;
        }
        throw new InvalidDataException(
            "The memory file response contained an invalid modifiedAt timestamp.");
    }

    private static string MemoryContent(JsonElement response, string propertyName)
    {
        if (!response.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } content ||
            Encoding.UTF8.GetByteCount(content) > MaximumMemoryContentBytes)
        {
            throw new InvalidDataException(
                $"The memory response contained invalid '{propertyName}' content.");
        }
        return content;
    }

    private static MemoryMutationResult ParseMemoryMutation(
        JsonElement response,
        MemoryFileId expectedId)
    {
        ExtensionOnly(response, "memory mutation result", "schemaVersion", "status", "message", "file");
        ValidateMemorySchema(response);
        var status = ExtensionEnum(
            response,
            "status",
            "memory mutation status",
            new Dictionary<string, MemoryMutationStatus>(StringComparer.Ordinal)
            {
                ["confirmation_required"] = MemoryMutationStatus.ConfirmationRequired,
                ["success"] = MemoryMutationStatus.Success,
                ["not_found"] = MemoryMutationStatus.NotFound,
            });
        if (!response.TryGetProperty("file", out var fileElement))
        {
            throw new InvalidDataException(
                "The memory mutation result did not contain file metadata.");
        }
        var file = fileElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Object => ParseMemoryFile(fileElement),
            _ => throw new InvalidDataException(
                "The memory mutation result contained invalid file metadata."),
        };
        if (file is not null && file.Id != expectedId)
        {
            throw new InvalidDataException(
                "The memory mutation result was bound to another file ID.");
        }
        if (status is MemoryMutationStatus.ConfirmationRequired or MemoryMutationStatus.NotFound &&
            file is not null)
        {
            throw new InvalidDataException(
                "The memory mutation result exposed file metadata for a non-success status.");
        }
        return new MemoryMutationResult(
            status,
            ExtensionText(response, "message", MaximumMemoryMessageLength, allowEmpty: true),
            file);
    }

    public async Task<IReadOnlyList<McpServerCatalogItem>> ListMcpServersAsync(
        SessionId? sessionId,
        bool useCache = true,
        CancellationToken cancellationToken = default)
    {
        if (sessionId is not null)
        {
            ValidateSessionId(sessionId.Value, nameof(sessionId));
        }

        var parameters = new Dictionary<string, object?>
        {
            ["cache"] = useCache,
        };
        if (sessionId is not null)
        {
            parameters["sessionId"] = sessionId.Value;
        }

        var result = ExtensionResult(await _connection.SendRequestAsync(
            "x.ai/mcp/list", parameters, cancellationToken).ConfigureAwait(false));
        ExtensionOnly(result, "MCP list result", "servers");
        var entries = ExtensionArray(result, "servers", MaximumExtensionListLength);
        var parsed = new List<McpServerCatalogItem>(entries.GetArrayLength());
        foreach (var entry in entries.EnumerateArray())
        {
            parsed.Add(ParseMcpServer(entry));
        }
        return parsed;
    }

    public async Task<bool> ToggleMcpServerAsync(
        SessionId sessionId,
        string serverName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId?.Value ?? throw new ArgumentNullException(nameof(sessionId)), nameof(sessionId));
        serverName = ExtensionInput(serverName, nameof(serverName), MaximumExtensionNameLength);
        var result = ExtensionResult(await _connection.SendRequestAsync(
            "x.ai/mcp/toggle",
            new { sessionId = sessionId.Value, serverName, enabled },
            cancellationToken).ConfigureAwait(false));
        return ParseOkResult(result, "MCP toggle result");
    }

    public async Task<bool> UpsertMcpServerAsync(
        McpServerUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSessionId(request.SessionId?.Value ?? throw new ArgumentException("The session is required.", nameof(request)), nameof(request));
        var serverName = ExtensionInput(request.ServerName, nameof(request), MaximumExtensionNameLength);
        ArgumentNullException.ThrowIfNull(request.Configuration);

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["session_id"] = request.SessionId.Value,
            ["server_name"] = serverName,
        };
        AddMcpConfiguration(parameters, request.Configuration);
        var result = ExtensionResult(await _connection.SendRequestAsync(
            "x.ai/mcp/upsert", parameters, cancellationToken).ConfigureAwait(false));
        return ParseOkResult(result, "MCP upsert result");
    }

    public async Task<bool> DeleteMcpServerAsync(
        SessionId sessionId,
        string serverName,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId?.Value ?? throw new ArgumentNullException(nameof(sessionId)), nameof(sessionId));
        serverName = ExtensionInput(serverName, nameof(serverName), MaximumExtensionNameLength);
        var result = ExtensionResult(await _connection.SendRequestAsync(
            "x.ai/mcp/delete",
            new { sessionId = sessionId.Value, serverName },
            cancellationToken).ConfigureAwait(false));
        return ParseOkResult(result, "MCP delete result");
    }

    public async Task<IReadOnlyList<SkillDescriptor>> ListSkillsAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var cwd = ValidateOptionalWorkingDirectory(workingDirectory) ??
            throw new ArgumentException("The working directory is invalid.", nameof(workingDirectory));
        var result = ExtensionResult(await _connection.SendRequestAsync(
            "x.ai/skills/list", new { cwd }, cancellationToken).ConfigureAwait(false));
        ExtensionOnly(result, "skills list result", "skills");
        return ParseSkills(ExtensionArray(result, "skills", MaximumExtensionListLength));
    }

    public Task<SkillPathMutationResult> AddSkillPathAsync(
        string path,
        string? workingDirectory,
        CancellationToken cancellationToken = default) =>
        MutateSkillPathAsync("x.ai/skills/add", path, workingDirectory, cancellationToken);

    public Task<SkillPathMutationResult> RemoveSkillPathAsync(
        string path,
        string? workingDirectory,
        CancellationToken cancellationToken = default) =>
        MutateSkillPathAsync("x.ai/skills/remove", path, workingDirectory, cancellationToken);

    public async Task<SkillPathMutationResult> ResetSkillsAsync(
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var cwd = ValidateOptionalWorkingDirectory(workingDirectory);
        var result = ExtensionResult(await _connection.SendRequestAsync(
            "x.ai/skills/reset", new { cwd }, cancellationToken).ConfigureAwait(false));
        return ParseSkillMutation(result, "skills reset result");
    }

    public async Task<SkillsConfiguration> GetSkillsConfigurationAsync(
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var cwd = ValidateOptionalWorkingDirectory(workingDirectory);
        var result = ExtensionResult(await _connection.SendRequestAsync(
            "x.ai/skills/config", new { cwd }, cancellationToken).ConfigureAwait(false));
        ExtensionOnly(result, "skills configuration result", "paths", "ignore", "totalSkills", "message", "skills");
        return new SkillsConfiguration(
            ExtensionPropertyStrings(result, "paths", MaximumExtensionListLength, MaximumExtensionPathLength),
            ExtensionPropertyStrings(result, "ignore", MaximumExtensionListLength, MaximumExtensionPathLength),
            ExtensionInt(result, "totalSkills", 0, MaximumExtensionListLength),
            ExtensionText(result, "message", MaximumExtensionTextLength, allowEmpty: true),
            ParseSkills(ExtensionArray(result, "skills", MaximumExtensionListLength)));
    }

    public async Task<HookCatalog> ListHooksAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId?.Value ?? throw new ArgumentNullException(nameof(sessionId)), nameof(sessionId));
        var result = ExtensionResult(await _connection.SendRequestAsync(
            "x.ai/hooks/list", new { sessionId = sessionId.Value }, cancellationToken).ConfigureAwait(false));
        ExtensionOnly(result, "hooks list result", "hooks", "projectTrusted", "loadErrors");
        var hooks = ExtensionArray(result, "hooks", MaximumExtensionListLength)
            .EnumerateArray().Select(ParseHook).ToArray();
        var errors = ExtensionPropertyStrings(result, "loadErrors", MaximumExtensionListLength, MaximumExtensionTextLength);
        return new HookCatalog(hooks, ExtensionBool(result, "projectTrusted"), errors);
    }

    public async Task<ExtensionActionOutcome> ExecuteHookActionAsync(
        SessionId sessionId,
        HookAction action,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId?.Value ?? throw new ArgumentNullException(nameof(sessionId)), nameof(sessionId));
        ArgumentNullException.ThrowIfNull(action);
        var result = ExtensionResult(await _connection.SendRequestAsync(
            "x.ai/hooks/action",
            new { sessionId = sessionId.Value, action = SerializeHookAction(action) },
            cancellationToken).ConfigureAwait(false));
        return ParseActionOutcome(result, "hook action result");
    }

    public async Task<IReadOnlyList<PluginDescriptor>> ListPluginsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId?.Value ?? throw new ArgumentNullException(nameof(sessionId)), nameof(sessionId));
        var result = ExtensionResult(await _connection.SendRequestAsync(
            "x.ai/plugins/list", new { sessionId = sessionId.Value }, cancellationToken).ConfigureAwait(false));
        ExtensionOnly(result, "plugins list result", "plugins");
        return ExtensionArray(result, "plugins", MaximumExtensionListLength)
            .EnumerateArray().Select(ParsePlugin).ToArray();
    }

    public async Task<ExtensionActionOutcome> ExecutePluginActionAsync(
        SessionId sessionId,
        PluginAction action,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId?.Value ?? throw new ArgumentNullException(nameof(sessionId)), nameof(sessionId));
        ArgumentNullException.ThrowIfNull(action);
        var result = ExtensionResult(await _connection.SendRequestAsync(
            "x.ai/plugins/action",
            new { sessionId = sessionId.Value, action = SerializePluginAction(action) },
            cancellationToken).ConfigureAwait(false));
        return ParseActionOutcome(result, "plugin action result");
    }

    public async Task<MarketplaceCatalog> ListMarketplaceAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId?.Value ?? throw new ArgumentNullException(nameof(sessionId)), nameof(sessionId));
        var result = ExtensionResult(await _connection.SendRequestAsync(
            "x.ai/marketplace/list",
            new { sessionId = sessionId.Value, clientIdentifier = "agentdesk" },
            cancellationToken).ConfigureAwait(false));
        ExtensionOnly(result, "marketplace list result", "sources");
        var sources = ExtensionArray(result, "sources", MaximumExtensionListLength)
            .EnumerateArray().Select(ParseMarketplaceSource).ToArray();
        return new MarketplaceCatalog(sources);
    }

    public async Task<ExtensionActionOutcome> ExecuteMarketplaceActionAsync(
        SessionId sessionId,
        MarketplaceAction action,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId?.Value ?? throw new ArgumentNullException(nameof(sessionId)), nameof(sessionId));
        ArgumentNullException.ThrowIfNull(action);
        var result = ExtensionResult(await _connection.SendRequestAsync(
            "x.ai/marketplace/action",
            new
            {
                sessionId = sessionId.Value,
                clientIdentifier = "agentdesk",
                action = SerializeMarketplaceAction(action),
            },
            cancellationToken).ConfigureAwait(false));
        return ParseActionOutcome(result, "marketplace action result");
    }

    private async Task<SkillPathMutationResult> MutateSkillPathAsync(
        string method,
        string path,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        path = ExtensionInput(path, nameof(path), MaximumExtensionPathLength);
        var cwd = ValidateOptionalWorkingDirectory(workingDirectory);
        var result = ExtensionResult(await _connection.SendRequestAsync(
            method, new { path, cwd }, cancellationToken).ConfigureAwait(false));
        return ParseSkillMutation(result, "skills mutation result");
    }

    private static void AddMcpConfiguration(
        IDictionary<string, object?> target,
        McpServerConfiguration configuration)
    {
        target["enabled"] = configuration.Enabled;
        if (configuration.StartupTimeoutSeconds is { } startup)
        {
            target["startupTimeoutSec"] = ExtensionNumber(startup, "startup timeout");
        }
        if (configuration.ToolTimeoutSeconds is { } tool)
        {
            target["toolTimeoutSec"] = ExtensionNumber(tool, "tool timeout");
        }
        if (configuration.ToolTimeouts is { } toolTimeouts)
        {
            if (toolTimeouts.Count > MaximumExtensionListLength)
            {
                throw new ArgumentException("Too many MCP tool timeout overrides.", nameof(configuration));
            }
            target["toolTimeouts"] = toolTimeouts.ToDictionary(
                pair => ExtensionInput(pair.Key, "tool name", MaximumExtensionNameLength),
                pair => ExtensionNumber(pair.Value, "tool timeout"),
                StringComparer.Ordinal);
        }
        if (configuration.ExposeImageBase64 is { } expose)
        {
            target["exposeImageBase64"] = expose;
        }

        switch (configuration)
        {
            case McpStdioServerConfiguration stdio:
                target["command"] = ExtensionInput(stdio.Command, "command", MaximumExtensionTextLength);
                target["args"] = ExtensionInputs(stdio.Arguments, "args", MaximumExtensionArgumentCount, MaximumExtensionTextLength);
                if (!string.IsNullOrWhiteSpace(stdio.WorkingDirectory))
                {
                    target["cwd"] = ValidateOptionalWorkingDirectory(stdio.WorkingDirectory);
                }
                target["env"] = SerializeEnvironmentReferences(stdio.Environment);
                break;
            case McpHttpServerConfiguration http:
                target["url"] = ExtensionInput(http.Url, "url", MaximumExtensionTextLength);
                target["type"] = "streamable_http";
                if (http.BearerTokenEnvironmentVariable is not null)
                {
                    target["bearer_token_env_var"] = EnvironmentName(http.BearerTokenEnvironmentVariable, "bearer token environment variable");
                }
                target["headers"] = SerializeHeaderReferences(http.Headers);
                if (http.OAuthClientId is not null)
                {
                    target["oauth_client_id"] = ExtensionInput(http.OAuthClientId, "OAuth client id", MaximumExtensionNameLength);
                }
                if (http.OAuthClientSecretEnvironmentVariable is not null)
                {
                    target["oauth_client_secret_env_var"] = EnvironmentName(http.OAuthClientSecretEnvironmentVariable, "OAuth secret environment variable");
                }
                if (http.OAuthScopes is not null)
                {
                    target["oauth_scopes"] = ExtensionInputs(http.OAuthScopes, "OAuth scopes", MaximumExtensionArgumentCount, MaximumExtensionNameLength);
                }
                break;
            default:
                throw new ArgumentException("The MCP server configuration is unsupported.", nameof(configuration));
        }
    }

    private static Dictionary<string, string> SerializeEnvironmentReferences(
        IReadOnlyList<McpEnvironmentReference>? references)
    {
        if (references is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        if (references.Count > MaximumExtensionListLength)
        {
            throw new ArgumentException("Too many MCP environment references.", nameof(references));
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            var name = EnvironmentName(reference.Name, "environment variable");
            if (!values.TryAdd(name, "${" + EnvironmentName(reference.SourceVariable, "source environment variable") + "}"))
            {
                throw new ArgumentException("Duplicate MCP environment variable.", nameof(references));
            }
        }
        return values;
    }

    private static Dictionary<string, string> SerializeHeaderReferences(
        IReadOnlyList<McpHeaderEnvironmentReference>? references)
    {
        if (references is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        if (references.Count > MaximumExtensionListLength)
        {
            throw new ArgumentException("Too many MCP header references.", nameof(references));
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            var name = ExtensionInput(reference.Name, "header name", 256);
            if (!values.TryAdd(name, "${" + EnvironmentName(reference.SourceVariable, "source environment variable") + "}"))
            {
                throw new ArgumentException("Duplicate MCP header.", nameof(references));
            }
        }
        return values;
    }

    private static object SerializeHookAction(HookAction action) => action switch
    {
        HookAction.Reload => new { type = "reload" },
        HookAction.Trust => new { type = "trust" },
        HookAction.Untrust => new { type = "untrust" },
        HookAction.Add value => new { type = "add", path = ExtensionInput(value.Path, "hook path", MaximumExtensionPathLength) },
        HookAction.Remove value => new { type = "remove", path = ExtensionInput(value.Path, "hook path", MaximumExtensionPathLength) },
        HookAction.Enable value => new { type = "enable", hook_name = ExtensionInput(value.HookName, "hook name", MaximumExtensionNameLength) },
        HookAction.Disable value => new { type = "disable", hook_name = ExtensionInput(value.HookName, "hook name", MaximumExtensionNameLength) },
        HookAction.ToggleSource value => new
        {
            type = "toggle_source",
            hook_names = ExtensionInputs(value.HookNames, "hook names", MaximumExtensionListLength, MaximumExtensionNameLength),
            disable = value.DisableSource,
        },
        _ => throw new ArgumentException("The hook action is unsupported.", nameof(action)),
    };

    private static object SerializePluginAction(PluginAction action) => action switch
    {
        PluginAction.Reload => new { type = "reload" },
        PluginAction.Install value => new { type = "install", source = ExtensionInput(value.Source, "plugin source", 8192) },
        PluginAction.Uninstall value => new { type = "uninstall", plugin_id = ExtensionInput(value.PluginId, "plugin id", MaximumExtensionNameLength), confirmed = value.Confirmed },
        PluginAction.Update value => new { type = "update", plugin_id = value.PluginId is null ? null : ExtensionInput(value.PluginId, "plugin id", MaximumExtensionNameLength) },
        PluginAction.Add value => new { type = "add", path = ExtensionInput(value.Path, "plugin path", MaximumExtensionPathLength) },
        PluginAction.Remove value => new { type = "remove", path = ExtensionInput(value.Path, "plugin path", MaximumExtensionPathLength) },
        PluginAction.Enable value => new { type = "enable", plugin_id = ExtensionInput(value.PluginId, "plugin id", MaximumExtensionNameLength) },
        PluginAction.Disable value => new { type = "disable", plugin_id = ExtensionInput(value.PluginId, "plugin id", MaximumExtensionNameLength) },
        _ => throw new ArgumentException("The plugin action is unsupported.", nameof(action)),
    };

    private static object SerializeMarketplaceAction(MarketplaceAction action) => action switch
    {
        MarketplaceAction.Refresh value => new { type = "refresh", source_url_or_path = value.Source is null ? null : ExtensionInput(value.Source, "marketplace source", MaximumExtensionPathLength) },
        MarketplaceAction.Install value => SerializeMarketplaceTarget("install", value.Target),
        MarketplaceAction.Update value => SerializeMarketplaceTarget("update", value.Target),
        MarketplaceAction.Uninstall value => SerializeMarketplaceTarget("uninstall", value.Target),
        _ => throw new ArgumentException("The marketplace action is unsupported.", nameof(action)),
    };

    private static object SerializeMarketplaceTarget(string type, MarketplacePluginTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var source = ExtensionInput(target.Source, "marketplace source", MaximumExtensionPathLength);
        var relativePath = ExtensionInput(target.RelativePath, "marketplace plugin path", MaximumExtensionPathLength);
        if (Path.IsPathRooted(relativePath) || relativePath.Split(['/', '\\']).Any(static part => part is ".." or "."))
        {
            throw new ArgumentException("The marketplace plugin path must remain within its source.", nameof(target));
        }
        return new { type, source_url_or_path = source, plugin_relative_path = relativePath };
    }

    private static McpServerCatalogItem ParseMcpServer(JsonElement item)
    {
        ExtensionOnly(item, "MCP server", "name", "displayName", "source", "sourceLabel", "type", "url", "scope", "scopeId", "scopeName", "command", "args", "env", "session", "pluginName");
        var transport = ExtensionEnum(item, "type", "MCP transport", new Dictionary<string, McpServerTransportKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["http"] = McpServerTransportKind.Http,
            ["streamable_http"] = McpServerTransportKind.Http,
            ["sse"] = McpServerTransportKind.Http,
            ["stdio"] = McpServerTransportKind.Stdio,
            ["managed_gateway"] = McpServerTransportKind.ManagedGateway,
            ["managed"] = McpServerTransportKind.ManagedGateway,
        });
        var source = ExtensionEnum(item, "source", "MCP source", new Dictionary<string, McpServerSource>(StringComparer.OrdinalIgnoreCase)
        {
            ["local"] = McpServerSource.Local,
            ["managed"] = McpServerSource.Managed,
        });
        var args = item.TryGetProperty("args", out var argsElement) ? ExtensionStringsArray(argsElement, "MCP args", MaximumExtensionArgumentCount, MaximumExtensionTextLength) : [];
        var envNames = ParseEnvironmentNames(item);
        var session = item.TryGetProperty("session", out var sessionElement) && sessionElement.ValueKind != JsonValueKind.Null
            ? ParseMcpSession(sessionElement)
            : null;
        return new McpServerCatalogItem(
            ExtensionText(item, "name", MaximumExtensionNameLength, false),
            ExtensionOptionalText(item, "displayName", MaximumExtensionTextLength),
            source,
            ExtensionOptionalText(item, "sourceLabel", MaximumExtensionTextLength),
            transport,
            ExtensionOptionalText(item, "url", MaximumExtensionTextLength),
            ExtensionOptionalText(item, "scope", MaximumExtensionNameLength),
            ExtensionOptionalText(item, "scopeId", MaximumExtensionNameLength),
            ExtensionOptionalText(item, "scopeName", MaximumExtensionTextLength),
            ExtensionOptionalText(item, "command", MaximumExtensionTextLength),
            args,
            envNames,
            session);
    }

    private static IReadOnlyList<string> ParseEnvironmentNames(JsonElement item)
    {
        if (!item.TryGetProperty("env", out var env) || env.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (env.ValueKind == JsonValueKind.Object)
        {
            return env.EnumerateObject().Select(static property => property.Name).ToArray();
        }
        if (env.ValueKind == JsonValueKind.Array)
        {
            return env.EnumerateArray().Select(entry => ExtensionText(entry, "name", MaximumExtensionNameLength, false)).ToArray();
        }
        throw new InvalidDataException("The MCP environment field was invalid.");
    }

    private static McpServerSessionInfo ParseMcpSession(JsonElement item)
    {
        ExtensionOnly(item, "MCP session", "enabled", "status", "tools", "authRequired");
        McpSessionStatus? status = item.TryGetProperty("status", out var statusElement) && statusElement.ValueKind != JsonValueKind.Null
            ? ExtensionEnum(statusElement, "MCP session status", new Dictionary<string, McpSessionStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["ready"] = McpSessionStatus.Ready,
                ["initializing"] = McpSessionStatus.Initializing,
                ["unavailable"] = McpSessionStatus.Unavailable,
                ["failed"] = McpSessionStatus.Unavailable,
            })
            : null;
        var tools = item.TryGetProperty("tools", out var toolsElement)
            ? ExtensionArray(toolsElement, "MCP tools", MaximumExtensionListLength).EnumerateArray().Select(ParseMcpTool).ToArray()
            : [];
        return new McpServerSessionInfo(
            ExtensionBool(item, "enabled"),
            status,
            tools,
            item.TryGetProperty("authRequired", out var auth) && auth.ValueKind == JsonValueKind.True && auth.GetBoolean());
    }

    private static McpToolInfo ParseMcpTool(JsonElement item)
    {
        ExtensionOnly(item, "MCP tool", "name", "displayName", "description", "enabled");
        return new McpToolInfo(
            ExtensionText(item, "name", MaximumExtensionNameLength, false),
            ExtensionOptionalText(item, "displayName", MaximumExtensionTextLength),
            ExtensionOptionalText(item, "description", MaximumExtensionTextLength),
            !item.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False || enabled.GetBoolean());
    }

    private static IReadOnlyList<SkillDescriptor> ParseSkills(JsonElement array) =>
        array.EnumerateArray().Select(ParseSkill).ToArray();

    private static SkillDescriptor ParseSkill(JsonElement item)
    {
        ExtensionOnly(item, "skill", "name", "displayName", "description", "has_user_specified_description", "paths", "when_to_use", "short_description", "author", "argument_hint", "license", "compatibility", "metadata", "path", "scope", "plugin_name", "plugin_version", "allowed_tools", "model", "effort", "user_invocable", "disable_model_invocation", "enabled");
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (item.TryGetProperty("metadata", out var metadataElement) && metadataElement.ValueKind != JsonValueKind.Null)
        {
            if (metadataElement.ValueKind != JsonValueKind.Object || metadataElement.EnumerateObject().Count() > MaximumExtensionListLength)
            {
                throw new InvalidDataException("The skill metadata was invalid.");
            }
            foreach (var property in metadataElement.EnumerateObject())
            {
                metadata[property.Name] = ExtensionText(property.Value, "skill metadata", MaximumExtensionTextLength, true);
            }
        }
        var scope = ExtensionEnum(item, "scope", "skill scope", new Dictionary<string, SkillScope>(StringComparer.OrdinalIgnoreCase)
        {
            ["local"] = SkillScope.Local,
            ["repo"] = SkillScope.Repo,
            ["user"] = SkillScope.User,
            ["server"] = SkillScope.Server,
            ["bundled"] = SkillScope.Bundled,
            ["plugin"] = SkillScope.Plugin,
        });
        return new SkillDescriptor(
            ExtensionText(item, "name", MaximumExtensionNameLength, false),
            ExtensionOptionalText(item, "displayName", MaximumExtensionTextLength),
            ExtensionText(item, "description", MaximumExtensionTextLength, true),
            ExtensionOptionalBool(item, "has_user_specified_description") ?? false,
            item.TryGetProperty("paths", out var paths) ? ExtensionStringsArray(paths, "skill paths", MaximumExtensionListLength, MaximumExtensionPathLength) : [],
            ExtensionOptionalText(item, "when_to_use", MaximumExtensionTextLength),
            ExtensionOptionalText(item, "short_description", MaximumExtensionTextLength),
            ExtensionOptionalText(item, "author", MaximumExtensionNameLength),
            ExtensionOptionalText(item, "argument_hint", MaximumExtensionTextLength),
            ExtensionOptionalText(item, "license", MaximumExtensionNameLength),
            ExtensionOptionalText(item, "compatibility", MaximumExtensionTextLength),
            new ReadOnlyDictionary<string, string>(metadata),
            ExtensionText(item, "path", MaximumExtensionPathLength, false),
            scope,
            ExtensionOptionalText(item, "plugin_name", MaximumExtensionNameLength),
            ExtensionOptionalText(item, "plugin_version", MaximumExtensionNameLength),
            item.TryGetProperty("allowed_tools", out var tools) ? ExtensionStringsArray(tools, "skill allowed tools", MaximumExtensionListLength, MaximumExtensionNameLength) : [],
            ExtensionOptionalText(item, "model", MaximumExtensionNameLength),
            ExtensionOptionalText(item, "effort", MaximumExtensionNameLength),
            ExtensionOptionalBool(item, "user_invocable") ?? true,
            ExtensionOptionalBool(item, "disable_model_invocation") ?? false,
            ExtensionOptionalBool(item, "enabled") ?? true);
    }

    private static SkillPathMutationResult ParseSkillMutation(JsonElement result, string context)
    {
        ExtensionOnly(result, context, "path", "addedCount", "total", "skills", "message");
        return new SkillPathMutationResult(
            ExtensionOptionalText(result, "path", MaximumExtensionPathLength),
            ExtensionOptionalInt(result, "addedCount"),
            ExtensionOptionalInt(result, "total"),
            result.TryGetProperty("skills", out var skills) ? ParseSkills(ExtensionArray(skills, "skills", MaximumExtensionListLength)) : [],
            ExtensionText(result, "message", MaximumExtensionTextLength, true));
    }

    private static HookDescriptor ParseHook(JsonElement item)
    {
        ExtensionOnly(item, "hook", "name", "event", "handlerType", "matcher", "command", "url", "timeoutMs", "sourceDir", "disabled");
        var hookEvent = ExtensionEnum(item, "event", "hook event", new Dictionary<string, HookEvent>(StringComparer.OrdinalIgnoreCase)
        {
            ["session_start"] = HookEvent.SessionStart,
            ["session_end"] = HookEvent.SessionEnd,
            ["stop"] = HookEvent.Stop,
            ["stop_failure"] = HookEvent.StopFailure,
            ["pre_tool_use"] = HookEvent.PreToolUse,
            ["post_tool_use"] = HookEvent.PostToolUse,
            ["post_tool_use_failure"] = HookEvent.PostToolUseFailure,
            ["permission_denied"] = HookEvent.PermissionDenied,
            ["user_prompt_submit"] = HookEvent.UserPromptSubmit,
            ["notification"] = HookEvent.Notification,
            ["subagent_start"] = HookEvent.SubagentStart,
            ["subagent_stop"] = HookEvent.SubagentStop,
            ["pre_compact"] = HookEvent.PreCompact,
            ["post_compact"] = HookEvent.PostCompact,
        });
        var handler = ExtensionEnum(item, "handlerType", "hook handler", new Dictionary<string, HookHandlerType>(StringComparer.OrdinalIgnoreCase)
        {
            ["command"] = HookHandlerType.Command,
            ["http"] = HookHandlerType.Http,
        });
        var timeoutMs = ExtensionOptionalInt(item, "timeoutMs", 600_000) ?? 0;
        if (timeoutMs is < 0 or > 600_000)
        {
            throw new InvalidDataException("The hook timeout was invalid.");
        }
        return new HookDescriptor(
            ExtensionText(item, "name", MaximumExtensionNameLength, false), hookEvent, handler,
            ExtensionOptionalText(item, "matcher", MaximumExtensionTextLength),
            ExtensionOptionalText(item, "command", MaximumExtensionTextLength),
            ExtensionOptionalText(item, "url", MaximumExtensionTextLength),
            TimeSpan.FromMilliseconds(timeoutMs),
            ExtensionText(item, "sourceDir", MaximumExtensionPathLength, true),
            ExtensionOptionalBool(item, "disabled") ?? false);
    }

    private static PluginDescriptor ParsePlugin(JsonElement item)
    {
        ExtensionOnly(item, "plugin", "name", "id", "root", "scope", "trusted", "enabled", "version", "description", "skillCount", "skillNames", "agentCount", "agentNames", "hookStatus", "hookCount", "mcpServerCount", "mcpStatus", "marketplaceSource", "origin", "conflict");
        var origin = item.TryGetProperty("origin", out var originElement) && originElement.ValueKind != JsonValueKind.Null ? ParsePluginOrigin(originElement) : null;
        return new PluginDescriptor(
            ExtensionText(item, "name", MaximumExtensionNameLength, false),
            ExtensionText(item, "id", MaximumExtensionNameLength, false),
            ExtensionText(item, "root", MaximumExtensionPathLength, false),
            ExtensionEnum(item, "scope", "plugin scope", new Dictionary<string, PluginScope>(StringComparer.OrdinalIgnoreCase)
            {
                ["cli"] = PluginScope.Cli,
                ["project"] = PluginScope.Project,
                ["user"] = PluginScope.User,
                ["config"] = PluginScope.Config,
            }),
            ExtensionBool(item, "trusted"), ExtensionBool(item, "enabled"),
            ExtensionOptionalText(item, "version", MaximumExtensionNameLength),
            ExtensionOptionalText(item, "description", MaximumExtensionTextLength),
            ExtensionInt(item, "skillCount", 0, MaximumExtensionListLength),
            item.TryGetProperty("skillNames", out var skillNames) ? ExtensionStringsArray(skillNames, "plugin skill names", MaximumExtensionListLength, MaximumExtensionNameLength) : [],
            ExtensionInt(item, "agentCount", 0, MaximumExtensionListLength),
            item.TryGetProperty("agentNames", out var agentNames) ? ExtensionStringsArray(agentNames, "plugin agent names", MaximumExtensionListLength, MaximumExtensionNameLength) : [],
            ExtensionEnum(item, "hookStatus", "plugin hook status", new Dictionary<string, PluginHookStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["active"] = PluginHookStatus.Active,
                ["active_inline"] = PluginHookStatus.ActiveInline,
                ["blocked"] = PluginHookStatus.Blocked,
                ["none"] = PluginHookStatus.None,
            }),
            ExtensionInt(item, "hookCount", 0, MaximumExtensionListLength),
            ExtensionInt(item, "mcpServerCount", 0, MaximumExtensionListLength),
            ExtensionEnum(item, "mcpStatus", "plugin MCP status", new Dictionary<string, PluginMcpStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["active"] = PluginMcpStatus.Active,
                ["active_inline"] = PluginMcpStatus.ActiveInline,
                ["blocked"] = PluginMcpStatus.Blocked,
                ["none"] = PluginMcpStatus.None,
            }),
            ExtensionOptionalText(item, "marketplaceSource", MaximumExtensionPathLength), origin,
            ExtensionOptionalText(item, "conflict", MaximumExtensionTextLength));
    }

    private static PluginOrigin ParsePluginOrigin(JsonElement item)
    {
        ExtensionOnly(item, "plugin origin", "type", "marketplace", "sourceName", "gitUrl");
        var kind = ExtensionEnum(item, "type", "plugin origin", new Dictionary<string, PluginOriginKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["cli_override"] = PluginOriginKind.CliOverride,
            ["project_grok"] = PluginOriginKind.ProjectGrok,
            ["project_claude"] = PluginOriginKind.ProjectClaude,
            ["user_grok"] = PluginOriginKind.UserGrok,
            ["user_claude"] = PluginOriginKind.UserClaude,
            ["claude_marketplace"] = PluginOriginKind.ClaudeMarketplace,
            ["claude_installed"] = PluginOriginKind.ClaudeInstalled,
            ["marketplace_install"] = PluginOriginKind.MarketplaceInstall,
            ["config_path"] = PluginOriginKind.ConfigPath,
            ["unknown"] = PluginOriginKind.Unknown,
        });
        return new PluginOrigin(kind, ExtensionOptionalText(item, "marketplace", MaximumExtensionPathLength), ExtensionOptionalText(item, "sourceName", MaximumExtensionNameLength), ExtensionOptionalText(item, "gitUrl", MaximumExtensionPathLength));
    }

    private static MarketplaceSourceDescriptor ParseMarketplaceSource(JsonElement item)
    {
        ExtensionOnly(item, "marketplace source", "sourceName", "sourceKind", "sourceUrlOrPath", "plugins", "error");
        return new MarketplaceSourceDescriptor(
            ExtensionText(item, "sourceName", MaximumExtensionNameLength, false),
            ExtensionEnum(item, "sourceKind", "marketplace source kind", new Dictionary<string, MarketplaceSourceKind>(StringComparer.OrdinalIgnoreCase)
            {
                ["git"] = MarketplaceSourceKind.Git,
                ["local"] = MarketplaceSourceKind.Local,
                ["failed"] = MarketplaceSourceKind.Failed,
            }),
            ExtensionText(item, "sourceUrlOrPath", MaximumExtensionPathLength, false),
            item.TryGetProperty("plugins", out var plugins)
                ? ExtensionArray(plugins, "marketplace plugins", MaximumExtensionListLength).EnumerateArray()
                    .Select(plugin => ParseMarketplacePlugin(plugin, ExtensionText(item, "sourceUrlOrPath", MaximumExtensionPathLength, false)))
                    .ToArray()
                : [],
            ExtensionOptionalText(item, "error", MaximumExtensionTextLength));
    }

    private static MarketplacePluginDescriptor ParseMarketplacePlugin(JsonElement item, string source)
    {
        ExtensionOnly(item, "marketplace plugin", "name", "version", "description", "category", "author", "tags", "keywords", "domains", "homepage", "relativePath", "skillCount", "hasHooks", "hasAgents", "hasMcp", "installStatus", "installedVersion", "components", "source");
        var components = item.TryGetProperty("components", out var componentElement) && componentElement.ValueKind != JsonValueKind.Null ? ParseMarketplaceComponents(componentElement) : null;
        source = ExtensionOptionalText(item, "source", MaximumExtensionPathLength) ?? source;
        var relativePath = ExtensionText(item, "relativePath", MaximumExtensionPathLength, false);
        if (Path.IsPathRooted(relativePath) || relativePath.Split(['/', '\\']).Any(static part => part is ".." or "."))
        {
            throw new InvalidDataException("The marketplace plugin relative path is unsafe.");
        }
        return new MarketplacePluginDescriptor(
            ExtensionText(item, "name", MaximumExtensionNameLength, false), ExtensionOptionalText(item, "version", MaximumExtensionNameLength),
            ExtensionOptionalText(item, "description", MaximumExtensionTextLength), ExtensionOptionalText(item, "category", MaximumExtensionNameLength),
            ExtensionOptionalText(item, "author", MaximumExtensionNameLength),
            item.TryGetProperty("tags", out var tags) ? ExtensionStringsArray(tags, "marketplace tags", MaximumExtensionListLength, MaximumExtensionNameLength) : [],
            item.TryGetProperty("keywords", out var keywords) ? ExtensionStringsArray(keywords, "marketplace keywords", MaximumExtensionListLength, MaximumExtensionNameLength) : [],
            item.TryGetProperty("domains", out var domains) ? ExtensionStringsArray(domains, "marketplace domains", MaximumExtensionListLength, MaximumExtensionNameLength) : [],
            ExtensionOptionalText(item, "homepage", MaximumExtensionPathLength), new MarketplacePluginTarget(source, relativePath),
            ExtensionInt(item, "skillCount", 0, MaximumExtensionListLength), ExtensionBool(item, "hasHooks"), ExtensionBool(item, "hasAgents"), ExtensionBool(item, "hasMcp"),
            ExtensionEnum(item, "installStatus", "marketplace install status", new Dictionary<string, MarketplaceInstallStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["not_installed"] = MarketplaceInstallStatus.NotInstalled,
                ["installed"] = MarketplaceInstallStatus.Installed,
                ["update_available"] = MarketplaceInstallStatus.UpdateAvailable,
            }),
            ExtensionOptionalText(item, "installedVersion", MaximumExtensionNameLength), components);
    }

    private static MarketplacePluginComponents ParseMarketplaceComponents(JsonElement item)
    {
        ExtensionOnly(item, "marketplace components", "skills", "commands", "agents", "mcpServers", "hooks", "lspServers");
        static IReadOnlyList<MarketplaceComponent> Parse(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
                ? ExtensionArray(value, name, MaximumExtensionListLength).EnumerateArray().Select(component =>
                {
                    ExtensionOnly(component, "marketplace component", "name", "description");
                    return new MarketplaceComponent(ExtensionText(component, "name", MaximumExtensionNameLength, false), ExtensionOptionalText(component, "description", MaximumExtensionTextLength));
                }).ToArray()
                : [];
        return new MarketplacePluginComponents(Parse(item, "skills"), Parse(item, "commands"), Parse(item, "agents"), Parse(item, "mcpServers"), Parse(item, "hooks"), Parse(item, "lspServers"));
    }

    private static ExtensionActionOutcome ParseActionOutcome(JsonElement result, string context)
    {
        ExtensionOnly(result, context, "status", "message", "requiresReload", "requiresRestart");
        var status = ExtensionEnum(result, "status", context, new Dictionary<string, ExtensionActionStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["success"] = ExtensionActionStatus.Success,
            ["validation_error"] = ExtensionActionStatus.ValidationError,
            ["confirmation_required"] = ExtensionActionStatus.ConfirmationRequired,
            ["not_found"] = ExtensionActionStatus.NotFound,
            ["internal_error"] = ExtensionActionStatus.InternalError,
            ["unsupported"] = ExtensionActionStatus.Unsupported,
        });
        return new ExtensionActionOutcome(status, ExtensionText(result, "message", MaximumExtensionTextLength, true), ExtensionBool(result, "requiresReload"), ExtensionBool(result, "requiresRestart"));
    }

    private static bool ParseOkResult(JsonElement result, string context)
    {
        ExtensionOnly(result, context, "ok");
        return ExtensionBool(result, "ok");
    }

    private static JsonElement ExtensionResult(JsonElement response) =>
        ReadRequiredExtensionResult(response);

    private static void ExtensionOnly(JsonElement element, string context, params string[] properties) =>
        EnsureOnlyProperties(element, context, properties);

    private static JsonElement ExtensionArray(JsonElement element, string name, int maximum)
    {
        var value = element.ValueKind == JsonValueKind.Array
            ? element
            : element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property)
                ? property
                : default;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum)
        {
            throw new InvalidDataException($"The {name} extension field was invalid.");
        }
        return value;
    }

    private static string ExtensionText(JsonElement element, string name, int maximum, bool allowEmpty)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || (!allowEmpty && text.Length == 0) || text.Length > maximum || text.Any(char.IsControl))
        {
            throw new InvalidDataException($"The extension field '{name}' was invalid.");
        }
        return text;
    }

    private static string? ExtensionOptionalText(JsonElement element, string name, int maximum)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return ExtensionText(element, name, maximum, allowEmpty: false);
    }

    private static bool ExtensionBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"The extension field '{name}' was invalid.");
        }
        return value.GetBoolean();
    }

    private static bool? ExtensionOptionalBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return ExtensionBool(element, name);
    }

    private static int ExtensionInt(JsonElement element, string name, int minimum, int maximum)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetInt32(out var number) || number < minimum || number > maximum)
        {
            throw new InvalidDataException($"The extension field '{name}' was invalid.");
        }
        return number;
    }

    private static int? ExtensionOptionalInt(JsonElement element, string name, int maximum = MaximumExtensionListLength)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return ExtensionInt(element, name, 0, maximum);
    }

    private static IReadOnlyList<string> ExtensionStringsArray(JsonElement element, string context, int maximumCount, int maximumLength)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maximumCount)
        {
            throw new InvalidDataException($"The {context} was invalid.");
        }
        return element.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text || text.Length > maximumLength || text.Any(char.IsControl))
            {
                throw new InvalidDataException($"The {context} contained an invalid value.");
            }
            return text;
        }).ToArray();
    }

    private static IReadOnlyList<string> ExtensionPropertyStrings(
        JsonElement parent,
        string name,
        int maximumCount,
        int maximumLength)
    {
        if (!parent.TryGetProperty(name, out var property))
        {
            throw new InvalidDataException($"The extension field '{name}' was invalid.");
        }
        return ExtensionStringsArray(property, name, maximumCount, maximumLength);
    }

    private static IReadOnlyList<string> ExtensionInputs(IReadOnlyList<string>? values, string parameterName, int maximumCount, int maximumLength)
    {
        if (values is null)
        {
            return [];
        }
        if (values.Count > maximumCount)
        {
            throw new ArgumentException($"Too many {parameterName}.", parameterName);
        }
        return values.Select(value => ExtensionInput(value, parameterName, maximumLength)).ToArray();
    }

    private static string ExtensionInput(string value, string parameterName, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"The {parameterName} is invalid.", parameterName);
        }
        return value;
    }

    private static string EnvironmentName(string value, string parameterName)
    {
        value = ExtensionInput(value, parameterName, 256);
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_')))
        {
            throw new ArgumentException($"The {parameterName} is invalid.", parameterName);
        }
        return value;
    }

    private static ulong ExtensionNumber(ulong value, string parameterName)
    {
        if (value is 0 or > 86_400)
        {
            throw new ArgumentException($"The {parameterName} is invalid.", parameterName);
        }
        return value;
    }

    private static T ExtensionEnum<T>(JsonElement element, string name, string context, IReadOnlyDictionary<string, T> values)
    {
        var raw = ExtensionText(element, name, 128, false);
        return values.TryGetValue(raw, out var value)
            ? value
            : throw new InvalidDataException($"The {context} contained an unknown value.");
    }

    private static T ExtensionEnum<T>(JsonElement value, string context, IReadOnlyDictionary<string, T> values)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } raw || !values.TryGetValue(raw, out var parsed))
        {
            throw new InvalidDataException($"The {context} contained an unknown value.");
        }
        return parsed;
    }
}
