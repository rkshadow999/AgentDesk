using System.Text.Json;
using AgentDesk.App.Attachments;
using AgentDesk.App.Automation;
using AgentDesk.App.Bridge;
using AgentDesk.App.Cloud;
using AgentDesk.App.Settings;
using AgentDesk.Core.Engine;
using AgentDesk.Core.Execution;
using AgentDesk.Core.Providers;

namespace AgentDesk.App.Tests;

public sealed class WebMessageProtocolTests
{
    [Fact]
    public void ParseCommand_MapsNativeAttachmentSelectionAndDiscard()
    {
        const string requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";
        const string token =
            "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

        var select = Assert.IsType<SelectImageAttachmentsWebCommand>(
            WebMessageProtocol.ParseCommand($$"""
                {
                  "schemaVersion": 1,
                  "type": "attachment/select",
                  "requestId": "{{requestId}}"
                }
                """));
        var discard = Assert.IsType<DiscardImageAttachmentsWebCommand>(
            WebMessageProtocol.ParseCommand($$"""
                {
                  "schemaVersion": 1,
                  "type": "attachment/discard",
                  "tokens": ["{{token}}"]
                }
                """));

        Assert.Equal(requestId, select.RequestId);
        Assert.Equal(token, Assert.Single(discard.Tokens));
    }

