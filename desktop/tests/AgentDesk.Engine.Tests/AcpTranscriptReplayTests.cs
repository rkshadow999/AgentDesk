using System.Text;
using System.Text.Json;
using AgentDesk.Core.Engine;

namespace AgentDesk.Engine.Tests;

public sealed class AcpTranscriptReplayTests
{
    private const string FixtureName = "agentdesk-acp-v1-session.ndjson";
    private const string ValidHeader =
        "{\"fixtureFormat\":\"agentdesk.acp-transcript\",\"schemaVersion\":1," +
        "\"redaction\":{\"strategy\":\"synthetic\",\"containsPromptText\":false," +
        "\"containsCredentials\":false,\"containsRealPaths\":false}}\n";

    public static TheoryData<string> InvalidRedactionHeaders => new()
    {
        "null",
        "{}",
        "{\"strategy\":\"recorded\",\"containsPromptText\":false," +
            "\"containsCredentials\":false,\"containsRealPaths\":false}",
        "{\"strategy\":\"synthetic\",\"containsCredentials\":false," +
            "\"containsRealPaths\":false}",
        "{\"strategy\":\"synthetic\",\"containsPromptText\":true," +
            "\"containsCredentials\":false,\"containsRealPaths\":false}",
        "{\"strategy\":\"synthetic\",\"containsPromptText\":false," +
            "\"containsRealPaths\":false}",
        "{\"strategy\":\"synthetic\",\"containsPromptText\":false," +
            "\"containsCredentials\":true,\"containsRealPaths\":false}",
        "{\"strategy\":\"synthetic\",\"containsPromptText\":false," +
            "\"containsCredentials\":false}",
        "{\"strategy\":\"synthetic\",\"containsPromptText\":false," +
            "\"containsCredentials\":false,\"containsRealPaths\":true}",
        "{\"strategy\":\"synthetic\",\"containsPromptText\":\"false\"," +
            "\"containsCredentials\":false,\"containsRealPaths\":false}",
    };

    [Fact]
    public async Task VersionOneFixture_ReplaysSanitizedWorkflowAndIgnoresOptionalFields()
    {
        await using var replay = await AcpTranscriptReplay.LoadAsync(FixturePath(FixtureName));
        var client = replay.Client;
        var eventReceived = new TaskCompletionSource<EngineEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var permissionReceived = new TaskCompletionSource<PermissionRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.EventReceived += (_, value) => eventReceived.TrySetResult(value);
        client.PermissionRequested += (_, value) => permissionReceived.TrySetResult(value);

        var initializeTask = client.InitializeAsync(replay.Timeout.Token);
        _ = await replay.ExpectClientMessageAsync("initialize");
        await replay.SendEngineMessageAsync();
        using var extensionInitialize = await replay.ExpectClientMessageAsync(
            "_agentdesk/v1/initialize");
        Assert.Equal(
            1,
            extensionInitialize.RootElement
                .GetProperty("params")
                .GetProperty("protocolVersion")
                .GetInt32());
        await replay.SendEngineMessageAsync();
        _ = await replay.ExpectClientMessageAsync("_agentdesk/v1/health");
        await replay.SendEngineMessageAsync();

        var capabilities = await initializeTask;
        Assert.True(capabilities.AgentDeskExtensions);
        Assert.True(capabilities.AgentDeskHealth);
        Assert.True(capabilities.StrictSandboxActive);

        var newSessionTask = client.NewSessionAsync("<WORKSPACE>", replay.Timeout.Token);
        using var newSession = await replay.ExpectClientMessageAsync("session/new");
        Assert.Equal(
            "<WORKSPACE>",
            newSession.RootElement.GetProperty("params").GetProperty("cwd").GetString());
        await replay.SendEngineMessageAsync();
        var sessionId = await newSessionTask;
        Assert.Equal("session-fixture", sessionId.Value);

        var promptTask = client.PromptAsync(
            sessionId,
            "[redacted]",
            replay.Timeout.Token);
        using var prompt = await replay.ExpectClientMessageAsync("session/prompt");
        var promptBlock = Assert.Single(
            prompt.RootElement
                .GetProperty("params")
                .GetProperty("prompt")
                .EnumerateArray());
        Assert.Equal("text", promptBlock.GetProperty("type").GetString());
        Assert.Equal("[redacted]", promptBlock.GetProperty("text").GetString());

        await replay.SendEngineMessageAsync();
        var engineEvent = await eventReceived.Task.WaitAsync(replay.Timeout.Token);
        Assert.Equal("agent_message_chunk", engineEvent.UpdateKind);
        Assert.Equal(
            1,
            engineEvent.Update.GetProperty("futureChunkOrdinal").GetInt32());

        await replay.SendEngineMessageAsync();
        var permission = await permissionReceived.Task.WaitAsync(replay.Timeout.Token);
        Assert.Equal("session-fixture", permission.SessionId.Value);
        Assert.Equal("edit-fixture", permission.ToolCallId);
        Assert.Equal(["workspace/fixture.txt:7"], permission.Locations);
        Assert.True(await client.RespondToPermissionAsync(
            permission.RequestId,
            PermissionDecision.Selected("allow-once"),
            replay.Timeout.Token));
        using var permissionResponse = await replay.ExpectClientMessageAsync();
        Assert.Equal(41, permissionResponse.RootElement.GetProperty("id").GetInt64());

        await replay.SendEngineMessageAsync();
        var promptResult = await promptTask;
        Assert.Equal(EngineStopReason.EndTurn, promptResult.StopReason);
        Assert.Equal("end_turn", promptResult.RawStopReason);

        var pendingPromptTask = client.PromptAsync(
            sessionId,
            "[redacted]",
            replay.Timeout.Token);
        _ = await replay.ExpectClientMessageAsync("session/prompt");
        await client.CancelAsync(sessionId, replay.Timeout.Token);
        using var cancel = await replay.ExpectClientMessageAsync("session/cancel");
        Assert.False(cancel.RootElement.TryGetProperty("id", out _));
        await replay.SendEngineMessageAsync();
        Assert.Equal(EngineStopReason.Cancelled, (await pendingPromptTask).StopReason);

        await replay.ShutdownAsync();
        replay.AssertComplete();
    }

