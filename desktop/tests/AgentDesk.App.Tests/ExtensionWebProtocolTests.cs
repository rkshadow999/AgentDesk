using System.Text.Json;
using AgentDesk.App.Bridge;
using AgentDesk.Core.Engine;

namespace AgentDesk.App.Tests;

public sealed class ExtensionWebProtocolTests
{
    [Fact]
    public void PluginAndMarketplacePublisherKeyIdsAreRejectedAsUntrustedClientClaims()
    {
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand("""
            {
              "schemaVersion": 1,
              "type": "extensions/action",
              "requestId": "4c29eb73-349e-4f48-aa24-2d4b35b270a0",
              "workspaceGeneration": 1,
              "sessionId": "session-42",
              "scope": "plugins",
              "action": "install",
              "confirmed": true,
              "payload": {
                "source": "https://github.com/example/review-plugin",
                "publisherKeyId": "publisher-1"
              }
            }
            """));
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand("""
            {
              "schemaVersion": 1,
              "type": "extensions/action",
              "requestId": "d3e2726c-65cc-4d3d-aad3-8c025a4087ed",
              "workspaceGeneration": 1,
              "sessionId": "session-42",
              "scope": "marketplace",
              "action": "update",
              "confirmed": true,
              "payload": {
                "source": "https://example.test/catalog.git",
                "relativePath": "plugins/review",
                "publisherKeyId": "publisher-1"
              }
            }
            """));
    }

    private const string RequestId = "b5a5ef2f-1d3d-4ba8-a8c7-33c5c3f5d0c2";

    [Fact]
    public void ParseList_RequiresVersionedWorkspaceAndPreservesOptionalSession()
    {
        var command = Assert.IsType<ExtensionsListWebCommand>(
            WebMessageProtocol.ParseCommand($$"""
            {
              "schemaVersion": 1,
              "type": "extensions/list",
              "requestId": "{{RequestId}}",
              "workspaceGeneration": 3,
              "sessionId": "session-42",
              "useCache": false
            }
            """));

        Assert.Equal(RequestId, command.RequestId);
        Assert.Equal(3, command.WorkspaceGeneration);
        Assert.Equal("session-42", command.SessionId);
        Assert.False(command.UseCache);
    }

    [Fact]
    public void ParseMcpStdioAction_RequiresEnvironmentReferencesInsteadOfSecrets()
    {
        var command = Assert.IsType<ExtensionsActionWebCommand>(
            WebMessageProtocol.ParseCommand($$"""
            {
              "schemaVersion": 1,
              "type": "extensions/action",
              "requestId": "{{RequestId}}",
              "workspaceGeneration": 3,
              "sessionId": "session-42",
              "scope": "mcp",
              "action": "upsert_stdio",
              "confirmed": true,
              "payload": {
                "serverName": "github",
                "command": "npx",
                "args": ["-y", "server-github"],
                "environment": [{"name": "GITHUB_TOKEN", "sourceVariable": "AGENTDESK_GITHUB_TOKEN"}]
              }
            }
            """));

        Assert.Equal(ExtensionScope.Mcp, command.Scope);
        Assert.Equal("upsert_stdio", command.Action);
        Assert.True(command.Confirmed);
        Assert.Equal("AGENTDESK_GITHUB_TOKEN", command.Payload.GetProperty("environment")[0].GetProperty("sourceVariable").GetString());
    }

    [Fact]
    public void InvalidExtensionCommand_CanProjectOnlyItsRequestIdIntoAnError()
    {
        var json = $$"""
        {
          "schemaVersion": 1,
          "type": "extensions/action",
          "requestId": "{{RequestId}}",
          "workspaceGeneration": 3,
          "sessionId": "session-42",
          "scope": "mcp",
          "action": "upsert_stdio",
          "confirmed": true,
          "payload": {
            "serverName": "server!",
            "command": "npx",
            "secret": "must-not-cross-the-bridge"
          }
        }
        """;
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));

        var error = Assert.IsType<ExtensionsErrorWebEvent>(
            WebMessageProtocol.TryCreateCommandErrorEvent(
                json,
                "The desktop command is invalid."));

        Assert.Equal(RequestId, error.RequestId);
        Assert.Null(error.SessionId);
        Assert.Null(error.Scope);
        Assert.Null(error.Action);
        Assert.Equal("The desktop command is invalid.", error.Message);
        Assert.DoesNotContain("must-not-cross-the-bridge", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown", "toggle")]
    [InlineData("mcp", "toggle")]
    public void ParseAction_RejectsUnknownScopeOrMissingPayloadFields(string scope, string action)
    {
        var json = $$"""
        {
          "schemaVersion": 1,
          "type": "extensions/action",
          "requestId": "{{RequestId}}",
          "workspaceGeneration": 1,
          "sessionId": "session-42",
          "scope": "{{scope}}",
          "action": "{{action}}",
          "confirmed": true,
          "payload": {"unexpected": true}
        }
        """;

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Fact]
    public void ParseMarketplaceAction_RejectsTraversalAndNonHttpsSource()
    {
        var traversal = $$"""
        {
          "schemaVersion": 1,
          "type": "extensions/action",
          "requestId": "{{RequestId}}",
          "workspaceGeneration": 1,
          "sessionId": "session-42",
          "scope": "marketplace",
          "action": "install",
          "confirmed": true,
          "payload": {"source": "http://example.test/catalog.git", "relativePath": "../outside"}
        }
        """;

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(traversal));
    }

    [Fact]
    public void ParseHookActions_AcceptsPathAndSourceMutations()
    {
        var add = Assert.IsType<ExtensionsActionWebCommand>(WebMessageProtocol.ParseCommand($$"""
        {
          "schemaVersion": 1,
          "type": "extensions/action",
          "requestId": "{{RequestId}}",
          "workspaceGeneration": 1,
          "sessionId": "session-42",
          "scope": "hooks",
          "action": "add",
          "confirmed": true,
          "payload": {"path": "C:\\Users\\tester\\.grok\\hooks.json"}
        }
        """));
        Assert.Equal("C:\\Users\\tester\\.grok\\hooks.json", add.Payload.GetProperty("path").GetString());

        var toggleSource = Assert.IsType<ExtensionsActionWebCommand>(WebMessageProtocol.ParseCommand($$"""
        {
          "schemaVersion": 1,
          "type": "extensions/action",
          "requestId": "{{Guid.NewGuid()}}",
          "workspaceGeneration": 1,
          "sessionId": "session-42",
          "scope": "hooks",
          "action": "toggle_source",
          "confirmed": true,
          "payload": {"hookNames": ["safety:pre[0]", "safety:post[0]"], "disableSource": true}
        }
        """));
        Assert.Equal(2, toggleSource.Payload.GetProperty("hookNames").GetArrayLength());
        Assert.True(toggleSource.Payload.GetProperty("disableSource").GetBoolean());
    }

    [Fact]
    public void ParsePluginActions_AcceptsPathInstallAndUpdateMutations()
    {
        var commands = new[]
        {
            (Action: "add", Payload: "{\"path\":\".agentdesk/plugins/review\"}"),
            (Action: "remove", Payload: "{\"path\":\".agentdesk/plugins/review\"}"),
            (Action: "install", Payload: "{\"source\":\"https://github.com/example/review-plugin\"}"),
            (Action: "update", Payload: "{\"pluginId\":\"user/12345678/review\"}"),
            (Action: "update", Payload: "{}"),
        };

        foreach (var (action, payload) in commands)
        {
            var command = Assert.IsType<ExtensionsActionWebCommand>(WebMessageProtocol.ParseCommand($$"""
            {
              "schemaVersion": 1,
              "type": "extensions/action",
              "requestId": "{{Guid.NewGuid()}}",
              "workspaceGeneration": 1,
              "sessionId": "session-42",
              "scope": "plugins",
              "action": "{{action}}",
              "confirmed": true,
              "payload": {{payload}}
            }
            """));
            Assert.Equal(action, command.Action);
        }
    }

    [Fact]
    public void ParseMarketplaceRefresh_AcceptsAllOrOneSafeSource()
    {
        foreach (var payload in new[]
        {
            "{}",
            "{\"source\":\"https://example.test/catalog.git\"}",
            "{\"source\":\"C:\\\\marketplaces\\\\official\"}",
        })
        {
            var command = Assert.IsType<ExtensionsActionWebCommand>(WebMessageProtocol.ParseCommand($$"""
            {
              "schemaVersion": 1,
              "type": "extensions/action",
              "requestId": "{{Guid.NewGuid()}}",
              "workspaceGeneration": 1,
              "sessionId": "session-42",
              "scope": "marketplace",
              "action": "refresh",
              "confirmed": true,
              "payload": {{payload}}
            }
            """));
            Assert.Equal("refresh", command.Action);
        }
    }

    [Fact]
    public void ParseNewExtensionActions_RejectsAmbiguousOrCredentialBearingInputs()
    {
        var invalidPayloads = new[]
        {
            (Scope: "hooks", Action: "toggle_source", Payload: "{\"hookNames\":[],\"disableSource\":true}"),
            (Scope: "hooks", Action: "toggle_source", Payload: "{\"hookNames\":[\"same\",\"same\"],\"disableSource\":true}"),
            (Scope: "plugins", Action: "install", Payload: "{\"source\":\"https://token@example.test/plugin.git\"}"),
            (Scope: "plugins", Action: "install", Payload: "{\"source\":\"prefix-git@evil.example:trusted/repo.git\"}"),
            (Scope: "plugins", Action: "add", Payload: "{\"path\":\"..\\outside\"}"),
            (Scope: "marketplace", Action: "refresh", Payload: "{\"source\":\"file:///C:/catalog\"}"),
        };

        foreach (var (scope, action, payload) in invalidPayloads)
        {
            Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand($$"""
            {
              "schemaVersion": 1,
              "type": "extensions/action",
              "requestId": "{{Guid.NewGuid()}}",
              "workspaceGeneration": 1,
              "sessionId": "session-42",
              "scope": "{{scope}}",
              "action": "{{action}}",
              "confirmed": true,
              "payload": {{payload}}
            }
            """));
        }
    }

    [Theory]
    [InlineData("\\u2028")]
    [InlineData("\\u2029")]
    [InlineData("\\u202E")]
    public void ParseAction_RejectsUnicodeLayoutControlsInApprovalTargets(string escapedCharacter)
    {
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand($$"""
            {
              "schemaVersion": 1,
              "type": "extensions/action",
              "requestId": "{{RequestId}}",
              "workspaceGeneration": 1,
              "sessionId": "session-42",
              "scope": "plugins",
              "action": "add",
              "confirmed": true,
              "payload": {"path":"C:\\plugins\\trusted{{escapedCharacter}}cod.exe"}
            }
            """));
    }

    [Fact]
    public void ParseHttpAction_AllowsHeaderNamesButNeverPlaintextValues()
    {
        var valid = $$"""
        {
          "schemaVersion": 1,
          "type": "extensions/action",
          "requestId": "{{RequestId}}",
          "workspaceGeneration": 1,
          "sessionId": "session-42",
          "scope": "mcp",
          "action": "upsert_http",
          "confirmed": true,
          "payload": {
            "serverName": "gateway",
            "url": "https://example.test/mcp",
            "headers": [{"name": "X-API-Key", "sourceVariable": "AGENTDESK_API_KEY"}]
          }
        }
        """;
        Assert.IsType<ExtensionsActionWebCommand>(WebMessageProtocol.ParseCommand(valid));

        var plaintext = valid.Replace(
            "\"sourceVariable\"",
            "\"value\"",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(plaintext));
    }

    [Fact]
    public void ParseAction_RejectsDuplicateEnvironmentNamesAndUnsafeIds()
    {
        var duplicate = $$"""
        {
          "schemaVersion": 1,
          "type": "extensions/action",
          "requestId": "{{RequestId}}",
          "workspaceGeneration": 1,
          "sessionId": "session-42",
          "scope": "mcp",
          "action": "upsert_stdio",
          "confirmed": true,
          "payload": {
            "serverName": "bad?name",
            "command": "npx",
            "environment": [
              {"name": "TOKEN", "sourceVariable": "TOKEN_ONE"},
              {"name": "TOKEN", "sourceVariable": "TOKEN_TWO"}
            ]
          }
        }
        """;
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(duplicate));
    }

    [Fact]
    public void SerializeCatalog_DoesNotProjectSecretsOrArbitraryMetadata()
    {
        var catalog = new ExtensionsCatalogWebEvent(
            RequestId,
            "session-42",
            [new McpServerCatalogItem(
                "github",
                "GitHub",
                McpServerSource.Local,
                "config",
                McpServerTransportKind.Http,
                "https://example.test/mcp?token=must-not-leak",
                null,
                null,
                null,
                null,
                [],
                ["GITHUB_TOKEN"],
                null)],
            [new SkillDescriptor(
                "review", null, "Review", false, [], null, null, null, null, null, null,
                new Dictionary<string, string> { ["secret"] = "must-not-leak" },
                "C:\\repo\\SKILL.md", SkillScope.Repo, null, null, [], null, null, true, false, true)],
            null,
            new HookCatalog(
                [new HookDescriptor(
                    "hook", HookEvent.PreToolUse, HookHandlerType.Http, null,
                    "pwsh -File secret.ps1", "https://example.test/hook?token=must-not-leak",
                    TimeSpan.FromSeconds(1), "C:\\hooks", false)],
                true,
                []),
            [],
            new MarketplaceCatalog([]));

        var json = WebMessageProtocol.SerializeEvent(catalog);

        Assert.DoesNotContain("must-not-leak", json, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata", json, StringComparison.Ordinal);
        Assert.Contains("GITHUB_TOKEN", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeActionCompleted_UsesStableSnakeCaseNames()
    {
        var json = WebMessageProtocol.SerializeEvent(
            new ExtensionsActionCompletedWebEvent(
                RequestId,
                "session-42",
                ExtensionScope.Marketplace,
                "install",
                new ExtensionActionOutcome(
                    ExtensionActionStatus.ConfirmationRequired,
                    "confirmation required",
                    RequiresReload: false,
                    RequiresRestart: false)));

        using var document = JsonDocument.Parse(json);
        Assert.Equal("extensions/action/completed", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("marketplace", document.RootElement.GetProperty("scope").GetString());
        Assert.Equal("confirmation_required", document.RootElement.GetProperty("status").GetString());
    }
}