    [Fact]
    public void SerializeEvent_ProjectsOnlyNativeAttachmentMetadata()
    {
        const string token =
            "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        var json = WebMessageProtocol.SerializeEvent(new ImageAttachmentsChangedWebEvent(
            "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d",
            [new NativeImageAttachmentReference(token, "pixel.png", "image/png", 68)]));

        Assert.Contains("attachment/changed", json, StringComparison.Ordinal);
        Assert.Contains(token, json, StringComparison.Ordinal);
        Assert.Contains("pixel.png", json, StringComparison.Ordinal);
        Assert.DoesNotContain("base64Data", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCommand_MapsOnlyBoundedPromptAttachmentReferences()
    {
        var command = Assert.IsType<PromptWebCommand>(WebMessageProtocol.ParseCommand("""
            {
              "schemaVersion": 1,
              "type": "engine/prompt",
              "text": "inspect",
              "executionProfile": "NativeProtected",
              "sessionMode": "default",
              "nativeRiskAcknowledged": true,
              "workspaceGeneration": 1,
              "attachments": [
                {
                  "token": "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
                  "name": "pixel.png",
                  "mimeType": "image/png",
                  "size": 68
                }
              ]
            }
            """));

        var attachment = Assert.Single(command.AttachmentReferences);
        Assert.Equal(
            "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
            attachment.Token);
        Assert.Equal("pixel.png", attachment.Name);
        Assert.Equal("image/png", attachment.MimeType);
        Assert.Equal(68, attachment.Size);
    }

    [Fact]
    public void ParseCommand_RejectsBrowserSuppliedAttachmentContentWithoutEchoingIt()
    {
        const string invalidData = "this-sensitive-base64-must-not-be-echoed";

        var error = Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand($$"""
            {
              "schemaVersion": 1,
              "type": "engine/prompt",
              "text": "inspect",
              "executionProfile": "NativeProtected",
              "sessionMode": "default",
              "nativeRiskAcknowledged": true,
              "workspaceGeneration": 1,
              "attachments": [
                {
                  "token": "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
                  "name": "pixel.png",
                  "mimeType": "image/png",
                  "size": 68,
                  "base64Data": "{{invalidData}}"
                }
              ]
            }
            """));

        Assert.DoesNotContain(invalidData, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown", "true")]
    [InlineData("text", "\"first\", \"text\": \"second\"")]
    public void ParseCommand_RejectsUnknownOrDuplicateRootProperties(string property, string value)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "type": "engine/prompt",
              "text": "inspect",
              "executionProfile": "NativeProtected",
              "sessionMode": "default",
              "nativeRiskAcknowledged": true,
              "workspaceGeneration": 1,
              "{{property}}": {{value}}
            }
            """;

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Fact]
    public void ParseCommand_RejectsMessagesAboveTheProtocolLimit()
    {
        var json = new string('x', WebMessageProtocol.MaximumMessageCharacters + 1);

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Fact]
    public void ParseCommand_MapsRuntimeCommandsMemoryAndPreferencesCommands()
    {
        var list = Assert.IsType<RuntimeCommandsListWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "runtime/commands/list",
                  "workspaceGeneration": 7
                }
                """));
        Assert.Equal(7, list.WorkspaceGeneration);

        var flush = Assert.IsType<MemoryFlushWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "runtime/memory/flush",
                  "sessionId": "session-42"
                }
                """));
        Assert.Equal("session-42", flush.SessionId);

        var preferences = Assert.IsType<SaveUiPreferencesWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "ui/preferences/save",
                  "language": "en-US",
                  "composerDraft": "continue the review",
                  "sessionMode": "plan",
                  "executionProfile": "WslStrict",
                  "notificationsEnabled": true,
                  "windowsAutomationEnabled": true,
                  "backgroundUpdateChecksEnabled": true
                }
                """));
        Assert.Equal("en-US", preferences.Preferences.Language);
        Assert.Equal("continue the review", preferences.Preferences.ComposerDraft);
        Assert.Equal(SessionMode.Plan, preferences.Preferences.SessionMode);
        Assert.Equal(ExecutionProfile.WslStrict, preferences.Preferences.ExecutionProfile);
        Assert.True(preferences.Preferences.NotificationsEnabled);
        Assert.True(preferences.Preferences.WindowsAutomationEnabled);
        Assert.True(preferences.Preferences.BackgroundUpdateChecksEnabled);
    }

    [Fact]
    public void ParseCommand_RejectsUiPreferencesWithoutSecurityOptIns()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "type": "ui/preferences/save",
              "language": "en-US",
              "composerDraft": "continue the review",
              "sessionMode": "plan",
              "executionProfile": "WslStrict"
            }
            """;

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Fact]
    public void ParseCommand_MapsPromptAndExecutionProfile()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "type": "engine/prompt",
              "text": "检查当前改动",
              "executionProfile": "NativeProtected",
              "sessionMode": "plan",
              "nativeRiskAcknowledged": true,
              "workspaceGeneration": 0
            }
            """;

        var command = Assert.IsType<PromptWebCommand>(WebMessageProtocol.ParseCommand(json));

        Assert.Equal("检查当前改动", command.Text);
        Assert.Equal(ExecutionProfile.NativeProtected, command.ExecutionProfile);
        Assert.Equal(SessionMode.Plan, command.SessionMode);
        Assert.True(command.NativeRiskAcknowledged);
        Assert.Equal(0, command.WorkspaceGeneration);
    }

    [Fact]
    public void ParseCommand_MapsSessionCatalogCommands()
    {
        var list = Assert.IsType<SessionListWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "session/list",
                  "requestId": "11111111-1111-4111-8111-111111111111",
                  "query": "parser",
                  "cursor": "cursor-1",
                  "limit": 50,
                  "archived": true
                }
                """));
        Assert.Equal("parser", list.Query);
        Assert.Equal(
            "11111111-1111-4111-8111-111111111111",
            list.GetType().GetProperty("RequestId")?.GetValue(list));
        Assert.Equal("cursor-1", list.Cursor);
        Assert.Equal(50, list.Limit);
        Assert.True(list.Archived);

        var open = Assert.IsType<SessionOpenWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "session/open",
                  "sessionId": "session-42",
                  "workspacePath": "C:\\repo",
                  "executionProfile": "WslStrict"
                }
                """));
        Assert.Equal("session-42", open.SessionId);
        Assert.Equal("C:\\repo", open.WorkspacePath);
        Assert.Equal(ExecutionProfile.WslStrict, open.ExecutionProfile);

        var rename = Assert.IsType<SessionRenameWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "session/rename",
                  "requestId": "22222222-2222-4222-8222-222222222222",
                  "sessionId": "session-42",
                  "title": "Parser repaired",
                  "workspacePath": "C:\\repo"
                }
                """));
        Assert.Equal("Parser repaired", rename.Title);
        Assert.Equal("22222222-2222-4222-8222-222222222222", rename.RequestId);

        var archive = Assert.IsType<SessionArchiveWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "session/archive",
                  "requestId": "33333333-3333-4333-8333-333333333333",
                  "sessionId": "session-42",
                  "archived": true
                }
                """));
        Assert.Equal("session-42", archive.SessionId);
        Assert.True(archive.Archived);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("1.5")]
    [InlineData("null")]
    public void ParseCommand_RejectsInvalidSessionCatalogLimits(string rawLimit)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "type": "session/list",
              "requestId": "11111111-1111-4111-8111-111111111111",
              "query": "",
              "limit": {{rawLimit}}
            }
            """;

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-request-id")]
    [InlineData("AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA")]
    public void ParseCommand_RejectsInvalidSessionListRequestIdentifiers(string? requestId)
    {
        var requestIdField = requestId is null
            ? string.Empty
            : $"\"requestId\": {JsonSerializer.Serialize(requestId)},";
        var json = $$"""
            {
              "schemaVersion": 1,
              "type": "session/list",
              {{requestIdField}}
              "query": "",
              "limit": 50
            }
            """;

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Fact]
    public void ParseCommand_MapsSessionHistoryOperations()
    {
        var fork = Assert.IsType<SessionForkWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "session/fork",
                  "sessionId": "session-42",
                  "sourceWorkspacePath": "C:\\repo",
                  "targetWorkspacePath": "C:\\repo",
                  "targetPromptIndex": 3
                }
                """));
        Assert.Equal(3, fork.TargetPromptIndex);

        var compact = Assert.IsType<SessionCompactWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "session/compact",
                  "sessionId": "session-42",
                  "userContext": "Keep the API contract"
                }
                """));
        Assert.Equal("Keep the API contract", compact.UserContext);

        var points = Assert.IsType<SessionRewindPointsWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "session/rewind/points",
                  "sessionId": "session-42"
                }
                """));
        Assert.Equal("session-42", points.SessionId);

        var rewind = Assert.IsType<SessionRewindWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "session/rewind",
                  "sessionId": "session-42",
                  "targetPromptIndex": 3,
                  "mode": "conversation_only",
                  "force": false
                }
                """));
        Assert.Equal(SessionRewindMode.ConversationOnly, rewind.Mode);
        Assert.False(rewind.Force);
    }

    [Fact]
    public void ParseCommand_MapsRuntimeDashboardOperations()
    {
        var refresh = Assert.IsType<RuntimeDashboardRefreshWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "runtime/dashboard/refresh",
                  "sessionId": "session-42"
                }
                """));
        Assert.Equal("session-42", refresh.SessionId);

        var kill = Assert.IsType<RuntimeTaskKillWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "runtime/task/kill",
                  "sessionId": "session-42",
                  "taskId": "task-7"
                }
                """));
        Assert.Equal("task-7", kill.TaskId);

        var get = Assert.IsType<RuntimeSubagentGetWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "runtime/subagent/get",
                  "sessionId": "session-42",
                  "subagentId": "subagent-7"
                }
                """));
        Assert.Equal("subagent-7", get.SubagentId);

        var cancel = Assert.IsType<RuntimeSubagentCancelWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "runtime/subagent/cancel",
                  "sessionId": "session-42",
                  "subagentId": "subagent-7"
                }
                """));
        Assert.Equal("subagent-7", cancel.SubagentId);
    }

    [Fact]
    public void ParseCommand_MapsWorktreeLifecycleOperations()
    {
        var create = Assert.IsType<WorktreeCreateWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "worktree/create",
                  "workspaceGeneration": 7,
                  "sessionId": "session-42",
                  "copyMode": "dirty",
                  "gitReference": "feature/parser",
                  "copyIgnoredInBackground": true,
                  "ignoredSkipPatterns": ["target/**", ".cache/**"],
                  "creationType": "linked",
                  "label": "Parser experiment",
                  "destinationPath": "C:\\repo\\.worktrees\\parser"
                }
                """));
        Assert.Equal(7, create.WorkspaceGeneration);
        Assert.Equal("session-42", create.SessionId);
        Assert.Equal(WorktreeCopyMode.Dirty, create.CopyMode);
        Assert.Equal("feature/parser", create.GitReference);
        Assert.True(create.CopyIgnoredInBackground);
        Assert.Equal(["target/**", ".cache/**"], create.IgnoredSkipPatterns);
        Assert.Equal(WorktreeCreationType.Linked, create.CreationType);
        Assert.Equal("Parser experiment", create.Label);
        Assert.Equal("C:\\repo\\.worktrees\\parser", create.DestinationPath);

        var list = Assert.IsType<WorktreeListWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "worktree/list",
                  "workspaceGeneration": 7,
                  "includeAll": true,
                  "types": ["session", "manual"]
                }
                """));
        Assert.True(list.IncludeAll);
        Assert.Equal([WorktreeKind.Session, WorktreeKind.Manual], list.Types);

        var show = Assert.IsType<WorktreeShowWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "worktree/show",
                  "workspaceGeneration": 7,
                  "idOrPath": "worktree-7"
                }
                """));
        Assert.Equal("worktree-7", show.IdOrPath);

        var apply = Assert.IsType<WorktreeApplyWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "worktree/apply",
                  "workspaceGeneration": 7,
                  "sessionId": "session-42",
                  "worktreePath": "C:\\\\repo\\\\.worktrees\\\\parser",
                  "mode": "merge"
                }
                """));
        Assert.Equal(WorktreeApplyMode.Merge, apply.Mode);

        var remove = Assert.IsType<WorktreeRemoveWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "worktree/remove",
                  "workspaceGeneration": 7,
                  "idOrPath": "worktree-7",
                  "force": true,
                  "dryRun": false
                }
                """));
        Assert.True(remove.Force);
        Assert.False(remove.DryRun);

        var gc = Assert.IsType<WorktreeGcWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "worktree/gc",
                  "workspaceGeneration": 7,
                  "dryRun": true,
                  "maximumAgeSeconds": 86400,
                  "force": false
                }
                """));
        Assert.Equal(86400, gc.MaximumAgeSeconds);
        Assert.True(gc.DryRun);
        Assert.False(gc.Force);
    }

    [Theory]
    [InlineData("worktree/create", "\"sessionId\": \"session-42\", \"copyMode\": \"fast\"")]
    [InlineData("worktree/list", "\"includeAll\": false, \"types\": [\"unknown\"]")]
    [InlineData("worktree/apply", "\"sessionId\": \"session-42\", \"worktreePath\": \"C:\\\\repo\", \"mode\": \"rebase\"")]
    [InlineData("worktree/gc", "\"dryRun\": true, \"maximumAgeSeconds\": 0, \"force\": false")]
    [InlineData("worktree/gc", "\"dryRun\": true, \"maximumAgeSeconds\": 315360001, \"force\": false")]
    public void ParseCommand_RejectsNonCanonicalOrOutOfRangeWorktreeFields(
        string type,
        string fields)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "type": "{{type}}",
              "workspaceGeneration": 1,
              {{fields}}
            }
            """;

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Fact]
    public void ParseCommand_RejectsOversizedOrUnknownWorktreeFields()
    {
        var longLabel = new string('x', 257);
        var longPattern = new string('x', 1025);
        var tooManyPatterns = Enumerable.Range(0, 257).Select(index => $"pattern-{index}");

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand($$"""
            {
              "schemaVersion": 1,
              "type": "worktree/create",
              "workspaceGeneration": 1,
              "sessionId": "session-42",
              "copyMode": "dirty",
              "copyIgnoredInBackground": false,
              "ignoredSkipPatterns": [],
              "label": {{JsonSerializer.Serialize(longLabel)}}
            }
            """));
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand($$"""
            {
              "schemaVersion": 1,
              "type": "worktree/create",
              "workspaceGeneration": 1,
              "sessionId": "session-42",
              "copyMode": "dirty",
              "copyIgnoredInBackground": false,
              "ignoredSkipPatterns": [{{JsonSerializer.Serialize(longPattern)}}]
            }
            """));
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand($$"""
            {
              "schemaVersion": 1,
              "type": "worktree/create",
              "workspaceGeneration": 1,
              "sessionId": "session-42",
              "copyMode": "dirty",
              "copyIgnoredInBackground": false,
              "ignoredSkipPatterns": {{JsonSerializer.Serialize(tooManyPatterns)}},
              "unexpected": true
            }
            """));
    }

    [Fact]
    public void ParseCommand_MissingNativeRiskAcknowledgementFailsClosed()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "type": "engine/prompt",
              "text": "检查当前改动",
              "executionProfile": "NativeProtected",
              "sessionMode": "default",
              "workspaceGeneration": 0
            }
            """;

        var command = Assert.IsType<PromptWebCommand>(WebMessageProtocol.ParseCommand(json));

        Assert.False(command.NativeRiskAcknowledged);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Plan")]
    [InlineData(" plan")]
    [InlineData("plan ")]
    [InlineData("browser_use")]
    public void ParseCommand_RejectsMissingOrNonCanonicalSessionModes(string? mode)
    {
        var modeField = mode is null
            ? string.Empty
            : $", \"sessionMode\": {JsonSerializer.Serialize(mode)}";
        var json = $$"""
            {
              "schemaVersion": 1,
              "type": "engine/prompt",
              "text": "inspect",
              "executionProfile": "NativeProtected",
              "nativeRiskAcknowledged": true,
              "workspaceGeneration": 0{{modeField}}
            }
            """;

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParseCommand_MapsNativeModalState(bool isOpen)
    {
        var command = Assert.IsType<ModalStateWebCommand>(
            WebMessageProtocol.ParseCommand($$"""
                {
                  "schemaVersion": 1,
                  "type": "ui/modal",
                  "isOpen": {{isOpen.ToString().ToLowerInvariant()}}
                }
                """));

        Assert.Equal(isOpen, command.IsOpen);
    }

    [Fact]
    public void ParseCommand_MissingWorkspaceGenerationFailsClosed()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "type": "engine/prompt",
              "text": "检查当前改动",
              "executionProfile": "NativeProtected",
              "nativeRiskAcknowledged": true
            }
            """;

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"0\"")]
    [InlineData("null")]
    public void ParseCommand_RejectsInvalidWorkspaceGeneration(string rawValue)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "type": "engine/prompt",
              "text": "检查当前改动",
              "executionProfile": "NativeProtected",
              "nativeRiskAcknowledged": true,
              "workspaceGeneration": {{rawValue}}
            }
            """;

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("00")]
    [InlineData("+0")]
    [InlineData("1")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData(" NativeProtected ")]
    [InlineData("WslStrict ")]
    [InlineData("nativeprotected")]
    [InlineData("wslstrict")]
    public void ParseCommand_RejectsNonCanonicalExecutionProfiles(string profileName)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "type": "engine/prompt",
              "text": "检查当前改动",
              "executionProfile": {{JsonSerializer.Serialize(profileName)}},
              "nativeRiskAcknowledged": true,
              "workspaceGeneration": 0
            }
            """;

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void ParseCommand_RejectsNonBooleanNativeRiskAcknowledgement(string rawValue)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "type": "engine/prompt",
              "text": "检查当前改动",
              "executionProfile": "NativeProtected",
              "nativeRiskAcknowledged": {{rawValue}},
              "workspaceGeneration": 0
            }
            """;

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Fact]
    public void ParseCommand_RejectsCredentialSaveCommandsFromTheWebView()
    {
        Assert.Throws<InvalidDataException>(() =>
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "credential/save",
                  "provider": "xai",
                  "secret": "must-never-cross-the-webview-boundary"
                }
                """));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ParseCommand_MapsProviderCredentialIntentWithoutSecretMaterial(
        bool useExistingCredential,
        bool replaceCredential)
    {
        var command = Assert.IsType<SaveProviderWebCommand>(
            WebMessageProtocol.ParseCommand($$"""
                {
                  "schemaVersion": 1,
                  "type": "provider/save",
                  "baseUrl": "https://example.com/v1/",
                  "model": "grok-4.5",
                  "backend": "chat_completions",
                  "allowInsecureTransport": false,
                  "useExistingCredential": {{JsonSerializer.Serialize(useExistingCredential)}},
                  "replaceCredential": {{JsonSerializer.Serialize(replaceCredential)}}
                }
                """));

        Assert.Equal("https://example.com/v1", command.BaseUrl);
        Assert.Equal("grok-4.5", command.Model);
        Assert.Equal("chat_completions", command.Backend);
        Assert.False(command.AllowInsecureTransport);
        Assert.Equal(useExistingCredential, command.UseExistingCredential);
        Assert.Equal(replaceCredential, command.ReplaceCredential);
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("apiKey")]
    public void ParseCommand_RejectsProviderCredentialMaterial(string fieldName)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "type": "provider/save",
              "baseUrl": "https://example.com/v1",
              "model": "grok-4.5",
              "backend": "chat_completions",
              "allowInsecureTransport": false,
              "useExistingCredential": false,
              "replaceCredential": true,
              "{{fieldName}}": "must-never-cross-the-webview-boundary"
            }
            """;

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"useExistingCredential\":true")]
    [InlineData(",\"replaceCredential\":true")]
    [InlineData(",\"useExistingCredential\":true,\"replaceCredential\":true")]
    [InlineData(",\"useExistingCredential\":false,\"replaceCredential\":false")]
    [InlineData(",\"useExistingCredential\":\"true\",\"replaceCredential\":false")]
    [InlineData(",\"useExistingCredential\":true,\"replaceCredential\":0")]
    [InlineData(",\"useExistingCredential\":true,\"useExistingCredential\":false,\"replaceCredential\":false")]
    public void ParseCommand_RejectsInvalidProviderCredentialIntent(string intentFields)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "type": "provider/save",
              "baseUrl": "https://example.com/v1",
              "model": "grok-4.5",
              "backend": "chat_completions",
              "allowInsecureTransport": false{{intentFields}}
            }
            """;

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"false\"")]
    [InlineData("0")]
    [InlineData("{}")]
    public void ParseCommand_RejectsNonBooleanProviderTransportOptIn(string rawValue)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "type": "provider/save",
              "baseUrl": "https://example.com/v1",
              "model": "grok-4.5",
              "backend": "chat_completions",
              "allowInsecureTransport": {{rawValue}},
              "useExistingCredential": false,
              "replaceCredential": true
            }
            """;

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Fact]
    public void ParseCommand_MapsProviderProfileAndReplacementIntent()
    {
        var command = Assert.IsType<SaveProviderWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "provider/save",
                  "baseUrl": "https://EXAMPLE.com:443/v1/",
                  "model": "grok-4.5",
                  "backend": "chat_completions",
                  "allowInsecureTransport": false,
                  "useExistingCredential": false,
                  "replaceCredential": true
                }
                """));

        Assert.Equal("https://example.com/v1", command.Profile.BaseUrl);
        Assert.Equal("grok-4.5", command.Profile.Model);
        Assert.Equal(ProviderBackend.ChatCompletions, command.Profile.Backend);
        Assert.False(command.Profile.AllowInsecureTransport);
        Assert.False(command.UseExistingCredential);
        Assert.True(command.ReplaceCredential);

        var localProvider = Assert.IsType<SaveProviderWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "provider/save",
                  "baseUrl": "http://localhost:8080/v1",
                  "model": "local-model",
                  "backend": "chat_completions",
                  "allowInsecureTransport": true,
                  "useExistingCredential": true,
                  "replaceCredential": false
                }
                """));
        Assert.True(localProvider.UseExistingCredential);
        Assert.False(localProvider.ReplaceCredential);
        Assert.Equal(ProviderBackend.ChatCompletions, localProvider.Profile.Backend);
        Assert.True(localProvider.Profile.CanSendCredentials);
    }

    [Fact]
    public void ParseCommand_MapsResponsesProviderBackend()
    {
        var command = Assert.IsType<SaveProviderWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "provider/save",
                  "baseUrl": "https://example.com/v1",
                  "model": "grok-4.5",
                  "backend": "responses",
                  "allowInsecureTransport": false,
                  "useExistingCredential": true,
                  "replaceCredential": false
                }
                """));

        Assert.Equal(ProviderBackend.Responses, command.Profile.Backend);
        Assert.Equal("responses", command.Backend);
        Assert.True(command.UseExistingCredential);
        Assert.False(command.ReplaceCredential);
    }

    [Fact]
    public void ParseCommand_MapsEveryCloudDesktopCommandWithoutSecretMaterial()
    {
        const string requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";
        (string Type, string Fields, string ExpectedType)[] cases =
        [
            ("cloud/profile/get", "", "CloudProfileGetWebCommand"),
            ("cloud/profile/save-local", "", "CloudProfileSaveLocalWebCommand"),
            ("cloud/profile/save-remote",
                ",\"baseUri\":\"https://cloud.example.test/\",\"teamId\":\"team-1\",\"deviceId\":\"device-1\"",
                "CloudProfileSaveRemoteWebCommand"),
            ("cloud/pairing/export", "", "CloudPairingExportWebCommand"),
            ("cloud/pairing/import", "", "CloudPairingImportWebCommand"),
            ("cloud/session/upload", ",\"sessionId\":\"session-1\"", "CloudSessionUploadWebCommand"),
            ("cloud/session/download", ",\"remoteSessionId\":\"session-1\"", "CloudSessionDownloadWebCommand"),
            ("cloud/session/delete", ",\"remoteSessionId\":\"session-1\"", "CloudSessionDeleteWebCommand"),
            ("cloud/session/export", ",\"sessionId\":\"session-1\"", "CloudSessionExportWebCommand"),
            ("cloud/handoff/create",
                ",\"sessionId\":\"session-1\",\"targetDeviceId\":\"device-2\"",
                "CloudHandoffCreateWebCommand"),
            ("cloud/handoff/receive", "", "CloudHandoffReceiveWebCommand"),
            ("cloud/policy/get", "", "CloudPolicyGetWebCommand"),
            ("cloud/policy/update",
                ",\"allowedExecutionProfiles\":[\"NativeProtected\"],\"remoteRunnerEnabled\":true,\"uiAutomationEnabled\":false,\"maximumConcurrentJobs\":2,\"allowedPluginPublishers\":[\"publisher-1\"]",
                "CloudPolicyUpdateWebCommand"),
            ("cloud/runner/register",
                ",\"runnerId\":\"runner-1\",\"capabilities\":[\"windows\"]",
                "CloudRunnerRegisterWebCommand"),
            ("cloud/automation/list", "", "CloudAutomationListWebCommand"),
            ("cloud/automation/disable",
                ",\"automationId\":\"automation-1\"",
                "CloudAutomationDisableWebCommand"),
        ];

        foreach (var (type, fields, expectedType) in cases)
        {
            var command = WebMessageProtocol.ParseCommand(
                $"{{\"schemaVersion\":1,\"type\":\"{type}\",\"requestId\":\"{requestId}\"{fields}}}");
            Assert.Equal(expectedType, command.GetType().Name);
        }

        var remoteProfile =
            $"{{\"schemaVersion\":1,\"type\":\"cloud/profile/save-remote\",\"requestId\":\"{requestId}\",\"baseUri\":\"https://cloud.example.test/\",\"teamId\":\"team-1\",\"deviceId\":\"device-1\",\"accessToken\":\"must-not-cross-web\"}}";
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(remoteProfile));
        var exportWithDocument =
            $"{{\"schemaVersion\":1,\"type\":\"cloud/session/export\",\"requestId\":\"{requestId}\",\"sessionId\":\"session-1\",\"document\":{{\"secret\":true}}}}";
        Assert.Throws<InvalidDataException>(
            () => WebMessageProtocol.ParseCommand(exportWithDocument));
    }

    [Fact]
    public void SerializeEvent_ProjectsCloudDeleteAndExportWithoutDocumentOrPath()
    {
        const string requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";
        var deleted = WebMessageProtocol.SerializeEvent(new CloudSessionDeletedWebEvent(
            requestId,
            "remote-session-1",
            Found: true,
            Revision: 7));
        var exported = WebMessageProtocol.SerializeEvent(new CloudSessionExportedWebEvent(
            requestId,
            "local-session-1",
            "safe.agentdesk-session.json"));

        Assert.Contains("cloud/session/deleted", deleted, StringComparison.Ordinal);
        Assert.Contains("cloud/session/exported", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("document", deleted + exported, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", deleted + exported, StringComparison.OrdinalIgnoreCase);
        _ = WebMessageProtocol.SerializeEvent(
            new CloudCancelledWebEvent(requestId, "session-export"));
    }

    [Fact]
    public void ParseCommand_RejectsMorePublishersThanTheCloudPolicyContractAllows()
    {
        var publishers = Enumerable.Range(0, 129)
            .Select(index => $"publisher-{index:D3}")
            .ToArray();
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            type = "cloud/policy/update",
            requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d",
            allowedExecutionProfiles = new[] { "NativeProtected" },
            remoteRunnerEnabled = false,
            uiAutomationEnabled = false,
            maximumConcurrentJobs = 1,
            allowedPluginPublishers = publishers,
        });

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Fact]
    public void SerializeEvent_AllowsTheInitialCloudPolicyVersionZero()
    {
        var json = WebMessageProtocol.SerializeEvent(new CloudPolicyWebEvent(
            "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d",
            Version: 0,
            AllowedExecutionProfiles: ["NativeProtected"],
            RemoteRunnerEnabled: false,
            UiAutomationEnabled: false,
            MaximumConcurrentJobs: 1,
            AllowedPluginPublishers: []));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(0, document.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public void ParseCommand_MapsRunnerTaskAndAutomationCreateCommandsWithoutEnvelopes()
    {
        const string requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";
        var queue = Assert.IsType<CloudRunnerQueueWebCommand>(
            WebMessageProtocol.ParseCommand($$"""
                {"schemaVersion":1,"type":"cloud/runner/queue","requestId":"{{requestId}}","requiredCapability":"windows","task":"inspect workspace"}
                """));
        Assert.Equal("inspect workspace", queue.Task);

        var claim = Assert.IsType<CloudRunnerClaimWebCommand>(
            WebMessageProtocol.ParseCommand($$"""
                {"schemaVersion":1,"type":"cloud/runner/claim","requestId":"{{requestId}}","runnerId":"runner-1","leaseSeconds":30}
                """));
        Assert.Equal(30, claim.LeaseSeconds);

        var complete = Assert.IsType<CloudRunnerCompleteWebCommand>(
            WebMessageProtocol.ParseCommand($$"""
                {"schemaVersion":1,"type":"cloud/runner/complete","requestId":"{{requestId}}","claimHandle":"claim-1","jobId":"job-1","result":"done"}
                """));
        Assert.Equal("claim-1", complete.ClaimHandle);
        Assert.Equal("done", complete.Result);

        var create = Assert.IsType<CloudAutomationCreateWebCommand>(
            WebMessageProtocol.ParseCommand($$"""
                {"schemaVersion":1,"type":"cloud/automation/create","requestId":"{{requestId}}","name":"nightly","intervalSeconds":3600,"requiredCapability":"windows","task":"review branch"}
                """));
        Assert.Equal("review branch", create.Task);

        var oversized = $$"""
            {"schemaVersion":1,"type":"cloud/runner/queue","requestId":"{{requestId}}","requiredCapability":"windows","task":"{{new string('x', 65537)}}"}
            """;
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(oversized));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(601)]
    [InlineData(3_600)]
    public void ParseCommand_RejectsRunnerLeaseSecondsOutsideServerRange(int leaseSeconds)
    {
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand($$"""
            {"schemaVersion":1,"type":"cloud/runner/claim","requestId":"5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d","runnerId":"runner-1","leaseSeconds":{{leaseSeconds}}}
            """));
    }

    [Fact]
    public void SerializeEvent_ProjectsRunnerAndAutomationSummariesWithoutCiphertext()
    {
        const string requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";
        var automation = new CloudAutomationWebSummary(
            "automation-1",
            "nightly",
            3600,
            true,
            DateTimeOffset.Parse("2026-07-19T00:00:00Z"));
        WebEvent[] events =
        [
            new CloudRunnerQueuedWebEvent(requestId, "job-1"),
            new CloudRunnerClaimedWebEvent(
                requestId,
                true,
                "job-1",
                "windows",
                "inspect",
                DateTimeOffset.UtcNow,
                "claim-1"),
            new CloudRunnerCompletedWebEvent(requestId, "claim-1", "job-1"),
            new CloudAutomationCreatedWebEvent(requestId, automation),
        ];

        var json = string.Join('\n', events.Select(WebMessageProtocol.SerializeEvent));
        Assert.DoesNotContain("ciphertext", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nonce", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("leaseToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claimHandle", json, StringComparison.Ordinal);
        Assert.Contains("job-1", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ChatCompletions")]
    [InlineData("chat-completions")]
    [InlineData("unknown")]
    [InlineData("")]
    public void ParseCommand_RejectsNonCanonicalProviderBackends(string backend)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "type": "provider/save",
              "baseUrl": "https://example.com/v1",
              "model": "grok-4.5",
              "backend": {{JsonSerializer.Serialize(backend)}},
              "allowInsecureTransport": false,
              "useExistingCredential": true,
              "replaceCredential": false
            }
            """;

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand(json));
    }

    [Fact]
    public void SerializeEvent_ProviderStatusNeverContainsTheCredential()
    {
        var profile = new ProviderProfile("https://example.com/v1", "grok-4.5");
        var json = WebMessageProtocol.SerializeEvent(
            new ProviderStatusWebEvent("saved", profile, HasCredential: true));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("provider/status", root.GetProperty("type").GetString());
        Assert.Equal("https://example.com/v1", root.GetProperty("baseUrl").GetString());
        Assert.Equal("grok-4.5", root.GetProperty("model").GetString());
        Assert.Equal("chat_completions", root.GetProperty("backend").GetString());
        Assert.True(root.GetProperty("hasCredential").GetBoolean());
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profile.CredentialName, json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeEvent_UsesTheVersionedCamelCaseEnvelope()
    {
        var json = WebMessageProtocol.SerializeEvent(
            new EngineStatusWebEvent("running", "正在运行", "session-42"));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("engine/status", root.GetProperty("type").GetString());
        Assert.Equal("running", root.GetProperty("status").GetString());
        Assert.Equal("正在运行", root.GetProperty("message").GetString());
        Assert.Equal("session-42", root.GetProperty("sessionId").GetString());
    }

    [Fact]
    public void SerializeEvent_PreservesNotificationAndAutomationPreferences()
    {
        var preferences = new UiPreferences(
            "en-US",
            "continue the review",
            SessionMode.Plan,
            ExecutionProfile.WslStrict,
            NotificationsEnabled: true,
            WindowsAutomationEnabled: true,
            BackgroundUpdateChecksEnabled: true);

        var json = WebMessageProtocol.SerializeEvent(
            new UiPreferencesChangedWebEvent(preferences));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("ui/preferences/changed", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("notificationsEnabled").GetBoolean());
        Assert.True(root.GetProperty("windowsAutomationEnabled").GetBoolean());
        Assert.True(root.GetProperty("backgroundUpdateChecksEnabled").GetBoolean());
    }

    [Fact]
    public void SerializeEvent_MapsAvailableExecutionProfilesAsCapabilities()
    {
        var json = WebMessageProtocol.SerializeEvent(
            new EngineStatusWebEvent(
                "idle",
                ExecutionProfiles:
                [ExecutionProfile.NativeProtected, ExecutionProfile.WslStrict]));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            ["NativeProtected", "WslStrict"],
            document.RootElement.GetProperty("capabilities")
                .GetProperty("executionProfiles")
                .EnumerateArray()
                .Select(item => item.GetString()));
    }

    [Fact]
    public void SerializeEvent_MapsAuthoritativeEnginePromptCapabilities()
    {
        var json = WebMessageProtocol.SerializeEvent(new EngineCapabilitiesChangedWebEvent(
            "session-42",
            ImagePrompts: true,
            [SessionMode.Default, SessionMode.Plan]));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("engine/capabilities", root.GetProperty("type").GetString());
        Assert.Equal("session-42", root.GetProperty("sessionId").GetString());
        Assert.True(root.GetProperty("imagePrompts").GetBoolean());
        Assert.Equal(
            ["default", "plan"],
            root.GetProperty("sessionModes").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void SerializeEvent_MapsAuthoritativeSessionModeState()
    {
        var json = WebMessageProtocol.SerializeEvent(
            new SessionModeChangedWebEvent(
                "session-42",
                SessionMode.Plan,
                PlanAvailable: true));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("session/mode/changed", root.GetProperty("type").GetString());
        Assert.Equal("session-42", root.GetProperty("sessionId").GetString());
        Assert.Equal("plan", root.GetProperty("mode").GetString());
        Assert.True(root.GetProperty("planAvailable").GetBoolean());
    }

    [Fact]
    public void SerializeEvent_MapsSessionCatalogAndActiveSessionState()
    {
        var summary = new SessionSummary(
            new SessionId("session-42"),
            "Fix the parser",
            "C:\\repo",
            DateTimeOffset.Parse("2026-07-16T08:00:00Z"),
            DateTimeOffset.Parse("2026-07-16T09:30:00Z"),
            12,
            ModelId: "grok-4.5",
            Branch: "feature/parser");
        var json = WebMessageProtocol.SerializeEvent(
            new SessionListChangedWebEvent(
                [summary],
                "cursor-2",
                "11111111-1111-4111-8111-111111111111"));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("session/list/changed", root.GetProperty("type").GetString());
        Assert.Equal(
            "11111111-1111-4111-8111-111111111111",
            root.GetProperty("requestId").GetString());
        Assert.Equal("cursor-2", root.GetProperty("nextCursor").GetString());
        var item = Assert.Single(root.GetProperty("sessions").EnumerateArray());
        Assert.Equal("session-42", item.GetProperty("sessionId").GetString());
        Assert.Equal("Fix the parser", item.GetProperty("title").GetString());
        Assert.Equal("C:\\repo", item.GetProperty("workspacePath").GetString());
        Assert.Equal(12, item.GetProperty("messageCount").GetInt32());
        Assert.Equal("grok-4.5", item.GetProperty("modelId").GetString());

        var activeJson = WebMessageProtocol.SerializeEvent(
            new SessionActiveChangedWebEvent("session-42", "C:\\repo"));
        using var activeDocument = JsonDocument.Parse(activeJson);
        Assert.Equal(
            "session/active/changed",
            activeDocument.RootElement.GetProperty("type").GetString());
        Assert.True(activeDocument.RootElement.TryGetProperty("engineEpoch", out _));

        var archivedJson = WebMessageProtocol.SerializeEvent(
            new SessionArchiveChangedWebEvent(
                "33333333-3333-4333-8333-333333333333",
                "session-42",
                Archived: true));
        using var archivedDocument = JsonDocument.Parse(archivedJson);
        Assert.Equal(
            "session/archive/changed",
            archivedDocument.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "33333333-3333-4333-8333-333333333333",
            archivedDocument.RootElement.GetProperty("requestId").GetString());
        Assert.True(archivedDocument.RootElement.GetProperty("archived").GetBoolean());
    }

    [Fact]
    public void SerializeEvent_MapsSessionUpdateEngineEpoch()
    {
        var json = WebMessageProtocol.SerializeEvent(
            new SessionUpdateWebEvent(
                "session-42",
                "agent_message_chunk",
                "hello"));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("session/update", root.GetProperty("type").GetString());
        Assert.True(root.TryGetProperty("engineEpoch", out _));
    }

    [Fact]
    public void SerializeEvent_MapsSessionListFailureCorrelation()
    {
        const string requestId = "11111111-1111-4111-8111-111111111111";
        var json = WebMessageProtocol.SerializeEvent(
            new SessionListErrorWebEvent(requestId, "Unable to list sessions."));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("session/list/error", root.GetProperty("type").GetString());
        Assert.Equal(requestId, root.GetProperty("requestId").GetString());
        Assert.Equal("Unable to list sessions.", root.GetProperty("message").GetString());
    }

    [Fact]
    public void SerializeEvent_MapsSessionHistoryOperationResults()
    {
        var forkedJson = WebMessageProtocol.SerializeEvent(
            new SessionForkedWebEvent(
                new SessionForkResult(
                    new SessionId("fork-session"),
                    "C:\\repo",
                    "session-42",
                    8,
                    20,
                    PlanStateCopied: true,
                    ModelId: "grok-4.5")));
        using var forked = JsonDocument.Parse(forkedJson);
        Assert.Equal("session/forked", forked.RootElement.GetProperty("type").GetString());
        Assert.Equal("fork-session", forked.RootElement.GetProperty("sessionId").GetString());

        var pointsJson = WebMessageProtocol.SerializeEvent(
            new SessionRewindPointsWebEvent(
                "session-42",
                [
                    new SessionRewindPoint(
                        3,
                        DateTimeOffset.Parse("2026-07-16T09:30:00Z"),
                        2,
                        HasFileChanges: true,
                        PromptPreview: "Refactor parser"),
                ]));
        using var points = JsonDocument.Parse(pointsJson);
        Assert.Equal(
            3,
            points.RootElement.GetProperty("points")[0].GetProperty("promptIndex").GetInt32());

        var pointsErrorJson = WebMessageProtocol.SerializeEvent(
            new SessionRewindPointsErrorWebEvent(
                "session-42",
                "Rewind checkpoints could not be loaded."));
        using var pointsError = JsonDocument.Parse(pointsErrorJson);
        Assert.Equal(
            "session/rewind/points/error",
            pointsError.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "Rewind checkpoints could not be loaded.",
            pointsError.RootElement.GetProperty("message").GetString());

        var rewoundJson = WebMessageProtocol.SerializeEvent(
            new SessionRewoundWebEvent(
                "session-42",
                new SessionRewindResult(
                    Success: true,
                    3,
                    SessionRewindMode.ConversationOnly,
                    RevertedFiles: [],
                    CleanFiles: ["src/parser.rs"],
                    Conflicts: [],
                    PromptText: "Refactor parser")));
        using var rewound = JsonDocument.Parse(rewoundJson);
        Assert.Equal("conversation_only", rewound.RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public void SerializeEvent_MapsRuntimeDashboardWithoutNumericEnums()
    {
        var backgroundTask = new BackgroundTaskSnapshot(
            "task-7",
            "wrapped command",
            "dotnet test",
            "C:\\repo",
            DateTimeOffset.Parse("2026-07-17T08:00:00Z"),
            null,
            "running",
            "C:\\temp\\task.log",
            Truncated: false,
            ExitCode: null,
            Signal: null,
            Completed: false,
            BackgroundTaskKind.Bash,
            ExplicitlyKilled: false,
            OwnerSessionId: "session-42");
        var subagent = new SubagentSnapshot(
            "subagent-7",
            "session-42",
            "session-child",
            "worker",
            "Run tests",
            DateTimeOffset.Parse("2026-07-17T08:00:00Z"),
            TimeSpan.FromSeconds(2),
            SubagentStatus.Running,
            TurnCount: 2,
            ToolCallCount: 4,
            TokensUsed: 8192,
            ContextWindowTokens: 131072,
            ContextUsagePercent: 6,
            ToolsUsed: ["shell_command"],
            ErrorCount: 0);

        var json = WebMessageProtocol.SerializeEvent(
            new RuntimeDashboardChangedWebEvent("session-42", [backgroundTask], [subagent]));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("runtime/dashboard/changed", root.GetProperty("type").GetString());
        var task = root.GetProperty("backgroundTasks")[0];
        Assert.Equal("dotnet test", task.GetProperty("command").GetString());
        Assert.Equal("bash", task.GetProperty("kind").GetString());
        var agent = root.GetProperty("subagents")[0];
        Assert.Equal("running", agent.GetProperty("status").GetString());
        Assert.Equal(2000, agent.GetProperty("durationMs").GetInt64());

        var cancelledJson = WebMessageProtocol.SerializeEvent(
            new RuntimeSubagentCancelledWebEvent(
                "session-42",
                "subagent-7",
                new SubagentCancelResult(
                    SubagentCancelOutcome.AlreadyFinished,
                    SubagentStatus.Completed)));
        using var cancelled = JsonDocument.Parse(cancelledJson);
        Assert.Equal(
            "already_finished",
            cancelled.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            "completed",
            cancelled.RootElement.GetProperty("terminalStatus").GetString());

        var errorJson = WebMessageProtocol.SerializeEvent(
            new RuntimeDashboardErrorWebEvent(
                "session-42",
                "Unable to stop task.",
                RuntimeDashboardOperation.TaskKill,
                "task-7"));
        using var error = JsonDocument.Parse(errorJson);
        Assert.Equal("task_kill", error.RootElement.GetProperty("operation").GetString());
        Assert.Equal("task-7", error.RootElement.GetProperty("itemId").GetString());
    }

    [Fact]
    public void SerializeEvent_MapsWorktreeLifecycleWithoutNumericEnums()
    {
        var record = CreateWorktreeRecord();
        var listJson = WebMessageProtocol.SerializeEvent(
            new WorktreeListChangedWebEvent(7, [record]));
        using var list = JsonDocument.Parse(listJson);
        var projectedRecord = Assert.Single(
            list.RootElement.GetProperty("worktrees").EnumerateArray());
        Assert.Equal("worktree/list/changed", list.RootElement.GetProperty("type").GetString());
        Assert.Equal("manual", projectedRecord.GetProperty("kind").GetString());
        Assert.Equal("linked", projectedRecord.GetProperty("creationType").GetString());
        Assert.Equal("alive", projectedRecord.GetProperty("status").GetString());
        Assert.Equal("session-42", projectedRecord.GetProperty("sessionId").GetString());

        var createdJson = WebMessageProtocol.SerializeEvent(
            new WorktreeCreatedWebEvent(
                7,
                new WorktreeCreateResult(
                    WorktreeCreateStatus.Creating,
                    new SessionId("session-42"),
                    "C:\\repo\\.worktrees\\parser",
                    "C:\\repo",
                    "abc123")));
        using var created = JsonDocument.Parse(createdJson);
        Assert.Equal("worktree/created", created.RootElement.GetProperty("type").GetString());
        Assert.Equal("creating", created.RootElement.GetProperty("status").GetString());

        var detailJson = WebMessageProtocol.SerializeEvent(
            new WorktreeDetailWebEvent(7, record));
        using var detail = JsonDocument.Parse(detailJson);
        Assert.Equal("worktree/detail", detail.RootElement.GetProperty("type").GetString());
        Assert.Equal("worktree-7", detail.RootElement.GetProperty("worktree").GetProperty("id").GetString());

        var appliedJson = WebMessageProtocol.SerializeEvent(
            new WorktreeAppliedWebEvent(
                7,
                new WorktreeApplyResult(
                    WorktreeApplyStatus.Conflicts,
                    [
                        new WorktreeFileChange(
                            "src/parser.rs",
                            null,
                            WorktreeChangeType.Edit,
                            Staged: false,
                            Additions: 4,
                            Deletions: 2,
                            Patch: "@@ parser @@")
                    ],
                    [
                        new WorktreeConflict(
                            "src/parser.rs",
                            WorktreeChangeType.Edit,
                            "base",
                            "ours",
                            "theirs")
                    ],
                    "C:\\repo")));
        using var applied = JsonDocument.Parse(appliedJson);
        Assert.Equal("conflicts", applied.RootElement.GetProperty("status").GetString());
        Assert.Equal("edit", applied.RootElement.GetProperty("files")[0].GetProperty("changeType").GetString());

        var removedJson = WebMessageProtocol.SerializeEvent(
            new WorktreeRemovedWebEvent(
                7,
                "worktree-7",
                new WorktreeRemoveResult(true, "C:\\repo\\.worktrees\\parser")));
        using var removed = JsonDocument.Parse(removedJson);
        Assert.Equal("worktree/removed", removed.RootElement.GetProperty("type").GetString());
        Assert.True(removed.RootElement.GetProperty("removed").GetBoolean());

        var gcJson = WebMessageProtocol.SerializeEvent(
            new WorktreeGcCompletedWebEvent(7, new WorktreeGcResult(1, 2, 3, 4)));
        using var gc = JsonDocument.Parse(gcJson);
        Assert.Equal("worktree/gc/completed", gc.RootElement.GetProperty("type").GetString());
        Assert.Equal(4UL, gc.RootElement.GetProperty("removeFailed").GetUInt64());

        var errorJson = WebMessageProtocol.SerializeEvent(
            new WorktreeErrorWebEvent(7, "Unable to apply worktree.", WorktreeOperation.Apply, "worktree-7"));
        using var error = JsonDocument.Parse(errorJson);
        Assert.Equal("worktree/error", error.RootElement.GetProperty("type").GetString());
        Assert.Equal("apply", error.RootElement.GetProperty("operation").GetString());
        Assert.Equal("worktree-7", error.RootElement.GetProperty("itemId").GetString());
    }

    [Fact]
    public void SerializeEvent_RejectsWorktreePayloadsAboveProtocolBudgets()
    {
        var record = CreateWorktreeRecord();
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.SerializeEvent(
            new WorktreeListChangedWebEvent(1, Enumerable.Repeat(record, 4097).ToArray())));

        var oversizedText = new string('x', (2 * 1024 * 1024) + 1);
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.SerializeEvent(
            new WorktreeAppliedWebEvent(
                1,
                new WorktreeApplyResult(
                    WorktreeApplyStatus.Success,
                    [
                        new WorktreeFileChange(
                            "src/parser.rs",
                            null,
                            WorktreeChangeType.Edit,
                            Staged: false,
                            Additions: 1,
                            Deletions: 0,
                            Patch: oversizedText)
                    ],
                    []))));
    }

    [Fact]
    public void SerializeEvent_MapsProviderStatusWithoutCredentialMaterial()
    {
        var json = WebMessageProtocol.SerializeEvent(
            new ProviderStatusWebEvent(
                "saved",
                "https://example.com/v1",
                "grok-4.5",
                "chat_completions",
                AllowInsecureTransport: false));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("provider/status", root.GetProperty("type").GetString());
        Assert.Equal("saved", root.GetProperty("status").GetString());
        Assert.Equal("https://example.com/v1", root.GetProperty("baseUrl").GetString());
        Assert.Equal("grok-4.5", root.GetProperty("model").GetString());
        Assert.Equal("chat_completions", root.GetProperty("backend").GetString());
        Assert.False(root.GetProperty("allowInsecureTransport").GetBoolean());
        Assert.False(root.TryGetProperty("secret", out _));
        Assert.False(root.TryGetProperty("apiKey", out _));
        Assert.False(root.TryGetProperty("credentialName", out _));
    }

    [Theory]
    [InlineData("focus-window", WindowsAutomationAction.FocusWindow, null, null, null)]
    [InlineData("invoke", WindowsAutomationAction.Invoke, "RunButton", null, null)]
    [InlineData("set-value", WindowsAutomationAction.SetValue, "SearchBox", "Search", "query")]
    public void ParseCommand_MapsWindowsAutomationCommands(
        string action,
        WindowsAutomationAction expectedAction,
        string? automationId,
        string? name,
        string? value)
    {
        var payload = new Dictionary<string, object>
        {
            ["schemaVersion"] = 1,
            ["type"] = "windows/automation/execute",
            ["requestId"] = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d",
            ["action"] = action,
            ["processId"] = 4242,
        };
        if (automationId is not null)
        {
            payload["automationId"] = automationId;
        }
        if (name is not null)
        {
            payload["name"] = name;
        }
        if (value is not null)
        {
            payload["value"] = value;
        }
        var command = Assert.IsType<WindowsAutomationWebCommand>(
            WebMessageProtocol.ParseCommand(JsonSerializer.Serialize(payload)));

        Assert.Equal("5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d", command.RequestId);
        Assert.Equal(expectedAction, command.Request.Action);
        Assert.Equal(4242, command.Request.ProcessId);
        Assert.Equal(automationId, command.Request.AutomationId);
        Assert.Equal(name, command.Request.Name);
        Assert.Equal(value, command.Request.Value);
    }

    [Theory]
    [InlineData("unknown", 4242, "RunButton", null, null)]
    [InlineData("invoke", 0, "RunButton", null, null)]
    [InlineData("invoke", 4242, null, null, null)]
    [InlineData("invoke", 4242, "RunButton", null, "not-allowed")]
    [InlineData("set-value", 4242, "SearchBox", null, null)]
    public void ParseCommand_RejectsInvalidWindowsAutomationCommandsWithoutEchoingValues(
        string action,
        int processId,
        string? automationId,
        string? name,
        string? value)
    {
        const string sensitiveValue = "must-not-appear-in-errors";
        var payload = new Dictionary<string, object>
        {
            ["schemaVersion"] = 1,
            ["type"] = "windows/automation/execute",
            ["requestId"] = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d",
            ["action"] = action,
            ["processId"] = processId,
        };
        if (automationId is not null)
        {
            payload["automationId"] = automationId;
        }
        if (name is not null)
        {
            payload["name"] = name;
        }
        if (value is not null)
        {
            payload["value"] = value == "not-allowed" ? sensitiveValue : value;
        }
        var json = JsonSerializer.Serialize(payload);

        var error = Assert.Throws<InvalidDataException>(() =>
            WebMessageProtocol.ParseCommand(json));

        Assert.DoesNotContain(sensitiveValue, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCommand_RejectsUnknownWindowsAutomationFields()
    {
        Assert.Throws<InvalidDataException>(() =>
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "windows/automation/execute",
                  "requestId": "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d",
                  "action": "focus-window",
                  "processId": 4242,
                  "command": "must-not-be-accepted"
                }
                """));
    }

    [Fact]
    public void SerializeEvent_MapsWindowsAutomationOutcomesWithoutInputValues()
    {
        WebEvent[] events =
        [
            new WindowsAutomationCompletedWebEvent(
                "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d",
                WindowsAutomationAction.SetValue,
                4242,
                "SearchBox"),
            new WindowsAutomationCancelledWebEvent(
                "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d"),
            new WindowsAutomationErrorWebEvent(
                "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d",
                "disabled"),
        ];

        var completed = JsonDocument.Parse(WebMessageProtocol.SerializeEvent(events[0]));
        Assert.Equal(
            "windows/automation/completed",
            completed.RootElement.GetProperty("type").GetString());
        Assert.Equal("set-value", completed.RootElement.GetProperty("action").GetString());
        Assert.Equal(4242, completed.RootElement.GetProperty("processId").GetInt32());
        Assert.Equal("SearchBox", completed.RootElement.GetProperty("target").GetString());
        Assert.False(completed.RootElement.TryGetProperty("value", out _));

        var cancelled = JsonDocument.Parse(WebMessageProtocol.SerializeEvent(events[1]));
        Assert.Equal(
            "windows/automation/cancelled",
            cancelled.RootElement.GetProperty("type").GetString());

        var error = JsonDocument.Parse(WebMessageProtocol.SerializeEvent(events[2]));
        Assert.Equal(
            "windows/automation/error",
            error.RootElement.GetProperty("type").GetString());
        Assert.Equal("disabled", error.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void ParseCommand_MapsSelectedAndCancelledPermissionResponses()
    {
        var selected = Assert.IsType<PermissionRespondWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "permission/respond",
                  "requestId": "permission-1",
                  "outcome": "selected",
                  "optionId": "allow-once"
                }
                """));
        Assert.Equal("permission-1", selected.RequestId);
        Assert.Equal(PermissionDecisionKind.Selected, selected.Decision.Kind);
        Assert.Equal("allow-once", selected.Decision.OptionId);

        var cancelled = Assert.IsType<PermissionRespondWebCommand>(
            WebMessageProtocol.ParseCommand(
                """
                {
                  "schemaVersion": 1,
                  "type": "permission/respond",
                  "requestId": "permission-2",
                  "outcome": "cancelled"
                }
                """));
        Assert.Equal(PermissionDecision.Cancelled, cancelled.Decision);
    }

    [Theory]
    [InlineData("selected", null)]
    [InlineData("approve", "allow-once")]
    public void ParseCommand_RejectsInvalidPermissionResponses(
        string outcome,
        string? optionId)
    {
        var optionField = optionId is null
            ? string.Empty
            : $", \"optionId\": {JsonSerializer.Serialize(optionId)}";

        Assert.Throws<InvalidDataException>(
            () => WebMessageProtocol.ParseCommand(
                $$"""
                {
                  "schemaVersion": 1,
                  "type": "permission/respond",
                  "requestId": "permission-1",
                  "outcome": "{{outcome}}"{{optionField}}
                }
                """));
    }

    [Fact]
    public void SerializeEvent_MapsPermissionOptionsWithoutNumericEnums()
    {
        using var rawInputDocument = JsonDocument.Parse(
            "{ \"command\": \"pwsh -File install.ps1\", \"timeoutMs\": 30000 }");
        var json = WebMessageProtocol.SerializeEvent(
            new PermissionRequestedWebEvent(
                "permission-1",
                "session-42",
                "tool-7",
                "写入 README.md",
                [
                    new PermissionOption(
                        "allow-once",
                        "允许一次",
                        PermissionOptionKind.AllowOnce),
                    new PermissionOption(
                        "reject-always",
                        "始终拒绝",
                        PermissionOptionKind.RejectAlways),
                ],
                ["C:\\work\\README.md:12"],
                ToolKind: "execute",
                RawInput: rawInputDocument.RootElement.Clone()));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("permission/requested", root.GetProperty("type").GetString());
        Assert.Equal("permission-1", root.GetProperty("requestId").GetString());
        Assert.Equal("session-42", root.GetProperty("sessionId").GetString());
        Assert.Equal("tool-7", root.GetProperty("toolCallId").GetString());
        Assert.Equal("写入 README.md", root.GetProperty("title").GetString());
        Assert.Equal("execute", root.GetProperty("toolKind").GetString());
        Assert.Equal(
            "pwsh -File install.ps1",
            root.GetProperty("rawInput").GetProperty("command").GetString());
        Assert.Equal(
            ["allow_once", "reject_always"],
            root.GetProperty("options")
                .EnumerateArray()
                .Select(option => option.GetProperty("kind").GetString()));
        Assert.Equal(
            "C:\\work\\README.md:12",
            Assert.Single(root.GetProperty("locations").EnumerateArray()).GetString());
    }

    [Fact]
    public void ParseCommand_MapsMaintenanceCommandsWithoutAcceptingNativePaths()
    {
        const string requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";

        var export = Assert.IsType<SessionExportWebCommand>(WebMessageProtocol.ParseCommand($$"""
            {
              "schemaVersion": 1,
              "type": "session/export",
              "requestId": "{{requestId}}",
              "sessionId": "session-42"
            }
            """));
        Assert.Equal(requestId, export.RequestId);
        Assert.Equal("session-42", export.SessionId);

        var commands = new (string Type, Type ExpectedType)[]
        {
            ("session/import", typeof(SessionImportWebCommand)),
            ("backup/create", typeof(BackupCreateWebCommand)),
            ("backup/restore", typeof(BackupRestoreWebCommand)),
            ("update/check", typeof(UpdateCheckWebCommand)),
            ("update/apply", typeof(UpdateApplyWebCommand)),
        };

        foreach (var (type, expectedType) in commands)
        {
            var command = WebMessageProtocol.ParseCommand($$"""
                {
                  "schemaVersion": 1,
                  "type": "{{type}}",
                  "requestId": "{{requestId}}"
                }
                """);

            Assert.IsType(expectedType, command);
            Assert.Equal(requestId, Assert.IsAssignableFrom<MaintenanceWebCommand>(command).RequestId);
        }
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("5F70F2BF-C3AD-4A13-9CA0-61B847F52F0D")]
    [InlineData("{5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d}")]
    [InlineData("5f70f2bfc3ad4a139ca061b847f52f0d")]
    public void ParseCommand_RejectsNonCanonicalMaintenanceRequestIds(string requestId)
    {
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand($$"""
            {
              "schemaVersion": 1,
              "type": "update/check",
              "requestId": "{{requestId}}"
            }
            """));
    }

    [Theory]
    [InlineData("session/export", ", \"sessionId\": \"session-42\"")]
    [InlineData("session/import", "")]
    [InlineData("backup/create", "")]
    [InlineData("backup/restore", "")]
    [InlineData("update/check", "")]
    [InlineData("update/apply", "")]
    public void ParseCommand_RejectsNativePathsAndUnknownMaintenanceFields(
        string type,
        string requiredFields)
    {
        const string requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand($$"""
            {
              "schemaVersion": 1,
              "type": "{{type}}",
              "requestId": "{{requestId}}"{{requiredFields}},
              "path": "C:\\private\\session.agentdesk-session"
            }
            """));
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.ParseCommand($$"""
            {
              "schemaVersion": 1,
              "type": "{{type}}",
              "requestId": "{{requestId}}"{{requiredFields}},
              "document": { "secret": "must-not-cross-the-webview-boundary" }
            }
            """));
    }

    [Fact]
    public void SerializeEvent_ProjectsOnlyBoundedMaintenanceMetadata()
    {
        const string requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";
        var cases = new (WebEvent Event, string[] Properties, string Type)[]
        {
            (
                new SessionExportedWebEvent(requestId, "session-42", "session-42.agentdesk-session"),
                ["schemaVersion", "type", "requestId", "sessionId", "fileName"],
                "session/exported"),
            (
                new SessionImportedWebEvent(requestId, "session-imported", "C:\\workspace"),
                ["schemaVersion", "type", "requestId", "sessionId", "workspacePath"],
                "session/imported"),
            (
                new BackupCompletedWebEvent(requestId, "create", 7, 4096, RestartRequired: false),
                ["schemaVersion", "type", "requestId", "operation", "fileCount", "totalBytes", "restartRequired"],
                "backup/completed"),
            (
                new UpdateStatusWebEvent(requestId, "available", Version: "2.0.0"),
                ["schemaVersion", "type", "requestId", "status", "version"],
                "update/status"),
            (
                new MaintenanceErrorWebEvent(requestId, "backup-restore"),
                ["schemaVersion", "type", "requestId", "operation"],
                "maintenance/error"),
            (
                new MaintenanceCancelledWebEvent(requestId, "session-import"),
                ["schemaVersion", "type", "requestId", "operation"],
                "maintenance/cancelled"),
        };

        foreach (var item in cases)
        {
            var json = WebMessageProtocol.SerializeEvent(item.Event);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal(item.Type, root.GetProperty("type").GetString());
            Assert.Equal(requestId, root.GetProperty("requestId").GetString());
            Assert.Equal(
                item.Properties.Order(StringComparer.Ordinal),
                root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.DoesNotContain("message", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"path\":", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("document", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SerializeEvent_RejectsUnsafeMaintenanceMetadata()
    {
        const string requestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.SerializeEvent(
            new SessionExportedWebEvent(requestId, "session-42", "C:\\private\\session.json")));
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.SerializeEvent(
            new SessionExportedWebEvent(requestId, "session-42", "..\\session.json")));
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.SerializeEvent(
            new UpdateStatusWebEvent(requestId, "error", Version: "not-a-version")));
    }

    [Fact]
    public void SerializeEvent_ProjectsOnlyTheBackgroundUpdateVersion()
    {
        var json = WebMessageProtocol.SerializeEvent(
            new BackgroundUpdateAvailableWebEvent("2.3.4"));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("update/background-available", root.GetProperty("type").GetString());
        Assert.Equal("2.3.4", root.GetProperty("version").GetString());
        Assert.Equal(
            ["schemaVersion", "type", "version"],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.DoesNotContain("url", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("message", json, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.SerializeEvent(
            new BackgroundUpdateAvailableWebEvent("not-a-version")));
    }

    private static WorktreeRecord CreateWorktreeRecord() => new(
        "worktree-7",
        "C:\\repo\\.worktrees\\parser",
        "C:\\repo",
        "repo",
        WorktreeKind.Manual,
        WorktreeCreationType.Linked,
        "feature/parser",
        "abc123",
        new SessionId("session-42"),
        4242,
        DateTimeOffset.Parse("2026-07-18T08:00:00Z"),
        DateTimeOffset.Parse("2026-07-18T09:00:00Z"),
        WorktreeRecordStatus.Alive,
        new WorktreeMetadata("Parser experiment", UserProvided: true));
}
