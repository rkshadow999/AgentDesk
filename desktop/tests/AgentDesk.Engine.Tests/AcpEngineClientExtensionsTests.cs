using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using AgentDesk.Core.Engine;
using AgentDesk.Engine.Acp;

namespace AgentDesk.Engine.Tests;

public sealed class AcpEngineClientExtensionsTests
{
    [Fact]
    public async Task ListMemoryFilesAsync_UsesVersionedWireShapeAndParsesDescriptors()
    {
        await using var peer = new AcpPeer();
        var operation = peer.Client.ListMemoryFilesAsync(
            new SessionId("session-42"),
            peer.Timeout.Token);
        using var request = await peer.ReadMessageAsync();

        AssertRequest(request.RootElement, 1, "agentdesk/v1/memory/list");
        AssertPropertyNames(request.RootElement.GetProperty("params"), "sessionId");
        Assert.Equal(
            "session-42",
            request.RootElement.GetProperty("params").GetProperty("sessionId").GetString());

        await peer.RespondAsync(
            1,
            """
            {
              "schemaVersion": 1,
              "files": [{
                "id": "workspace",
                "scope": "workspace",
                "name": "Workspace MEMORY.md",
                "byteLen": 17,
                "modifiedAt": "2026-07-18T08:30:00.000Z",
                "writable": true
              }],
              "truncated": false
            }
            """);

        var listing = await operation;
        var file = Assert.Single(listing.Files);
        Assert.False(listing.Truncated);
        Assert.Equal(new MemoryFileId("workspace"), file.Id);
        Assert.Equal(MemoryFileScope.Workspace, file.Scope);
        Assert.Equal("Workspace MEMORY.md", file.Name);
        Assert.Equal(17UL, file.ByteLength);
        Assert.Equal(DateTimeOffset.Parse("2026-07-18T08:30:00.000Z"), file.ModifiedAt);
        Assert.True(file.Writable);
    }

    [Fact]
    public async Task ReadMemoryFileAsync_UsesOpaqueIdAndParsesUtf8Document()
    {
        await using var peer = new AcpPeer();
        var operation = peer.Client.ReadMemoryFileAsync(
            new SessionId("session-42"),
            new MemoryFileId("workspace"),
            peer.Timeout.Token);
        using var request = await peer.ReadMessageAsync();

        AssertRequest(request.RootElement, 1, "agentdesk/v1/memory/read");
        AssertPropertyNames(request.RootElement.GetProperty("params"), "sessionId", "fileId");
        Assert.Equal(
            "workspace",
            request.RootElement.GetProperty("params").GetProperty("fileId").GetString());

        await peer.RespondAsync(
            1,
            """
            {
              "schemaVersion": 1,
              "file": {
                "id": "workspace",
                "scope": "workspace",
                "name": "Workspace MEMORY.md",
                "byteLen": 25,
                "modifiedAt": null,
                "writable": true
              },
              "content": "# 项目记忆\n\nAgentDesk"
            }
            """);

        var document = await operation;
        Assert.Equal("workspace", document.File.Id.Value);
        Assert.Equal("# 项目记忆\n\nAgentDesk", document.Content);
        Assert.Equal((ulong)Encoding.UTF8.GetByteCount(document.Content), document.File.ByteLength);
    }