    [Fact]
    public async Task LoadAsync_WhenFixtureIsMissingFailsClosed()
    {
        var path = FixturePath("missing-acp-transcript.ndjson");

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => AcpTranscriptReplay.LoadAsync(path));

        Assert.Equal(path, exception.FileName);
    }

    [Fact]
    public async Task LoadAsync_WhenFixtureVersionIsUnsupportedFailsClosed()
    {
        const string transcript =
            "{\"fixtureFormat\":\"agentdesk.acp-transcript\",\"schemaVersion\":2}\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(transcript));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => AcpTranscriptReplay.LoadAsync(stream, "unsupported-version"));

        Assert.Contains("schema version 2", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidRedactionHeaders))]
    public async Task LoadAsync_WhenRedactionDeclarationIsUnsafeFailsClosed(
        string redactionJson)
    {
        var transcript =
            "{\"fixtureFormat\":\"agentdesk.acp-transcript\",\"schemaVersion\":1," +
            $"\"redaction\":{redactionJson}}}\n";

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => LoadAndDisposeAsync(transcript, "unsafe-redaction"));

        Assert.Contains("redaction", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sk-0123456789abcdefghijklmnop")]
    [InlineData("ghp_0123456789abcdefghijklmnop")]
    [InlineData("github_pat_0123456789_abcdefghijklmnop")]
    [InlineData("xoxb" + "-0123456789-abcdefghijklmnop")]
    [InlineData("Bearer synthetic-token-0123456789")]
    [InlineData(@"C:\Users\fixture\repo")]
    [InlineData("/home/fixture/repo")]
    public async Task LoadAsync_WhenFixtureContainsSensitiveValueFailsClosed(string value)
    {
        var record = JsonSerializer.Serialize(new
        {
            sequence = 1,
            direction = "lifecycle",
            @event = "shutdown",
            sample = value,
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => LoadAndDisposeAsync(
                ValidHeader + record + "\n",
                "sensitive-value"));

        Assert.Contains("sensitive", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_WhenFixtureExceedsSizeBoundFailsClosed()
    {
        var transcript = JsonSerializer.Serialize(new
        {
            fixtureFormat = "agentdesk.acp-transcript",
            schemaVersion = 1,
            redaction = new
            {
                strategy = "synthetic",
                containsPromptText = false,
                containsCredentials = false,
                containsRealPaths = false,
            },
            padding = new string('x', (1024 * 1024) + 1),
        }) + "\n";

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => LoadAndDisposeAsync(transcript, "oversized"));

        Assert.Contains("maximum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_WhenRecordsAreOutOfOrderFailsClosed()
    {
        const string record =
            "{\"sequence\":2,\"direction\":\"lifecycle\",\"event\":\"shutdown\"}\n";

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => LoadAndDisposeAsync(ValidHeader + record, "out-of-order"));

        Assert.Contains("out-of-order", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssertComplete_WhenTailRecordWasNotConsumedFailsClosed()
    {
        const string records =
            "{\"sequence\":1,\"direction\":\"lifecycle\",\"event\":\"shutdown\"}\n" +
            "{\"sequence\":2,\"direction\":\"lifecycle\",\"event\":\"shutdown\"}\n";
        await using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes(ValidHeader + records));
        await using var replay = await AcpTranscriptReplay.LoadAsync(stream, "tail-record");

        await replay.ShutdownAsync();

        var exception = Assert.Throws<InvalidDataException>(replay.AssertComplete);
        Assert.Contains("1 remain", exception.Message, StringComparison.Ordinal);
    }

    private static async Task LoadAndDisposeAsync(string transcript, string sourceName)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(transcript));
        await using var replay = await AcpTranscriptReplay.LoadAsync(stream, sourceName);
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
