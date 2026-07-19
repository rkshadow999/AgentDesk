using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using AgentDesk.Core.Engine;
using AgentDesk.Engine.Acp;
using AgentDesk.Engine.Transport;

namespace AgentDesk.Engine.Tests;

public sealed class AcpEngineClientTests
{
    [Fact]
    public async Task ListBackgroundTasksAsync_MapsTheUpstreamTaskSnapshotShape()
    {
        await using var peer = new AcpPeer();

        var listTask = peer.Client.ListBackgroundTasksAsync(
            new SessionId("session-42"),
            peer.Timeout.Token);
        using var request = await peer.ReadMessageAsync();

        AssertRequest(request.RootElement, 1, "x.ai/task/list");
        Assert.Equal(
            "session-42",
            request.RootElement.GetProperty("params").GetProperty("sessionId").GetString());
        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "tasks": [
                  {
                    "task_id": "task-7",
                    "command": "internal wrapper",
                    "display_command": "dotnet test",
                    "cwd": "C:\\repo",
                    "start_time": {
                      "secs_since_epoch": 1735689600,
                      "nanos_since_epoch": 250000000
                    },
                    "end_time": null,
                    "output": "running",
                    "output_file": "C:\\temp\\task-7.log",
                    "truncated": false,
                    "exit_code": null,
                    "signal": null,
                    "completed": false,
                    "kind": "bash",
                    "block_waited": false,
                    "explicitly_killed": false,
                    "owner_session_id": "session-42"
                  }
                ]
              }
            }
            """);

        var item = Assert.Single(await listTask);
        Assert.Equal("task-7", item.TaskId);
        Assert.Equal("dotnet test", item.UserFacingCommand);
        Assert.Equal("C:\\repo", item.WorkingDirectory);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1735689600).AddTicks(2_500_000),
            item.StartedAt);
        Assert.Equal(BackgroundTaskKind.Bash, item.Kind);
        Assert.False(item.Completed);
        Assert.Equal("session-42", item.OwnerSessionId);
    }

    [Fact]
    public async Task ListBackgroundTasksAsync_RejectsAWhitespaceTaskIdAsUntrustedData()
    {
        await using var peer = new AcpPeer();

        var listTask = peer.Client.ListBackgroundTasksAsync(
            new SessionId("session-42"),
            peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "tasks": [{
                  "task_id": " ",
                  "command": "dotnet test",
                  "cwd": "C:\\repo",
                  "start_time": { "secs_since_epoch": 1735689600, "nanos_since_epoch": 0 },
                  "end_time": null,
                  "output": "",
                  "output_file": "C:\\temp\\task.log",
                  "truncated": false,
                  "exit_code": null,
                  "signal": null,
                  "completed": false,
                  "kind": "bash",
                  "explicitly_killed": false
                }]
              }
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => listTask);
    }

    [Theory]
    [InlineData("killed", BackgroundTaskKillOutcome.Killed)]
    [InlineData("already_exited", BackgroundTaskKillOutcome.AlreadyExited)]
    [InlineData("not_found", BackgroundTaskKillOutcome.NotFound)]
    public async Task KillBackgroundTaskAsync_MapsTheTypedOutcome(
        string wireOutcome,
        BackgroundTaskKillOutcome expected)
    {
        await using var peer = new AcpPeer();

        var killTask = peer.Client.KillBackgroundTaskAsync(
            new SessionId("session-42"),
            "task-7",
            peer.Timeout.Token);
        using var request = await peer.ReadMessageAsync();

        AssertRequest(request.RootElement, 1, "x.ai/task/kill");
        var parameters = request.RootElement.GetProperty("params");
        Assert.Equal("session-42", parameters.GetProperty("sessionId").GetString());
        Assert.Equal("task-7", parameters.GetProperty("taskId").GetString());
        await peer.RespondAsync(
            1,
            $$"""{ "result": { "taskId": "task-7", "outcome": "{{wireOutcome}}" } }""");

        Assert.Equal(expected, await killTask);
    }

    [Fact]
    public async Task ListRunningSubagentsAsync_MapsLiveProgress()
    {
        await using var peer = new AcpPeer();

        var listTask = peer.Client.ListRunningSubagentsAsync(
            new SessionId("session-42"),
            peer.Timeout.Token);
        using var request = await peer.ReadMessageAsync();

        AssertRequest(request.RootElement, 1, "x.ai/subagent/list_running");
        Assert.Equal(
            "session-42",
            request.RootElement.GetProperty("params").GetProperty("sessionId").GetString());
        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "sessionId": "session-42",
                "subagents": [
                  {
                    "subagentId": "subagent-7",
                    "parentSessionId": "session-42",
                    "childSessionId": "session-child",
                    "subagentType": "worker",
                    "description": "Run the desktop tests",
                    "startedAtEpochMs": 1735689600250,
                    "durationMs": 1250,
                    "turnCount": 2,
                    "toolCallCount": 4,
                    "tokensUsed": 8192,
                    "contextWindowTokens": 131072,
                    "contextUsagePct": 6,
                    "toolsUsed": ["shell_command", "apply_patch"],
                    "errorCount": 1
                  }
                ]
              }
            }
            """);

        var item = Assert.Single(await listTask);
        Assert.Equal("subagent-7", item.SubagentId);
        Assert.Equal(SubagentStatus.Running, item.Status);
        Assert.Equal(2, item.TurnCount);
        Assert.Equal(8192UL, item.TokensUsed);
        Assert.Equal(["shell_command", "apply_patch"], item.ToolsUsed);
    }

    [Fact]
    public async Task ListRunningSubagentsAsync_RejectsAWhitespaceIdAsUntrustedData()
    {
        await using var peer = new AcpPeer();

        var listTask = peer.Client.ListRunningSubagentsAsync(
            new SessionId("session-42"),
            peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "subagents": [{
                  "subagentId": " ",
                  "parentSessionId": "session-42",
                  "childSessionId": "session-child",
                  "subagentType": "worker",
                  "description": "Run tests",
                  "startedAtEpochMs": 1735689600000,
                  "durationMs": 1000,
                  "turnCount": 1,
                  "toolCallCount": 1,
                  "tokensUsed": 1,
                  "contextWindowTokens": 100,
                  "contextUsagePct": 1,
                  "toolsUsed": [],
                  "errorCount": 0
                }]
              }
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => listTask);
    }

    [Fact]
    public async Task ListRunningSubagentsAsync_RejectsAResponseBoundToAnotherSession()
    {
        await using var peer = new AcpPeer();

        var listTask = peer.Client.ListRunningSubagentsAsync(
            new SessionId("session-42"),
            peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "sessionId": "other-session",
                "subagents": []
              }
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => listTask);
    }

    [Fact]
    public async Task GetSubagentAsync_MapsACompletedSnapshot()
    {
        await using var peer = new AcpPeer();

        var getTask = peer.Client.GetSubagentAsync(
            new SessionId("session-42"),
            "subagent-7",
            block: false,
            timeout: null,
            peer.Timeout.Token);
        using var request = await peer.ReadMessageAsync();

        AssertRequest(request.RootElement, 1, "x.ai/subagent/get");
        var parameters = request.RootElement.GetProperty("params");
        Assert.Equal("subagent-7", parameters.GetProperty("subagentId").GetString());
        Assert.False(parameters.GetProperty("block").GetBoolean());
        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "sessionId": "session-42",
                "snapshot": {
                  "subagentId": "subagent-7",
                  "parentSessionId": "session-42",
                  "childSessionId": "session-child",
                  "subagentType": "worker",
                  "description": "Run the desktop tests",
                  "startedAtEpochMs": 1735689600250,
                  "durationMs": 2500,
                  "status": "completed",
                  "output": "All tests passed",
                  "toolCalls": 5,
                  "turns": 3,
                  "worktreePath": "C:\\repo\\worktree",
                  "forkParentPromptId": "prompt-5"
                }
              }
            }
            """);

        var snapshot = Assert.IsType<SubagentSnapshot>(await getTask);
        Assert.Equal(SubagentStatus.Completed, snapshot.Status);
        Assert.Equal("All tests passed", snapshot.Output);
        Assert.Equal(5, snapshot.ToolCallCount);
        Assert.Equal(3, snapshot.TurnCount);
        Assert.Equal("C:\\repo\\worktree", snapshot.WorktreePath);
        Assert.Equal("prompt-5", snapshot.ForkParentPromptId);
        Assert.True(snapshot.IsTerminal);
    }

    [Fact]
    public async Task GetSubagentAsync_RejectsADetailFromAnotherParentSession()
    {
        await using var peer = new AcpPeer();

        var getTask = peer.Client.GetSubagentAsync(
            new SessionId("session-42"),
            "subagent-7",
            cancellationToken: peer.Timeout.Token);
        using var request = await peer.ReadMessageAsync();
        Assert.Equal(
            "session-42",
            request.RootElement.GetProperty("params").GetProperty("sessionId").GetString());
        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "sessionId": "session-42",
                "snapshot": {
                  "subagentId": "subagent-7",
                  "parentSessionId": "other-session",
                  "childSessionId": "session-child",
                  "subagentType": "worker",
                  "description": "Run tests",
                  "startedAtEpochMs": 1735689600000,
                  "durationMs": 1000,
                  "status": "running",
                  "turnCount": 1,
                  "toolCallCount": 1,
                  "tokensUsed": 1,
                  "contextWindowTokens": 100,
                  "contextUsagePct": 1,
                  "toolsUsed": [],
                  "errorCount": 0
                }
              }
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => getTask);
    }

    [Theory]
    [InlineData("cancelled", SubagentCancelOutcome.Cancelled, null)]
    [InlineData("not_found", SubagentCancelOutcome.NotFound, null)]
    [InlineData("already_finished", SubagentCancelOutcome.AlreadyFinished, SubagentStatus.Completed)]
    public async Task CancelSubagentAsync_MapsTheTypedOutcome(
        string kind,
        SubagentCancelOutcome expected,
        SubagentStatus? terminalStatus)
    {
        await using var peer = new AcpPeer();

        var cancelTask = peer.Client.CancelSubagentAsync(
            new SessionId("session-42"),
            "subagent-7",
            peer.Timeout.Token);
        using var request = await peer.ReadMessageAsync();

        AssertRequest(request.RootElement, 1, "x.ai/subagent/cancel");
        Assert.Equal(
            "subagent-7",
            request.RootElement.GetProperty("params").GetProperty("subagentId").GetString());
        var statusProperty = terminalStatus is null ? string.Empty : ", \"status\": \"completed\"";
        await peer.RespondAsync(
            1,
            $$"""
            {
              "result": {
                "sessionId": "session-42",
                "subagentId": "subagent-7",
                "cancelled": {{(kind == "cancelled" ? "true" : "false")}},
                "outcome": { "kind": "{{kind}}"{{statusProperty}} }
              }
            }
            """);

        var result = await cancelTask;
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(terminalStatus, result.TerminalStatus);
    }

    [Fact]
    public async Task CancelSubagentAsync_RejectsAlreadyFinishedWithANonTerminalStatus()
    {
        await using var peer = new AcpPeer();

        var cancelTask = peer.Client.CancelSubagentAsync(
            new SessionId("session-42"),
            "subagent-7",
            peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "sessionId": "session-42",
                "subagentId": "subagent-7",
                "cancelled": false,
                "outcome": { "kind": "already_finished", "status": "running" }
              }
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => cancelTask);
    }

    [Fact]
    public async Task CancelSubagentAsync_RejectsAResponseBoundToAnotherSession()
    {
        await using var peer = new AcpPeer();

        var cancelTask = peer.Client.CancelSubagentAsync(
            new SessionId("session-42"),
            "subagent-7",
            peer.Timeout.Token);
        using var request = await peer.ReadMessageAsync();
        Assert.Equal(
            "session-42",
            request.RootElement.GetProperty("params").GetProperty("sessionId").GetString());
        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "sessionId": "other-session",
                "subagentId": "subagent-7",
                "cancelled": true,
                "outcome": { "kind": "cancelled" }
              }
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => cancelTask);
    }

    [Fact]
    public async Task Faulted_ForwardsTransportEndOfStream()
    {
        await using var peer = new AcpPeer();
        var faulted = new TaskCompletionSource<EngineFaultedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Client.Faulted += (_, args) => faulted.TrySetResult(args);

        await peer.CompleteOutputAsync();

        var fault = await faulted.Task.WaitAsync(peer.Timeout.Token);
        Assert.IsType<EndOfStreamException>(fault.Exception);
    }

    [Fact]
    public async Task InitializeAsync_WithDesktopCredentialSeedsItBeforeTheAcpHandshake()
    {
        await using var peer = new AcpPeer("xai-pipe-secret");

        var initializeTask = peer.Client.InitializeAsync(peer.Timeout.Token);
        using var credential = await peer.ReadMessageAsync();

        AssertRequest(credential.RootElement, 1, "agentdesk/v1/credential");
        var credentialParameters = credential.RootElement.GetProperty("params");
        Assert.Equal(1, credentialParameters.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("xai-pipe-secret", credentialParameters.GetProperty("apiKey").GetString());
        await peer.RespondAsync(
            1,
            "{ \"credentialAccepted\": true, \"authMethodId\": \"xai.api_key\" }");

        using var initialize = await peer.ReadMessageAsync();
        AssertRequest(initialize.RootElement, 2, "initialize");
        Assert.DoesNotContain("xai-pipe-secret", initialize.RootElement.GetRawText(), StringComparison.Ordinal);
        await peer.RespondAsync(
            2,
            """
            {
              "protocolVersion": 1,
              "agentCapabilities": {},
              "authMethods": [
                { "type": "agent", "id": "xai.api_key", "name": "xai.api_key" }
              ]
            }
            """);

        using var extensionInitialize = await peer.ReadMessageAsync();
        AssertRequest(extensionInitialize.RootElement, 3, "agentdesk/v1/initialize");
        await peer.RespondWithErrorAsync(3, -32601, "Method not found");

        var capabilities = await initializeTask;
        Assert.Equal(1, capabilities.ProtocolVersion);
    }

    [Fact]
    public async Task InitializeAsync_WhenDesktopCredentialBridgeIsMissingFailsClosed()
    {
        await using var peer = new AcpPeer("xai-pipe-secret");

        var initializeTask = peer.Client.InitializeAsync(peer.Timeout.Token);
        using var credential = await peer.ReadMessageAsync();
        AssertRequest(credential.RootElement, 1, "agentdesk/v1/credential");
        await peer.RespondWithErrorAsync(1, -32601, "Method not found");

        var exception = await Assert.ThrowsAsync<JsonRpcException>(() => initializeTask);
        Assert.Equal(-32601, exception.Code);
    }

    [Fact]
    public async Task InitializeAsync_SendsRealAcpHandshakeAndDowngradesWhenExtensionsAreMissing()
    {
        await using var peer = new AcpPeer();

        var initializeTask = peer.Client.InitializeAsync(peer.Timeout.Token);
        using var initialize = await peer.ReadMessageAsync();

        AssertRequest(initialize.RootElement, 1, "initialize");
        var parameters = initialize.RootElement.GetProperty("params");
        Assert.Equal(1, parameters.GetProperty("protocolVersion").GetInt32());
        var clientCapabilities = parameters.GetProperty("clientCapabilities");
        Assert.False(clientCapabilities.GetProperty("fs").GetProperty("readTextFile").GetBoolean());
        Assert.False(clientCapabilities.GetProperty("fs").GetProperty("writeTextFile").GetBoolean());
        Assert.False(clientCapabilities.GetProperty("terminal").GetBoolean());
        Assert.True(clientCapabilities.GetProperty("_meta")
            .GetProperty("x.ai/incrementalBashOutput")
            .GetBoolean());
        Assert.Equal("agentdesk", parameters.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.True(parameters.GetProperty("_meta").GetProperty("startupHints").GetProperty("nonInteractive").GetBoolean());
        Assert.True(parameters.GetProperty("_meta").GetProperty("startupHints").GetProperty("skipGitStatus").GetBoolean());
        Assert.True(parameters.GetProperty("_meta").GetProperty("startupHints").GetProperty("skipProjectLayout").GetBoolean());
        Assert.Equal("agentdesk", parameters.GetProperty("_meta").GetProperty("clientIdentifier").GetString());

        await peer.RespondAsync(
            1,
            """
            {
              "protocolVersion": 1,
              "agentCapabilities": {
                "loadSession": true,
                "promptCapabilities": {
                  "image": true,
                  "audio": false,
                  "embeddedContext": true
                }
              },
              "authMethods": [
                { "type": "agent", "id": "xai.api_key", "name": "xai.api_key" }
              ]
            }
            """);

        using var extensionInitialize = await peer.ReadMessageAsync();
        AssertRequest(extensionInitialize.RootElement, 2, "agentdesk/v1/initialize");
        Assert.Equal(1, extensionInitialize.RootElement.GetProperty("params").GetProperty("protocolVersion").GetInt32());
        await peer.RespondWithErrorAsync(2, -32601, "Method not found");

        var capabilities = await initializeTask;

        Assert.Equal(1, capabilities.ProtocolVersion);
        Assert.True(capabilities.LoadSession);
        Assert.True(capabilities.ImagePrompts);
        Assert.False(capabilities.AudioPrompts);
        Assert.True(capabilities.EmbeddedContextPrompts);
        Assert.False(capabilities.AgentDeskExtensions);
        Assert.False(capabilities.AgentDeskHealth);
        Assert.Equal(capabilities, peer.Client.Capabilities);
    }

    [Fact]
    public async Task InitializeAsync_ProbesHealthAndMapsVersionedExtensionCapabilities()
    {
        await using var peer = new AcpPeer();

        var initializeTask = peer.Client.InitializeAsync(peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            """
            {
              "protocolVersion": 1,
              "agentCapabilities": {},
              "authMethods": [
                { "type": "agent", "id": "xai.api_key", "name": "xai.api_key" }
              ]
            }
            """);

        using var extensionInitialize = await peer.ReadMessageAsync();
        AssertRequest(extensionInitialize.RootElement, 2, "agentdesk/v1/initialize");
        await peer.RespondAsync(2, "{ \"protocolVersion\": 1 }");

        using var health = await peer.ReadMessageAsync();
        AssertRequest(health.RootElement, 3, "agentdesk/v1/health");
        await peer.RespondAsync(
            3,
            """
            {
              "status": "ok",
              "sandbox": {
                "configuredProfile": "strict",
                "active": true,
                "activeProfile": "strict",
                "childNetworkRestricted": true,
                "enforcementRequired": true
              }
            }
            """);

        var capabilities = await initializeTask;

        Assert.True(capabilities.AgentDeskExtensions);
        Assert.True(capabilities.AgentDeskHealth);
        Assert.True(capabilities.StrictSandboxActive);
    }

    [Fact]
    public async Task InitializeAsync_MapsVersionedMemoryCapabilities()
    {
        await using var peer = new AcpPeer();

        var initializeTask = peer.Client.InitializeAsync(peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            """
            {
              "protocolVersion": 1,
              "agentCapabilities": {},
              "authMethods": [
                { "type": "agent", "id": "xai.api_key", "name": "xai.api_key" }
              ]
            }
            """);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            2,
            """
            {
              "protocolVersion": 1,
              "memory": {
                "schemaVersion": 1,
                "list": true,
                "read": true,
                "write": true,
                "delete": true,
                "mutationConfirmationRequired": true
              }
            }
            """);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            3,
            """
            {
              "status": "ok",
              "sandbox": {
                "configuredProfile": "off",
                "active": false,
                "activeProfile": null,
                "childNetworkRestricted": false,
                "enforcementRequired": false
              }
            }
            """);

        var capabilities = await initializeTask;

        Assert.Equal(1, capabilities.Memory.SchemaVersion);
        Assert.True(capabilities.Memory.List);
        Assert.True(capabilities.Memory.Read);
        Assert.True(capabilities.Memory.Write);
        Assert.True(capabilities.Memory.Delete);
        Assert.True(capabilities.Memory.MutationConfirmationRequired);
    }

    [Fact]
    public async Task InitializeAsync_RejectsMalformedMemoryCapabilities()
    {
        await using var peer = new AcpPeer();
        var initializeTask = peer.Client.InitializeAsync(peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            """
            {
              "protocolVersion": 1,
              "agentCapabilities": {},
              "authMethods": [
                { "type": "agent", "id": "xai.api_key", "name": "xai.api_key" }
              ]
            }
            """);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            2,
            """
            {
              "protocolVersion": 1,
              "memory": {
                "schemaVersion": 1,
                "list": true,
                "read": true,
                "write": true,
                "delete": "yes",
                "mutationConfirmationRequired": true
              }
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => initializeTask);
    }

    [Fact]
    public async Task InitializeAsync_MapsVersionedPlanModeCapability()
    {
        await using var peer = new AcpPeer();
        var initializeTask = peer.Client.InitializeAsync(peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            """
            {
              "protocolVersion": 1,
              "agentCapabilities": {},
              "authMethods": [
                { "type": "agent", "id": "xai.api_key", "name": "xai.api_key" }
              ]
            }
            """);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            2,
            "{ \"protocolVersion\": 1, \"sessionModes\": [\"default\", \"plan\", \"future\"] }");
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            3,
            """
            {
              "status": "ok",
              "sandbox": {
                "configuredProfile": "off",
                "active": false,
                "activeProfile": null,
                "childNetworkRestricted": false,
                "enforcementRequired": false
              }
            }
            """);

        var capabilities = await initializeTask;

        Assert.True(capabilities.Supports(SessionMode.Default));
        Assert.True(capabilities.Supports(SessionMode.Plan));
        Assert.Equal([SessionMode.Default, SessionMode.Plan], capabilities.SessionModes);
    }

    [Fact]
    public async Task InitializeAsync_RejectsMalformedSessionModeCapabilities()
    {
        await using var peer = new AcpPeer();
        var initializeTask = peer.Client.InitializeAsync(peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            """
            {
              "protocolVersion": 1,
              "agentCapabilities": {},
              "authMethods": [
                { "type": "agent", "id": "xai.api_key", "name": "xai.api_key" }
              ]
            }
            """);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            2,
            "{ \"protocolVersion\": 1, \"sessionModes\": \"plan\" }");

        await Assert.ThrowsAsync<InvalidDataException>(() => initializeTask);
    }

    [Fact]
    public async Task SetSessionModeAsync_UsesTheCanonicalAcpRequestShape()
    {
        await using var peer = new AcpPeer();
        var requestTask = peer.Client.SetSessionModeAsync(
            new SessionId("session-42"),
            SessionMode.Plan,
            peer.Timeout.Token);

        using var request = await peer.ReadMessageAsync();
        AssertRequest(request.RootElement, 1, "session/set_mode");
        Assert.Equal(
            "session-42",
            request.RootElement.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal(
            "plan",
            request.RootElement.GetProperty("params").GetProperty("modeId").GetString());
        await peer.RespondAsync(1, "{}");
        await requestTask;
    }

    [Fact]
    public async Task ListRuntimeCommandsAsync_MapsTheActualCommandAndSkillMetadataShape()
    {
        await using var peer = new AcpPeer();
        var listTask = peer.Client.ListRuntimeCommandsAsync(
            "C:\\repo",
            peer.Timeout.Token);

        using var request = await peer.ReadMessageAsync();
        AssertRequest(request.RootElement, 1, "x.ai/commands/list");
        Assert.Equal(
            "C:\\repo",
            request.RootElement.GetProperty("params").GetProperty("cwd").GetString());
        await peer.RespondAsync(
            1,
            """
            {
              "commands": [
                {
                  "name": "memory",
                  "description": "Browse, view, and manage your memories",
                  "input": { "hint": "on|off" }
                },
                {
                  "name": "hooks-list",
                  "description": "Show hooks loaded in this session",
                  "input": null
                },
                {
                  "name": "plugins",
                  "description": "Manage plugins",
                  "input": { "hint": "list | reload" },
                  "_meta": { "futureExtension": true }
                },
                {
                  "name": "plugin:mcp-tools",
                  "description": "Inspect MCP tools exposed by a plugin",
                  "input": { "hint": "server name" },
                  "_meta": {
                    "scope": "plugin",
                    "path": "C:\\plugins\\mcp-tools\\SKILL.md",
                    "futureExtension": { "enabled": true }
                  }
                }
              ]
            }
            """);

        var commands = await listTask;

        Assert.Collection(
            commands,
            command =>
            {
                Assert.Equal("memory", command.Name);
                Assert.Equal("Browse, view, and manage your memories", command.Description);
                Assert.Equal("on|off", command.Input?.Hint);
                Assert.Null(command.Skill);
            },
            command =>
            {
                Assert.Equal("hooks-list", command.Name);
                Assert.Null(command.Input);
                Assert.Null(command.Skill);
            },
            command =>
            {
                Assert.Equal("plugins", command.Name);
                Assert.Equal("list | reload", command.Input?.Hint);
                Assert.Null(command.Skill);
            },
            command =>
            {
                Assert.Equal("plugin:mcp-tools", command.Name);
                Assert.Equal("server name", command.Input?.Hint);
                Assert.Equal(RuntimeSkillScope.Plugin, command.Skill?.Scope);
                Assert.Equal("C:\\plugins\\mcp-tools\\SKILL.md", command.Skill?.Path);
            });
    }

    [Fact]
    public async Task ListRuntimeCommandsAsync_UsesNullCwdForThePreSessionCatalog()
    {
        await using var peer = new AcpPeer();
        var listTask = peer.Client.ListRuntimeCommandsAsync(null, peer.Timeout.Token);

        using var request = await peer.ReadMessageAsync();
        AssertRequest(request.RootElement, 1, "x.ai/commands/list");
        Assert.Equal(
            JsonValueKind.Null,
            request.RootElement.GetProperty("params").GetProperty("cwd").ValueKind);
        await peer.RespondAsync(1, "{ \"commands\": [] }");

        Assert.Empty(await listTask);
    }

    public static TheoryData<string> MalformedRuntimeCommandResponses => new()
    {
        "{}",
        "{ \"commands\": {} }",
        "{ \"commands\": [42] }",
        "{ \"commands\": [{ \"description\": \"Missing name\", \"input\": null }] }",
        "{ \"commands\": [{ \"name\": \"memory\", \"description\": 42, \"input\": null }] }",
        "{ \"commands\": [{ \"name\": \"memory\", \"description\": \"Memory\", \"input\": {} }] }",
        "{ \"commands\": [{ \"name\": \"skill\", \"description\": \"Skill\", \"input\": null, \"_meta\": { \"scope\": \"user\" } }] }",
        "{ \"commands\": [{ \"name\": \"skill\", \"description\": \"Skill\", \"input\": null, \"_meta\": { \"scope\": \"unknown\", \"path\": \"C:\\\\skill\\\\SKILL.md\" } }] }",
    };

    [Theory]
    [MemberData(nameof(MalformedRuntimeCommandResponses))]
    public async Task ListRuntimeCommandsAsync_RejectsMalformedResponses(string responseJson)
    {
        await using var peer = new AcpPeer();
        var listTask = peer.Client.ListRuntimeCommandsAsync("C:\\repo", peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(1, responseJson);

        await Assert.ThrowsAsync<InvalidDataException>(() => listTask);
    }

    [Fact]
    public async Task ListRuntimeCommandsAsync_RejectsAnOversizedCatalog()
    {
        await using var peer = new AcpPeer();
        var listTask = peer.Client.ListRuntimeCommandsAsync("C:\\repo", peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        var commands = Enumerable.Range(0, 4097).Select(index => new
        {
            name = $"command-{index}",
            description = "command",
            input = (object?)null,
        });
        await peer.RespondAsync(1, JsonSerializer.Serialize(new { commands }));

        await Assert.ThrowsAsync<InvalidDataException>(() => listTask);
    }

    [Fact]
    public async Task ListRuntimeCommandsAsync_RejectsDuplicateCommandNames()
    {
        await using var peer = new AcpPeer();
        var listTask = peer.Client.ListRuntimeCommandsAsync("C:\\repo", peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            """
            {
              "commands": [
                { "name": "plugins", "description": "First", "input": null },
                { "name": "plugins", "description": "Second", "input": null }
              ]
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => listTask);
    }

    [Fact]
    public async Task ListRuntimeCommandsAsync_MapsEveryUpstreamSkillScope()
    {
        await using var peer = new AcpPeer();
        var listTask = peer.Client.ListRuntimeCommandsAsync("C:\\repo", peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            """
            {
              "commands": [
                { "name": "local-skill", "description": "Local", "input": null, "_meta": { "scope": "local", "path": "C:\\local\\SKILL.md" } },
                { "name": "repo-skill", "description": "Repo", "input": null, "_meta": { "scope": "repo", "path": "C:\\repo\\SKILL.md" } },
                { "name": "user-skill", "description": "User", "input": null, "_meta": { "scope": "user", "path": "C:\\user\\SKILL.md" } },
                { "name": "plugin:skill", "description": "Plugin", "input": null, "_meta": { "scope": "plugin", "path": "C:\\plugin\\SKILL.md" } }
              ]
            }
            """);

        var commands = await listTask;

        Assert.Equal(
            [
                RuntimeSkillScope.Local,
                RuntimeSkillScope.Repo,
                RuntimeSkillScope.User,
                RuntimeSkillScope.Plugin,
            ],
            commands.Select(command => command.Skill!.Scope));
    }

    [Fact]
    public async Task ListRuntimeCommandsAsync_RejectsControlCharactersInDisplayMetadata()
    {
        await using var peer = new AcpPeer();
        var listTask = peer.Client.ListRuntimeCommandsAsync("C:\\repo", peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            JsonSerializer.Serialize(new
            {
                commands = new[]
                {
                    new
                    {
                        name = "memory",
                        description = "Memory\0metadata",
                        input = new { hint = "on|off" },
                    },
                },
            }));

        await Assert.ThrowsAsync<InvalidDataException>(() => listTask);
    }

    [Fact]
    public async Task ListRuntimeCommandsAsync_RejectsOversizedCommandMetadata()
    {
        await using var peer = new AcpPeer();
        var listTask = peer.Client.ListRuntimeCommandsAsync("C:\\repo", peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            JsonSerializer.Serialize(new
            {
                commands = new[]
                {
                    new
                    {
                        name = new string('n', 257),
                        description = "command",
                        input = (object?)null,
                    },
                },
            }));

        await Assert.ThrowsAsync<InvalidDataException>(() => listTask);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("C:\\repo\nother")]
    public async Task ListRuntimeCommandsAsync_RejectsInvalidWorkingDirectories(string cwd)
    {
        await using var peer = new AcpPeer();

        await Assert.ThrowsAsync<ArgumentException>(
            () => peer.Client.ListRuntimeCommandsAsync(cwd, peer.Timeout.Token));
    }

    [Fact]
    public async Task ListRuntimeCommandsAsync_RejectsAnOversizedWorkingDirectory()
    {
        await using var peer = new AcpPeer();

        await Assert.ThrowsAsync<ArgumentException>(
            () => peer.Client.ListRuntimeCommandsAsync(
                new string('a', 32768),
                peer.Timeout.Token));
    }

    [Fact]
    public async Task FlushMemoryAsync_UsesTheActualExtensionRequestShape()
    {
        await using var peer = new AcpPeer();
        var flushTask = peer.Client.FlushMemoryAsync(
            new SessionId("session-42"),
            peer.Timeout.Token);

        using var request = await peer.ReadMessageAsync();
        AssertRequest(request.RootElement, 1, "x.ai/memory/flush");
        Assert.Equal(
            "session-42",
            request.RootElement.GetProperty("params").GetProperty("session_id").GetString());
        await peer.RespondAsync(1, "{}");

        await flushTask;
    }

    [Fact]
    public async Task FlushMemoryAsync_RejectsAnOversizedSessionId()
    {
        await using var peer = new AcpPeer();

        await Assert.ThrowsAsync<ArgumentException>(
            () => peer.Client.FlushMemoryAsync(
                new SessionId(new string('s', 513)),
                peer.Timeout.Token));
    }

    [Fact]
    public async Task FlushMemoryAsync_RejectsControlCharactersInTheSessionId()
    {
        await using var peer = new AcpPeer();

        await Assert.ThrowsAsync<ArgumentException>(
            () => peer.Client.FlushMemoryAsync(
                new SessionId("session-42\nother"),
                peer.Timeout.Token));
    }

    [Fact]
    public async Task ListSessionsAsync_UsesTheUnifiedSessionCatalogAndMapsAStablePage()
    {
        await using var peer = new AcpPeer();
        var listTask = peer.Client.ListSessionsAsync(
            "C:\\repo",
            "parser",
            "cursor-1",
            50,
            peer.Timeout.Token);

        using var request = await peer.ReadMessageAsync();
        AssertRequest(request.RootElement, 1, "x.ai/session/list");
        var parameters = request.RootElement.GetProperty("params");
        Assert.Equal("C:\\repo", parameters.GetProperty("cwd").GetString());
        Assert.Equal("parser", parameters.GetProperty("query").GetString());
        Assert.Equal("cursor-1", parameters.GetProperty("cursor").GetString());
        Assert.Equal(50, parameters.GetProperty("limit").GetInt32());
        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "sessions": [
                  {
                    "sessionId": "session-42",
                    "title": "Fix the parser",
                    "summary": "fallback",
                    "cwd": "C:\\repo",
                    "createdAt": "2026-07-16T08:00:00Z",
                    "updatedAt": "2026-07-16T09:30:00Z",
                    "numMessages": 12,
                    "modelId": "grok-4.5",
                    "branch": "feature/parser",
                    "worktreeLabel": "parser",
                    "sourceWorkspaceDir": "C:\\repo-main"
                  }
                ],
                "nextCursor": "cursor-2",
                "_meta": {}
              }
            }
            """);

        var page = await listTask;

        var session = Assert.Single(page.Sessions);
        Assert.Equal("session-42", session.SessionId.Value);
        Assert.Equal("Fix the parser", session.Title);
        Assert.Equal("C:\\repo", session.WorkspacePath);
        Assert.Equal(DateTimeOffset.Parse("2026-07-16T08:00:00Z"), session.CreatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-07-16T09:30:00Z"), session.UpdatedAt);
        Assert.Equal(12, session.MessageCount);
        Assert.Equal("grok-4.5", session.ModelId);
        Assert.Equal("feature/parser", session.Branch);
        Assert.Equal("parser", session.WorktreeLabel);
        Assert.Equal("C:\\repo-main", session.SourceWorkspacePath);
        Assert.Equal("cursor-2", page.NextCursor);
    }

    [Fact]
    public async Task ListSessionsAsync_RejectsMalformedCatalogRows()
    {
        await using var peer = new AcpPeer();
        var listTask = peer.Client.ListSessionsAsync(
            workingDirectory: null,
            query: null,
            cursor: null,
            limit: 30,
            peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "sessions": [
                  {
                    "sessionId": "session-42",
                    "title": "Broken",
                    "cwd": "C:\\repo",
                    "createdAt": "not-a-date",
                    "updatedAt": "2026-07-16T09:30:00Z",
                    "numMessages": 1
                  }
                ]
              }
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => listTask);
    }

    [Fact]
    public async Task RenameSessionAsync_UsesTheSessionAdminExtensionShape()
    {
        await using var peer = new AcpPeer();
        var renameTask = peer.Client.RenameSessionAsync(
            new SessionId("session-42"),
            "Parser repaired",
            "C:\\repo",
            peer.Timeout.Token);

        using var request = await peer.ReadMessageAsync();
        AssertRequest(request.RootElement, 1, "x.ai/session/rename");
        var parameters = request.RootElement.GetProperty("params");
        Assert.Equal("session-42", parameters.GetProperty("sessionId").GetString());
        Assert.Equal("Parser repaired", parameters.GetProperty("title").GetString());
        Assert.Equal("C:\\repo", parameters.GetProperty("cwd").GetString());
        Assert.Equal("build", parameters.GetProperty("kind").GetString());
        await peer.RespondAsync(1, "{ \"success\": true }");

        await renameTask;
    }

    [Fact]
    public async Task ForkSessionAsync_UsesPersistedForkExtensionAndMapsCopyEvidence()
    {
        await using var peer = new AcpPeer();
        var forkTask = peer.Client.ForkSessionAsync(
            new SessionId("source-session"),
            "C:\\repo",
            "C:\\repo-worktree",
            targetPromptIndex: 3,
            modelId: "grok-4.5",
            sessionKind: "worktree",
            sourceWorkspacePath: "C:\\repo",
            cancellationToken: peer.Timeout.Token);

        using var request = await peer.ReadMessageAsync();
        AssertRequest(request.RootElement, 1, "x.ai/session/fork");
        var parameters = request.RootElement.GetProperty("params");
        Assert.Equal("source-session", parameters.GetProperty("source_session_id").GetString());
        Assert.Equal("C:\\repo", parameters.GetProperty("source_cwd").GetString());
        Assert.Equal("C:\\repo-worktree", parameters.GetProperty("new_cwd").GetString());
        Assert.Equal(3, parameters.GetProperty("target_prompt_index").GetInt32());
        Assert.Equal("worktree", parameters.GetProperty("session_kind").GetString());
        await peer.RespondAsync(
            1,
            """
            {
              "newSessionId": "fork-session",
              "chatMessagesCopied": 8,
              "updatesCopied": 20,
              "planStateCopied": true,
              "newCwd": "C:\\repo-worktree",
              "parentSessionId": "source-session",
              "newModelId": "grok-4.5"
            }
            """);

        var result = await forkTask;
        Assert.Equal("fork-session", result.SessionId.Value);
        Assert.Equal(8, result.ChatMessagesCopied);
        Assert.Equal(20, result.UpdatesCopied);
        Assert.True(result.PlanStateCopied);
        Assert.Equal("source-session", result.ParentSessionId);
    }

    [Fact]
    public async Task CompactAndRewindOperations_UseCanonicalExtensionShapes()
    {
        await using var peer = new AcpPeer();
        var compactTask = peer.Client.CompactSessionAsync(
            new SessionId("session-42"),
            "Preserve the API contract",
            peer.Timeout.Token);
        using (var compact = await peer.ReadMessageAsync())
        {
            AssertRequest(compact.RootElement, 1, "x.ai/compact_conversation");
            var parameters = compact.RootElement.GetProperty("params");
            Assert.Equal("session-42", parameters.GetProperty("sessionId").GetString());
            Assert.Equal(
                "Preserve the API contract",
                parameters.GetProperty("userContext").GetString());
        }
        await peer.RespondAsync(1, "{}");
        await compactTask;

        var pointsTask = peer.Client.GetRewindPointsAsync(
            new SessionId("session-42"),
            peer.Timeout.Token);
        using (var points = await peer.ReadMessageAsync())
        {
            AssertRequest(points.RootElement, 2, "x.ai/rewind/points");
            Assert.Equal(
                "session-42",
                points.RootElement.GetProperty("params").GetProperty("sessionId").GetString());
        }
        await peer.RespondAsync(
            2,
            """
            {
              "rewind_points": [
                {
                  "prompt_index": 3,
                  "created_at": "2026-07-16T09:30:00Z",
                  "num_file_snapshots": 2,
                  "has_file_changes": true,
                  "prompt_preview": "Refactor parser"
                }
              ]
            }
            """);
        var rewindPoint = Assert.Single(await pointsTask);
        Assert.Equal(3, rewindPoint.PromptIndex);
        Assert.True(rewindPoint.HasFileChanges);

        var rewindTask = peer.Client.RewindSessionAsync(
            new SessionId("session-42"),
            targetPromptIndex: 3,
            SessionRewindMode.ConversationOnly,
            force: false,
            peer.Timeout.Token);
        using (var rewind = await peer.ReadMessageAsync())
        {
            AssertRequest(rewind.RootElement, 3, "x.ai/rewind/execute");
            var parameters = rewind.RootElement.GetProperty("params");
            Assert.Equal(3, parameters.GetProperty("targetPromptIndex").GetInt32());
            Assert.Equal("conversation_only", parameters.GetProperty("mode").GetString());
            Assert.False(parameters.GetProperty("force").GetBoolean());
        }
        await peer.RespondAsync(
            3,
            """
            {
              "success": true,
              "target_prompt_index": 3,
              "mode": "conversation_only",
              "reverted_files": [],
              "clean_files": ["src/parser.rs"],
              "conflicts": [],
              "prompt_text": "Refactor parser",
              "error": null
            }
            """);
        var rewindResult = await rewindTask;
        Assert.True(rewindResult.Success);
        Assert.Equal(SessionRewindMode.ConversationOnly, rewindResult.Mode);
        Assert.Equal("Refactor parser", rewindResult.PromptText);
        Assert.Equal(["src/parser.rs"], rewindResult.CleanFiles);
    }

    [Fact]
    public async Task InitializeAsync_RejectsHealthWithoutSandboxAttestation()
    {
        await using var peer = new AcpPeer();

        var initializeTask = peer.Client.InitializeAsync(peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            """
            {
              "protocolVersion": 1,
              "agentCapabilities": {},
              "authMethods": [
                { "type": "agent", "id": "xai.api_key", "name": "xai.api_key" }
              ]
            }
            """);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(2, "{ \"protocolVersion\": 1 }");
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(3, "{ \"status\": \"ok\" }");

        await Assert.ThrowsAsync<InvalidDataException>(() => initializeTask);
    }

    [Fact]
    public async Task InitializeAsync_PropagatesVersionedExtensionServerErrors()
    {
        await using var peer = new AcpPeer();

        var initializeTask = peer.Client.InitializeAsync(peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            """
            {
              "protocolVersion": 1,
              "agentCapabilities": {},
              "authMethods": [
                { "type": "agent", "id": "xai.api_key", "name": "xai.api_key" }
              ]
            }
            """);
        _ = await peer.ReadMessageAsync();
        await peer.RespondWithErrorAsync(2, -32603, "Extension initialization failed");

        var exception = await Assert.ThrowsAsync<JsonRpcException>(() => initializeTask);
        Assert.Equal(-32603, exception.Code);
    }

    [Fact]
    public async Task SessionLifecycle_UsesRealAcpShapesInRequestOrderAndMapsResponses()
    {
        await using var peer = new AcpPeer();
        await InitializeWithoutExtensionsAsync(peer);

        var authenticateTask = peer.Client.AuthenticateAsync(peer.Timeout.Token);
        using var authenticate = await peer.ReadMessageAsync();
        AssertRequest(authenticate.RootElement, 3, "authenticate");
        Assert.Equal("xai.api_key", authenticate.RootElement.GetProperty("params").GetProperty("methodId").GetString());
        Assert.True(authenticate.RootElement.GetProperty("params").GetProperty("_meta").GetProperty("headless").GetBoolean());
        await peer.RespondAsync(3, "{}");
        await authenticateTask;

        const string cwd = "C:\\work\\sample";
        var newSessionTask = peer.Client.NewSessionAsync(cwd, peer.Timeout.Token);
        using var newSession = await peer.ReadMessageAsync();
        AssertRequest(newSession.RootElement, 4, "session/new");
        Assert.Equal(cwd, newSession.RootElement.GetProperty("params").GetProperty("cwd").GetString());
        Assert.Empty(newSession.RootElement.GetProperty("params").GetProperty("mcpServers").EnumerateArray());
        await peer.RespondAsync(4, "{ \"sessionId\": \"session-42\" }");
        var sessionId = await newSessionTask;
        Assert.Equal("session-42", sessionId.Value);

        var loadTask = peer.Client.LoadSessionAsync(sessionId, cwd, peer.Timeout.Token);
        using var load = await peer.ReadMessageAsync();
        AssertRequest(load.RootElement, 5, "session/load");
        Assert.Equal("session-42", load.RootElement.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal(cwd, load.RootElement.GetProperty("params").GetProperty("cwd").GetString());
        Assert.Empty(load.RootElement.GetProperty("params").GetProperty("mcpServers").EnumerateArray());
        await peer.RespondAsync(5, "{}");
        await loadTask;

        var promptTask = peer.Client.PromptAsync(sessionId, "你好，AgentDesk", peer.Timeout.Token);
        using var prompt = await peer.ReadMessageAsync();
        AssertRequest(prompt.RootElement, 6, "session/prompt");
        Assert.Equal("session-42", prompt.RootElement.GetProperty("params").GetProperty("sessionId").GetString());
        var content = Assert.Single(prompt.RootElement.GetProperty("params").GetProperty("prompt").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.Equal("你好，AgentDesk", content.GetProperty("text").GetString());
        await peer.RespondAsync(6, "{ \"stopReason\": \"end_turn\" }");
        var promptResult = await promptTask;
        Assert.Equal(EngineStopReason.EndTurn, promptResult.StopReason);

        await peer.Client.CancelAsync(sessionId, peer.Timeout.Token);
        using var cancel = await peer.ReadMessageAsync();
        Assert.Equal("2.0", cancel.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("session/cancel", cancel.RootElement.GetProperty("method").GetString());
        Assert.Equal("session-42", cancel.RootElement.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.False(cancel.RootElement.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task ExportSessionAsync_UsesVersionedAgentDeskDocumentWithoutLeakingIt()
    {
        await using var peer = new AcpPeer();

        var exportTask = peer.Client.ExportSessionAsync(
            new SessionId("session-42"),
            peer.Timeout.Token);
        using var request = await peer.ReadMessageAsync();

        AssertRequest(request.RootElement, 1, "agentdesk/v1/session/export");
        Assert.Equal(
            "session-42",
            request.RootElement.GetProperty("params").GetProperty("sessionId").GetString());
        await peer.RespondAsync(
            1,
            """
            {
              "schemaVersion": 1,
              "session": {
                "sessionId": "session-42",
                "messages": [{ "content": "private-session-content" }],
                "metadata": { "cwd": "C:\\repo" }
              }
            }
            """);

        var document = await exportTask;
        var exported = document.ExportUtf8Json();
        Assert.Contains("private-session-content", Encoding.UTF8.GetString(exported));
        Assert.DoesNotContain("private-session-content", document.ToString(), StringComparison.Ordinal);
        exported[0] ^= 0xff;
        Assert.StartsWith("{", Encoding.UTF8.GetString(document.ExportUtf8Json()));
    }

    [Fact]
    public async Task ImportSessionAsync_SendsBoundedDocumentAndMapsTheNewSessionId()
    {
        await using var peer = new AcpPeer();
        var document = EngineSessionDocument.FromJson(
            "{\"sessionId\":\"source-1\",\"messages\":[],\"metadata\":{\"cwd\":\"C:\\\\source\"}}");

        var importTask = peer.Client.ImportSessionAsync(
            document,
            "C:\\target",
            peer.Timeout.Token);
        using var request = await peer.ReadMessageAsync();

        AssertRequest(request.RootElement, 1, "agentdesk/v1/session/import");
        var parameters = request.RootElement.GetProperty("params");
        Assert.Equal("C:\\target", parameters.GetProperty("cwd").GetString());
        Assert.Equal(
            "source-1",
            parameters.GetProperty("session").GetProperty("sessionId").GetString());
        await peer.RespondAsync(1, "{ \"schemaVersion\": 1, \"sessionId\": \"imported-7\" }");

        Assert.Equal("imported-7", (await importTask).Value);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public void EngineSessionDocument_RejectsNonObjectJson(string json)
    {
        Assert.Throws<ArgumentException>(() => EngineSessionDocument.FromJson(json));
    }

    [Fact]
    public async Task PromptWithAttachmentsAsync_SendsValidatedAcpImageBlocks()
    {
        await using var peer = new AcpPeer();
        var sessionId = new SessionId("session-42");
        var attachment = new PromptAttachment(
            "pixel.png",
            "image/png",
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        var promptTask = peer.Client.PromptWithAttachmentsAsync(
            sessionId,
            "检查这张图片",
            [attachment],
            peer.Timeout.Token);
        using var prompt = await peer.ReadMessageAsync();

        AssertRequest(prompt.RootElement, 1, "session/prompt");
        var blocks = prompt.RootElement
            .GetProperty("params")
            .GetProperty("prompt")
            .EnumerateArray()
            .ToArray();
        Assert.Collection(
            blocks,
            text =>
            {
                Assert.Equal("text", text.GetProperty("type").GetString());
                Assert.Equal("检查这张图片", text.GetProperty("text").GetString());
            },
            image =>
            {
                Assert.Equal("image", image.GetProperty("type").GetString());
                Assert.Equal(attachment.Base64Data, image.GetProperty("data").GetString());
                Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
            });

        await peer.RespondAsync(1, "{ \"stopReason\": \"end_turn\" }");
        Assert.Equal(EngineStopReason.EndTurn, (await promptTask).StopReason);
    }

    [Theory]
    [InlineData("text/plain", "aGVsbG8=")]
    [InlineData("image/png", "not-base64")]
    [InlineData("image/png", "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==")]
    public async Task PromptWithAttachmentsAsync_RejectsInvalidImagePayloads(
        string mimeType,
        string data)
    {
        await using var peer = new AcpPeer();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            peer.Client.PromptWithAttachmentsAsync(
                new SessionId("session-42"),
                "inspect",
                [new PromptAttachment("image", mimeType, data)],
                peer.Timeout.Token));
    }

    [Fact]
    public async Task PromptWithAttachmentsAsync_RejectsMoreThanFourImages()
    {
        await using var peer = new AcpPeer();
        var attachment = new PromptAttachment(
            "pixel.png",
            "image/png",
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            peer.Client.PromptWithAttachmentsAsync(
                new SessionId("session-42"),
                "inspect",
                [attachment, attachment, attachment, attachment, attachment],
                peer.Timeout.Token));
    }

    [Fact]
    public async Task SessionUpdateNotification_MapsTheSessionKindPayloadAndMetadata()
    {
        await using var peer = new AcpPeer();
        var received = new TaskCompletionSource<EngineEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Client.EventReceived += (_, engineEvent) => received.TrySetResult(engineEvent);

        await peer.NotifyAsync(
            "session/update",
            """
            {
              "sessionId": "session-42",
              "update": {
                "sessionUpdate": "agent_message_chunk",
                "content": { "type": "text", "text": "第一段" }
              },
              "_meta": { "promptId": "prompt-7" }
            }
            """);

        var engineEvent = await received.Task.WaitAsync(peer.Timeout.Token);

        Assert.Equal("session-42", engineEvent.SessionId.Value);
        Assert.Equal("agent_message_chunk", engineEvent.UpdateKind);
        Assert.Equal("第一段", engineEvent.Update.GetProperty("content").GetProperty("text").GetString());
        Assert.Equal("prompt-7", engineEvent.Metadata?.GetProperty("promptId").GetString());
    }

    [Fact]
    public async Task XaiSessionUpdateNotification_MapsDiffReviewPayloadAndMetadata()
    {
        await using var peer = new AcpPeer();
        var received = new TaskCompletionSource<EngineEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Client.EventReceived += (_, engineEvent) => received.TrySetResult(engineEvent);

        await peer.NotifyAsync(
            "x.ai/session/update",
            """
            {
              "sessionId": "session-42",
              "update": {
                "sessionUpdate": "diff_review",
                "content": [
                  {
                    "type": "diff",
                    "path": "desktop/src/App.cs",
                    "oldText": "old",
                    "newText": "new"
                  }
                ]
              },
              "_meta": { "eventId": "diff-7" }
            }
            """);

        var engineEvent = await received.Task.WaitAsync(peer.Timeout.Token);

        Assert.Equal("session-42", engineEvent.SessionId.Value);
        Assert.Equal("diff_review", engineEvent.UpdateKind);
        var diff = Assert.Single(engineEvent.Update.GetProperty("content").EnumerateArray());
        Assert.Equal("desktop/src/App.cs", diff.GetProperty("path").GetString());
        Assert.Equal("diff-7", engineEvent.Metadata?.GetProperty("eventId").GetString());
    }

    [Fact]
    public async Task MalformedSessionUpdate_IsIgnoredWithoutTerminatingTheConnection()
    {
        await using var peer = new AcpPeer();
        var initializeTask = peer.Client.InitializeAsync(peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();

        await peer.NotifyAsync(
            "session/update",
            """
            {
              "sessionId": 42,
              "update": { "sessionUpdate": "agent_message_chunk" }
            }
            """);
        await peer.RespondAsync(
            1,
            """
            {
              "protocolVersion": 1,
              "agentCapabilities": {},
              "authMethods": [
                { "type": "agent", "id": "xai.api_key", "name": "xai.api_key" }
              ]
            }
            """);

        using var extensionInitialize = await peer.ReadMessageAsync();
        AssertRequest(extensionInitialize.RootElement, 2, "agentdesk/v1/initialize");
        await peer.RespondWithErrorAsync(2, -32601, "Method not found");

        var capabilities = await initializeTask;
        Assert.Equal(1, capabilities.ProtocolVersion);
    }

    [Fact]
    public async Task SessionUpdateSubscriberException_DoesNotTerminateTheConnection()
    {
        await using var peer = new AcpPeer();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Client.EventReceived += (_, _) =>
        {
            received.TrySetResult();
            throw new InvalidOperationException("UI subscriber failed");
        };

        await peer.NotifyAsync(
            "session/update",
            """
            {
              "sessionId": "session-42",
              "update": {
                "sessionUpdate": "agent_message_chunk",
                "content": { "type": "text", "text": "still alive" }
              }
            }
            """);
        await received.Task.WaitAsync(peer.Timeout.Token);

        await InitializeWithoutExtensionsAsync(peer);
        Assert.Equal(1, peer.Client.Capabilities.ProtocolVersion);
    }

    [Fact]
    public async Task PermissionRequest_MapsTheAcpShapeAndReturnsTheSelectedOption()
    {
        await using var peer = new AcpPeer();
        var received = new TaskCompletionSource<PermissionRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Client.PermissionRequested += (_, request) => received.TrySetResult(request);

        await peer.RequestPermissionAsync(
            41,
            """
            {
              "sessionId": "session-42",
              "toolCall": {
                "toolCallId": "write-file-7",
                "title": "写入 desktop/README.md",
                "kind": "edit",
                "locations": [
                  { "path": "C:\\work\\desktop\\README.md", "line": 12 }
                ],
                "rawInput": { "path": "desktop/README.md" }
              },
              "options": [
                { "optionId": "allow-once", "name": "允许一次", "kind": "allow_once" },
                { "optionId": "allow-always", "name": "始终允许", "kind": "allow_always" },
                { "optionId": "reject-once", "name": "拒绝", "kind": "reject_once" },
                { "optionId": "reject-always", "name": "始终拒绝", "kind": "reject_always" }
              ],
              "_meta": { "source": "tool-loop" }
            }
            """);

        var request = await received.Task.WaitAsync(peer.Timeout.Token);
        Assert.False(string.IsNullOrWhiteSpace(request.RequestId));
        Assert.Equal("session-42", request.SessionId.Value);
        Assert.Equal("write-file-7", request.ToolCallId);
        Assert.Equal("写入 desktop/README.md", request.Title);
        Assert.Equal("edit", request.ToolKind);
        Assert.Equal(
            "desktop/README.md",
            request.RawInput?.GetProperty("path").GetString());
        Assert.Equal(["C:\\work\\desktop\\README.md:12"], request.Locations);
        Assert.Collection(
            request.Options,
            option => Assert.Equal(
                new PermissionOption("allow-once", "允许一次", PermissionOptionKind.AllowOnce),
                option),
            option => Assert.Equal(PermissionOptionKind.AllowAlways, option.Kind),
            option => Assert.Equal(PermissionOptionKind.RejectOnce, option.Kind),
            option => Assert.Equal(PermissionOptionKind.RejectAlways, option.Kind));

        Assert.True(await peer.Client.RespondToPermissionAsync(
            request.RequestId,
            PermissionDecision.Selected("allow-once"),
            peer.Timeout.Token));
        using var response = await peer.ReadMessageAsync();

        Assert.Equal(41, response.RootElement.GetProperty("id").GetInt64());
        var outcome = response.RootElement.GetProperty("result").GetProperty("outcome");
        Assert.Equal("selected", outcome.GetProperty("outcome").GetString());
        Assert.Equal("allow-once", outcome.GetProperty("optionId").GetString());
    }

    [Fact]
    public async Task PermissionRequest_CanBeCancelledExplicitly()
    {
        await using var peer = new AcpPeer();
        var received = new TaskCompletionSource<PermissionRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Client.PermissionRequested += (_, request) => received.TrySetResult(request);
        await peer.RequestPermissionAsync(42, PermissionRequestJson("session-42"));
        var request = await received.Task.WaitAsync(peer.Timeout.Token);

        Assert.True(await peer.Client.RespondToPermissionAsync(
            request.RequestId,
            PermissionDecision.Cancelled,
            peer.Timeout.Token));
        using var response = await peer.ReadMessageAsync();

        var outcome = response.RootElement.GetProperty("result").GetProperty("outcome");
        Assert.Equal("cancelled", outcome.GetProperty("outcome").GetString());
        Assert.False(outcome.TryGetProperty("optionId", out _));
    }

    [Fact]
    public async Task PermissionRequest_WithoutAUiSubscriberFailsClosedAsCancelled()
    {
        await using var peer = new AcpPeer();

        await peer.RequestPermissionAsync(43, PermissionRequestJson("session-42"));
        using var response = await peer.ReadMessageAsync();

        Assert.Equal(43, response.RootElement.GetProperty("id").GetInt64());
        Assert.Equal(
            "cancelled",
            response.RootElement.GetProperty("result")
                .GetProperty("outcome")
                .GetProperty("outcome")
                .GetString());
    }

    [Fact]
    public async Task PermissionResponse_WithAnUnknownOptionFailsClosedAsCancelled()
    {
        await using var peer = new AcpPeer();
        var received = new TaskCompletionSource<PermissionRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Client.PermissionRequested += (_, request) => received.TrySetResult(request);
        await peer.RequestPermissionAsync(44, PermissionRequestJson("session-42"));
        var request = await received.Task.WaitAsync(peer.Timeout.Token);

        Assert.False(await peer.Client.RespondToPermissionAsync(
            request.RequestId,
            PermissionDecision.Selected("not-an-advertised-option"),
            peer.Timeout.Token));
        using var response = await peer.ReadMessageAsync();

        Assert.Equal(
            "cancelled",
            response.RootElement.GetProperty("result")
                .GetProperty("outcome")
                .GetProperty("outcome")
                .GetString());
    }

    [Fact]
    public async Task PermissionRequest_WithAnUnknownOptionKindFailsClosedWithoutUiProjection()
    {
        await using var peer = new AcpPeer();
        var eventCount = 0;
        peer.Client.PermissionRequested += (_, _) => Interlocked.Increment(ref eventCount);

        await peer.RequestPermissionAsync(
            45,
            """
            {
              "sessionId": "session-42",
              "toolCall": { "toolCallId": "tool-1", "title": "未知操作" },
              "options": [
                { "optionId": "future", "name": "Future option", "kind": "allow_for_project" }
              ]
            }
            """);
        using var response = await peer.ReadMessageAsync();

        Assert.Equal(0, Volatile.Read(ref eventCount));
        Assert.Equal(
            "cancelled",
            response.RootElement.GetProperty("result")
                .GetProperty("outcome")
                .GetProperty("outcome")
                .GetString());
    }

    [Fact]
    public async Task CancelAsync_CancelsEveryPendingPermissionForOnlyThatSession()
    {
        await using var peer = new AcpPeer();
        var requests = new List<PermissionRequest>();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Client.PermissionRequested += (_, request) =>
        {
            lock (requests)
            {
                requests.Add(request);
                if (requests.Count == 3)
                {
                    allReceived.TrySetResult();
                }
            }
        };
        await peer.RequestPermissionAsync(51, PermissionRequestJson("session-1", "tool-1"));
        await peer.RequestPermissionAsync(52, PermissionRequestJson("session-1", "tool-2"));
        await peer.RequestPermissionAsync(53, PermissionRequestJson("session-2", "tool-3"));
        await allReceived.Task.WaitAsync(peer.Timeout.Token);

        await peer.Client.CancelAsync(new SessionId("session-1"), peer.Timeout.Token);
        using var cancel = await peer.ReadMessageAsync();
        Assert.Equal("session/cancel", cancel.RootElement.GetProperty("method").GetString());
        Assert.Equal("session-1", cancel.RootElement.GetProperty("params").GetProperty("sessionId").GetString());

        using var firstCancelled = await peer.ReadMessageAsync();
        using var secondCancelled = await peer.ReadMessageAsync();
        var cancelledIds = new[]
        {
            firstCancelled.RootElement.GetProperty("id").GetInt64(),
            secondCancelled.RootElement.GetProperty("id").GetInt64(),
        };
        Assert.Equal([51L, 52L], cancelledIds.Order());
        Assert.All(
            new[] { firstCancelled, secondCancelled },
            response => Assert.Equal(
                "cancelled",
                response.RootElement.GetProperty("result")
                    .GetProperty("outcome")
                    .GetProperty("outcome")
                    .GetString()));

        PermissionRequest remaining;
        lock (requests)
        {
            remaining = Assert.Single(requests, request => request.SessionId.Value == "session-2");
        }
        Assert.True(await peer.Client.RespondToPermissionAsync(
            remaining.RequestId,
            PermissionDecision.Selected("allow-once"),
            peer.Timeout.Token));
        using var remainingResponse = await peer.ReadMessageAsync();
        Assert.Equal(53, remainingResponse.RootElement.GetProperty("id").GetInt64());
        Assert.Equal(
            "selected",
            remainingResponse.RootElement.GetProperty("result")
                .GetProperty("outcome")
                .GetProperty("outcome")
                .GetString());
    }

    [Fact]
    public async Task MalformedPermissionRequest_ReturnsInvalidParamsAndKeepsTheConnectionAlive()
    {
        await using var peer = new AcpPeer();
        peer.Client.PermissionRequested += (_, _) => { };

        await peer.RequestPermissionAsync(
            61,
            "{ \"sessionId\": \"session-42\", \"toolCall\": {}, \"options\": [] }");
        using var response = await peer.ReadMessageAsync();
        Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        await InitializeWithoutExtensionsAsync(peer);
        Assert.Equal(1, peer.Client.Capabilities.ProtocolVersion);
    }

    private static async Task InitializeWithoutExtensionsAsync(AcpPeer peer)
    {
        var initializeTask = peer.Client.InitializeAsync(peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        await peer.RespondAsync(
            1,
            """
            {
              "protocolVersion": 1,
              "agentCapabilities": {},
              "authMethods": [
                { "type": "agent", "id": "xai.api_key", "name": "xai.api_key" }
              ]
            }
            """);
        _ = await peer.ReadMessageAsync();
        await peer.RespondWithErrorAsync(2, -32601, "Method not found");
        _ = await initializeTask;
    }

    private static string PermissionRequestJson(string sessionId, string toolCallId = "tool-1") =>
        $$"""
        {
          "sessionId": "{{sessionId}}",
          "toolCall": {
            "toolCallId": "{{toolCallId}}",
            "title": "执行敏感操作"
          },
          "options": [
            { "optionId": "allow-once", "name": "允许一次", "kind": "allow_once" },
            { "optionId": "reject-once", "name": "拒绝", "kind": "reject_once" }
          ]
        }
        """;

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

    private sealed class AcpPeer : IAsyncDisposable
    {
        private readonly Pipe _serverToClient = new();
        private readonly Pipe _clientToServer = new();
        private readonly StreamReader _requestReader;

        public AcpPeer(string? desktopApiKey = null)
        {
            Client = new AcpEngineClient(
                _serverToClient.Reader.AsStream(),
                _clientToServer.Writer.AsStream(),
                desktopApiKey);
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

        public Task RespondWithErrorAsync(long id, int code, string message) => WriteToClientAsync(
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                error = new { code, message },
            }) + "\n");

        public Task NotifyAsync(string method, string parametersJson) => WriteToClientAsync(
            $"{{\"jsonrpc\":\"2.0\",\"method\":{JsonSerializer.Serialize(method)},\"params\":{CompactJson(parametersJson)}}}\n");

        public Task RequestPermissionAsync(long id, string parametersJson) => WriteToClientAsync(
            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"session/request_permission\",\"params\":{CompactJson(parametersJson)}}}\n");

        public Task CompleteOutputAsync() => _serverToClient.Writer.CompleteAsync().AsTask();

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