    [Fact]
    public async Task MemoryMutations_SendConfirmationAndMapTypedOutcomes()
    {
        await using var peer = new AcpPeer();
        var sessionId = new SessionId("session-42");
        var fileId = new MemoryFileId("global");

        var write = peer.Client.WriteMemoryFileAsync(
            sessionId,
            fileId,
            "new memory",
            confirmed: false,
            peer.Timeout.Token);
        using (var request = await peer.ReadMessageAsync())
        {
            AssertRequest(request.RootElement, 1, "agentdesk/v1/memory/write");
            var parameters = request.RootElement.GetProperty("params");
            AssertPropertyNames(parameters, "sessionId", "fileId", "content", "confirmed");
            Assert.False(parameters.GetProperty("confirmed").GetBoolean());
            Assert.Equal("new memory", parameters.GetProperty("content").GetString());
        }
        await peer.RespondAsync(
            1,
            """
            {
              "schemaVersion": 1,
              "status": "confirmation_required",
              "message": "Writing memory requires explicit confirmation.",
              "file": null
            }
            """);
        var pending = await write;
        Assert.Equal(MemoryMutationStatus.ConfirmationRequired, pending.Status);
        Assert.Null(pending.File);

        var delete = peer.Client.DeleteMemoryFileAsync(
            sessionId,
            fileId,
            confirmed: true,
            peer.Timeout.Token);
        using (var request = await peer.ReadMessageAsync())
        {
            AssertRequest(request.RootElement, 2, "agentdesk/v1/memory/delete");
            var parameters = request.RootElement.GetProperty("params");
            AssertPropertyNames(parameters, "sessionId", "fileId", "confirmed");
            Assert.True(parameters.GetProperty("confirmed").GetBoolean());
        }
        await peer.RespondAsync(
            2,
            """
            {
              "schemaVersion": 1,
              "status": "success",
              "message": "Memory file deleted.",
              "file": null
            }
            """);
        Assert.Equal(MemoryMutationStatus.Success, (await delete).Status);
    }

    [Fact]
    public async Task MemoryMethods_RejectUnsafeIdsAndOversizedUtf8ContentBeforeTransport()
    {
        Assert.Throws<ArgumentException>(() => new MemoryFileId("../MEMORY.md"));
        Assert.Throws<ArgumentException>(() => new MemoryFileId("session/subdir/note.md"));

        await using var peer = new AcpPeer();
        var oversized = string.Concat(Enumerable.Repeat("界", 21_846));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Client.WriteMemoryFileAsync(
            new SessionId("session-42"),
            new MemoryFileId("workspace"),
            oversized,
            confirmed: true,
            peer.Timeout.Token));
    }

    [Fact]
    public async Task MemoryMethods_RejectUnboundedOrInconsistentResponses()
    {
        await using var peer = new AcpPeer();
        var list = peer.Client.ListMemoryFilesAsync(
            new SessionId("session-42"),
            peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        var files = Enumerable.Range(0, 513).Select(index => new
        {
            id = $"session/2026-07-18-{index:D4}.md",
            scope = "session",
            name = $"2026-07-18-{index:D4}.md",
            byteLen = 0,
            modifiedAt = (string?)null,
            writable = true,
        }).ToArray();
        await peer.RespondAsync(1, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            files,
            truncated = true,
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => list);

        var read = peer.Client.ReadMemoryFileAsync(
            new SessionId("session-42"),
            new MemoryFileId("workspace"),
            peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            2,
            """
            {
              "schemaVersion": 1,
              "file": {
                "id": "workspace",
                "scope": "workspace",
                "name": "Workspace MEMORY.md",
                "byteLen": 1,
                "modifiedAt": null,
                "writable": true
              },
              "content": "different byte length"
            }
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => read);
    }

    [Fact]
    public async Task ListMcpServersAsync_ParsesCatalogWithoutProjectingEnvironmentValues()
    {
        await using var peer = new AcpPeer();
        var operation = peer.Client.ListMcpServersAsync(
            new SessionId("session-42"),
            useCache: false,
            peer.Timeout.Token);
        using var request = await peer.ReadMessageAsync();

        AssertRequest(request.RootElement, 1, "x.ai/mcp/list");
        AssertPropertyNames(request.RootElement.GetProperty("params"), "sessionId", "cache");
        Assert.False(request.RootElement.GetProperty("params").GetProperty("cache").GetBoolean());

        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "servers": [{
                  "name": "github",
                  "displayName": "GitHub",
                  "source": "local",
                  "sourceLabel": "config.toml",
                  "type": "stdio",
                  "command": "npx",
                  "args": ["-y", "@modelcontextprotocol/server-github"],
                  "env": [{"name": "GITHUB_TOKEN", "value": "must-not-reach-ui"}],
                  "session": {
                    "enabled": true,
                    "status": "ready",
                    "tools": [{
                      "name": "issues.list",
                      "displayName": "List issues",
                      "description": "Lists repository issues",
                      "enabled": true
                    }],
                    "authRequired": false
                  }
                }]
              }
            }
            """);

        var server = Assert.Single(await operation);
        Assert.Equal(McpServerTransportKind.Stdio, server.Transport);
        Assert.Equal(McpServerSource.Local, server.Source);
        Assert.Equal("npx", server.Command);
        Assert.Equal(["GITHUB_TOKEN"], server.EnvironmentVariableNames);
        Assert.DoesNotContain("must-not-reach-ui", server.ToString(), StringComparison.Ordinal);
        Assert.Equal(McpSessionStatus.Ready, server.Session?.Status);
        Assert.Equal("issues.list", Assert.Single(server.Session!.Tools).Name);
    }

    [Fact]
    public async Task McpMutationMethods_UseBoundedTypedWireShapes()
    {
        await using var peer = new AcpPeer();
        var sessionId = new SessionId("session-42");

        var toggle = peer.Client.ToggleMcpServerAsync(
            sessionId,
            "github",
            enabled: false,
            peer.Timeout.Token);
        using (var request = await peer.ReadMessageAsync())
        {
            AssertRequest(request.RootElement, 1, "x.ai/mcp/toggle");
            var parameters = request.RootElement.GetProperty("params");
            AssertPropertyNames(parameters, "sessionId", "serverName", "enabled");
        }
        await peer.RespondAsync(1, "{\"result\":{\"ok\":true}}");
        Assert.True(await toggle);

        var upsert = peer.Client.UpsertMcpServerAsync(
            new McpServerUpsertRequest(
                sessionId,
                "github",
                new McpStdioServerConfiguration(
                    "npx",
                    ["-y", "@modelcontextprotocol/server-github"],
                    [new McpEnvironmentReference("GITHUB_TOKEN", "AGENTDESK_GITHUB_TOKEN")],
                    Enabled: true,
                    StartupTimeoutSeconds: 15,
                    ToolTimeoutSeconds: 60)),
            peer.Timeout.Token);
        using (var request = await peer.ReadMessageAsync())
        {
            AssertRequest(request.RootElement, 2, "x.ai/mcp/upsert");
            var parameters = request.RootElement.GetProperty("params");
            AssertPropertyNames(
                parameters,
                "session_id",
                "server_name",
                "command",
                "args",
                "env",
                "enabled",
                "startupTimeoutSec",
                "toolTimeoutSec");
            Assert.Equal(
                "${AGENTDESK_GITHUB_TOKEN}",
                parameters.GetProperty("env").GetProperty("GITHUB_TOKEN").GetString());
            Assert.DoesNotContain("must-not-reach-ui", request.RootElement.GetRawText());
        }
        await peer.RespondAsync(2, "{\"result\":{\"ok\":true}}");
        Assert.True(await upsert);

        var delete = peer.Client.DeleteMcpServerAsync(sessionId, "github", peer.Timeout.Token);
        using (var request = await peer.ReadMessageAsync())
        {
            AssertRequest(request.RootElement, 3, "x.ai/mcp/delete");
            AssertPropertyNames(request.RootElement.GetProperty("params"), "sessionId", "serverName");
        }
        await peer.RespondAsync(3, "{\"result\":{\"ok\":true}}");
        Assert.True(await delete);
    }

    [Fact]
    public async Task SkillsMethods_MapListMutationsResetAndConfig()
    {
        await using var peer = new AcpPeer();
        var list = peer.Client.ListSkillsAsync("C:\\repo", peer.Timeout.Token);
        using (var request = await peer.ReadMessageAsync())
        {
            AssertRequest(request.RootElement, 1, "x.ai/skills/list");
            Assert.Equal("C:\\repo", request.RootElement.GetProperty("params").GetProperty("cwd").GetString());
        }
        await peer.RespondAsync(
            1,
            SkillsResultJson(
                Skill("review", "C:\\repo\\.grok\\skills\\review\\SKILL.md")));
        Assert.Equal("review", Assert.Single(await list).Name);

        var add = peer.Client.AddSkillPathAsync("C:\\skills", "C:\\repo", peer.Timeout.Token);
        using (var request = await peer.ReadMessageAsync())
        {
            AssertRequest(request.RootElement, 2, "x.ai/skills/add");
            AssertPropertyNames(request.RootElement.GetProperty("params"), "path", "cwd");
        }
        await peer.RespondAsync(
            2,
            JsonSerializer.Serialize(new
            {
                result = new
                {
                    addedCount = 1,
                    total = 1,
                    path = "C:\\skills",
                    skills = new[] { Skill("review", "C:\\skills\\review\\SKILL.md") },
                    message = "Added",
                },
            }));
        Assert.Equal(1, (await add).AddedCount);

        var remove = peer.Client.RemoveSkillPathAsync("C:\\skills", "C:\\repo", peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(3, "{\"result\":{\"path\":\"C:\\\\skills\",\"skills\":[],\"message\":\"Removed\"}}");
        Assert.Empty((await remove).Skills);

        var reset = peer.Client.ResetSkillsAsync("C:\\repo", peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(4, "{\"result\":{\"skills\":[],\"message\":\"Reset\"}}");
        Assert.Empty((await reset).Skills);

        var config = peer.Client.GetSkillsConfigurationAsync("C:\\repo", peer.Timeout.Token);
        using (var request = await peer.ReadMessageAsync())
        {
            AssertRequest(request.RootElement, 5, "x.ai/skills/config");
        }
        await peer.RespondAsync(5, "{\"result\":{\"paths\":[\"C:\\\\skills\"],\"ignore\":[\"C:\\\\ignored\"],\"totalSkills\":0,\"message\":\"Configured\",\"skills\":[]}}");
        var configuration = await config;
        Assert.Equal(["C:\\skills"], configuration.Paths);
        Assert.Equal(["C:\\ignored"], configuration.IgnoredPaths);
    }

    [Fact]
    public async Task HooksAndPlugins_MapListsAndTypedActions()
    {
        await using var peer = new AcpPeer();
        var sessionId = new SessionId("session-42");

        var hooks = peer.Client.ListHooksAsync(sessionId, peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            """
            {"result":{"hooks":[{
              "name":"global/safety:pre_tool_use[0].hooks[0]",
              "event":"pre_tool_use",
              "handlerType":"command",
              "matcher":"run_terminal_command",
              "command":"${HOOKS_HOME}/safety.ps1",
              "url":null,
              "timeoutMs":5000,
              "sourceDir":"C:\\hooks",
              "disabled":false
            }],"projectTrusted":true,"loadErrors":[]}}
            """);
        var hook = Assert.Single((await hooks).Hooks);
        Assert.Equal(HookEvent.PreToolUse, hook.Event);
        Assert.Equal(HookHandlerType.Command, hook.HandlerType);

        var hookAction = peer.Client.ExecuteHookActionAsync(
            sessionId,
            new HookAction.Disable(hook.Name),
            peer.Timeout.Token);
        using (var request = await peer.ReadMessageAsync())
        {
            AssertRequest(request.RootElement, 2, "x.ai/hooks/action");
            var action = request.RootElement.GetProperty("params").GetProperty("action");
            AssertPropertyNames(action, "type", "hook_name");
            Assert.Equal("disable", action.GetProperty("type").GetString());
        }
        await peer.RespondAsync(2, SuccessfulOutcomeJson("Disabled"));
        Assert.Equal(ExtensionActionStatus.Success, (await hookAction).Status);

        var plugins = peer.Client.ListPluginsAsync(sessionId, peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            3,
            """
            {"result":{"plugins":[{
              "name":"superpowers","id":"user/12345678/superpowers","root":"C:\\plugins\\superpowers",
              "scope":"user","trusted":true,"enabled":true,"version":"1.0.0","description":"Workflow tools",
              "skillCount":1,"skillNames":["brainstorming"],"agentCount":0,"hookStatus":"none","hookCount":0,
              "mcpServerCount":0,"mcpStatus":"none","origin":{"type":"user_grok"}
            }]}}
            """);
        var plugin = Assert.Single(await plugins);
        Assert.Equal(PluginScope.User, plugin.Scope);
        Assert.Equal(PluginOriginKind.UserGrok, plugin.Origin?.Kind);

        var pluginAction = peer.Client.ExecutePluginActionAsync(
            sessionId,
            new PluginAction.Enable(plugin.Id),
            peer.Timeout.Token);
        using (var request = await peer.ReadMessageAsync())
        {
            AssertRequest(request.RootElement, 4, "x.ai/plugins/action");
            var action = request.RootElement.GetProperty("params").GetProperty("action");
            AssertPropertyNames(action, "type", "plugin_id");
        }
        await peer.RespondAsync(4, SuccessfulOutcomeJson("Enabled"));
        Assert.False((await pluginAction).RequiresRestart);
    }

    [Fact]
    public async Task MarketplaceMethods_MapCatalogAndInstallAction()
    {
        await using var peer = new AcpPeer();
        var sessionId = new SessionId("session-42");

        var list = peer.Client.ListMarketplaceAsync(sessionId, peer.Timeout.Token);
        using (var request = await peer.ReadMessageAsync())
        {
            AssertRequest(request.RootElement, 1, "x.ai/marketplace/list");
            var parameters = request.RootElement.GetProperty("params");
            AssertPropertyNames(parameters, "clientIdentifier", "sessionId");
            Assert.Equal("agentdesk", parameters.GetProperty("clientIdentifier").GetString());
        }
        await peer.RespondAsync(
            1,
            """
            {"result":{"sources":[{
              "sourceName":"official","sourceKind":"git","sourceUrlOrPath":"https://example.test/plugins.git",
              "plugins":[{
                "name":"review","version":"1.2.0","description":"Code review","category":"development","author":"xAI",
                "tags":["code"],"keywords":["review"],"domains":[],"homepage":"https://example.test/review",
                "relativePath":"plugins/review","skillCount":1,"hasHooks":false,"hasAgents":false,"hasMcp":false,
                "installStatus":"not_installed","installedVersion":null,
                "components":{"skills":[{"name":"review","description":"Review changes"}]}
              }],"error":null
            }]}}
            """);
        var source = Assert.Single((await list).Sources);
        var plugin = Assert.Single(source.Plugins);
        Assert.Equal(MarketplaceSourceKind.Git, source.Kind);
        Assert.Equal(MarketplaceInstallStatus.NotInstalled, plugin.InstallStatus);

        var action = peer.Client.ExecuteMarketplaceActionAsync(
            sessionId,
            new MarketplaceAction.Install(plugin.Target),
            peer.Timeout.Token);
        using (var request = await peer.ReadMessageAsync())
        {
            AssertRequest(request.RootElement, 2, "x.ai/marketplace/action");
            var parameters = request.RootElement.GetProperty("params");
            Assert.Equal("agentdesk", parameters.GetProperty("clientIdentifier").GetString());
            var actionJson = parameters.GetProperty("action");
            AssertPropertyNames(actionJson, "type", "source_url_or_path", "plugin_relative_path");
            Assert.Equal("install", actionJson.GetProperty("type").GetString());
        }
        await peer.RespondAsync(2, SuccessfulOutcomeJson("Installed"));
        Assert.True((await action).RequiresReload);
    }

    [Fact]
    public async Task MarketplaceRefresh_TargetsTheSelectedSourceWithRustContractField()
    {
        await using var peer = new AcpPeer();
        var sessionId = new SessionId("session-42");

        var list = peer.Client.ListMarketplaceAsync(sessionId, peer.Timeout.Token);
        using (var request = await peer.ReadMessageAsync())
        {
            AssertRequest(request.RootElement, 1, "x.ai/marketplace/list");
        }
        await peer.RespondAsync(
            1,
            """
            {"result":{"sources":[
              {"sourceName":"first","sourceKind":"local","sourceUrlOrPath":"C:\\marketplaces\\first","plugins":[]},
              {"sourceName":"second","sourceKind":"git","sourceUrlOrPath":"https://example.test/second.git","plugins":[]}
            ]}}
            """);
        var sources = (await list).Sources;
        Assert.Equal(2, sources.Count);

        var refresh = peer.Client.ExecuteMarketplaceActionAsync(
            sessionId,
            new MarketplaceAction.Refresh(sources[1].Source),
            peer.Timeout.Token);
        using (var request = await peer.ReadMessageAsync())
        {
            AssertRequest(request.RootElement, 2, "x.ai/marketplace/action");
            var action = request.RootElement.GetProperty("params").GetProperty("action");
            AssertPropertyNames(action, "type", "source_url_or_path");
            Assert.Equal("refresh", action.GetProperty("type").GetString());
            Assert.Equal(
                "https://example.test/second.git",
                action.GetProperty("source_url_or_path").GetString());
        }
        await peer.RespondAsync(2, SuccessfulOutcomeJson("Refreshed 1 source(s)."));
        Assert.Equal(ExtensionActionStatus.Success, (await refresh).Status);
    }

    [Fact]
    public async Task ExtensionWrites_RejectUnboundedOrAmbiguousInputBeforeTransport()
    {
        await using var peer = new AcpPeer();
        var sessionId = new SessionId("session-42");

        await Assert.ThrowsAsync<ArgumentException>(() => peer.Client.ToggleMcpServerAsync(
            sessionId,
            "server\nother",
            true,
            peer.Timeout.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Client.AddSkillPathAsync(
            new string('p', 32768),
            "C:\\repo",
            peer.Timeout.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Client.ExecutePluginActionAsync(
            sessionId,
            new PluginAction.Install(new string('s', 8193)),
            peer.Timeout.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => peer.Client.ExecuteMarketplaceActionAsync(
            sessionId,
            new MarketplaceAction.Install(
                new MarketplacePluginTarget("https://example.test/catalog.git", "../outside")),
            peer.Timeout.Token));
    }

    [Fact]
    public async Task ActionOutcome_RejectsUnknownFieldsAndFutureEnums()
    {
        await using var peer = new AcpPeer();
        var operation = peer.Client.ExecuteHookActionAsync(
            new SessionId("session-42"),
            new HookAction.Reload(),
            peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();

        await peer.RespondAsync(
            1,
            "{\"result\":{\"status\":\"future_status\",\"message\":\"x\",\"requiresReload\":false,\"requiresRestart\":false,\"environment\":{\"TOKEN\":\"secret\"}}}");

        await Assert.ThrowsAsync<InvalidDataException>(() => operation);
    }

    private static object Skill(string name, string path) =>
        new
        {
            name,
            description = "Reviews changes",
            has_user_specified_description = true,
            path,
            scope = "local",
            user_invocable = true,
            disable_model_invocation = false,
            enabled = true,
        };

    private static string SkillsResultJson(params object[] skills) => JsonSerializer.Serialize(new
    {
        result = new { skills },
    });

    private static string SuccessfulOutcomeJson(string message) => JsonSerializer.Serialize(new
    {
        result = new
        {
            status = "success",
            message,
            requiresReload = true,
            requiresRestart = false,
        },
    });

    private static void AssertRequest(JsonElement message, long id, string method)
    {
        Assert.Equal("2.0", message.GetProperty("jsonrpc").GetString());
        Assert.Equal(id, message.GetProperty("id").GetInt64());
        Assert.Equal(ToWireMethod(method), message.GetProperty("method").GetString());
    }

    private static string ToWireMethod(string method) =>
        method.StartsWith("agentdesk/", StringComparison.Ordinal) ||
        method.StartsWith("x.ai/", StringComparison.Ordinal)
            ? $"_{method}"
            : method;

    private static void AssertPropertyNames(JsonElement element, params string[] names) =>
        Assert.Equal(names.Order(StringComparer.Ordinal), element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

    private sealed class AcpPeer : IAsyncDisposable
    {
        private readonly Pipe _serverToClient = new();
        private readonly Pipe _clientToServer = new();
        private readonly StreamReader _requestReader;

        public AcpPeer()
        {
            Client = new AcpEngineClient(
                _serverToClient.Reader.AsStream(),
                _clientToServer.Writer.AsStream());
            _requestReader = new StreamReader(
                _clientToServer.Reader.AsStream(),
                Encoding.UTF8,
                leaveOpen: true);
        }

        public AcpEngineClient Client { get; }

        public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(5));

        public async Task<JsonDocument> ReadMessageAsync()
        {
            var line = await _requestReader.ReadLineAsync(Timeout.Token);
            Assert.NotNull(line);
            return JsonDocument.Parse(line);
        }

        public Task RespondAsync(long id, string resultJson) => WriteToClientAsync(
            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{CompactJson(resultJson)}}}\n");

        private static string CompactJson(string json)
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement);
        }

        private async Task WriteToClientAsync(string line)
        {
            await _serverToClient.Writer.WriteAsync(Encoding.UTF8.GetBytes(line), Timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            Timeout.Cancel();
            _requestReader.Dispose();
            await Client.DisposeAsync();
            Timeout.Dispose();
        }
    }
}
