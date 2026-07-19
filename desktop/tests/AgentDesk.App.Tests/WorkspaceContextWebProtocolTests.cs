using System.Text;
using System.Text.Json;
using AgentDesk.App.Bridge;
using AgentDesk.App.Workspace;

namespace AgentDesk.App.Tests;

public sealed class WorkspaceContextWebProtocolTests
{
    private const string RequestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";

    [Fact]
    public void ParseCommandMapsBoundedWorkspaceContextOperations()
    {
        var instructions = Assert.IsType<WorkspaceInstructionsListWebCommand>(
            Parse("workspace/context/instructions/list", ""));
        Assert.Equal(RequestId, instructions.RequestId);
        Assert.Equal(7, instructions.WorkspaceGeneration);

        var read = Assert.IsType<WorkspaceFileReadWebCommand>(
            Parse("workspace/context/file/read", ",\"relativePath\":\"src/AGENTS.md\""));
        Assert.Equal("src/AGENTS.md", read.RelativePath);

        var write = Assert.IsType<WorkspaceInstructionsWriteWebCommand>(
            Parse(
                "workspace/context/instructions/write",
                ",\"relativePath\":\"AGENTS.md\",\"content\":\"# Rules\\n\""));
        Assert.Equal("AGENTS.md", write.RelativePath);
        Assert.Equal("# Rules\n", write.Content);

        var search = Assert.IsType<WorkspaceFileSearchWebCommand>(
            Parse("workspace/context/file/search", ",\"query\":\"parser\""));
        Assert.Equal("parser", search.Query);
    }

    [Fact]
    public void ParseCommandRejectsUnboundedOrUnsafeWorkspaceContextFields()
    {
        Assert.Throws<InvalidDataException>(() => Parse(
            "workspace/context/file/search",
            $",\"query\":{JsonSerializer.Serialize(new string('q', 513))}"));
        Assert.Throws<InvalidDataException>(() => Parse(
            "workspace/context/instructions/write",
            $",\"relativePath\":\"AGENTS.md\",\"content\":{JsonSerializer.Serialize(new string('x', 512 * 1024 + 1))}"));
        Assert.Throws<InvalidDataException>(() => Parse(
            "workspace/context/file/read",
            ",\"relativePath\":\"../secret.txt\""));
        Assert.Throws<InvalidDataException>(() => Parse(
            "workspace/context/file/read",
            ",\"relativePath\":\"C:\\\\secret.txt\""));
        Assert.Throws<InvalidDataException>(() => Parse(
            "workspace/context/file/read",
            ",\"relativePath\":\"notes.md\""));
        Assert.Throws<InvalidDataException>(() => Parse(
            "workspace/context/file/read",
            ",\"relativePath\":\"agents.md\""));
        Assert.Throws<InvalidDataException>(() => Parse(
            "workspace/context/instructions/write",
            ",\"relativePath\":\"notes.md\",\"content\":\"replace\""));
        Assert.Throws<InvalidDataException>(() => Parse(
            "workspace/context/instructions/list",
            ",\"workspacePath\":\"C:\\\\private\""));
    }

    [Fact]
    public void SerializeEventProjectsBoundedRelativeWorkspaceContextOnly()
    {
        var file = new WorkspaceContextFile(
            "src/AGENTS.md",
            128,
            DateTimeOffset.Parse("2026-07-18T08:30:00Z"));
        WebEvent[] events =
        [
            new WorkspaceInstructionsListWebEvent(RequestId, 7, [file]),
            new WorkspaceFileReadWebEvent(RequestId, 7, "src/AGENTS.md", "# Rules\n"),
            new WorkspaceInstructionsWriteWebEvent(RequestId, 7, "src/AGENTS.md"),
            new WorkspaceFileSearchWebEvent(RequestId, 7, "agents", [file]),
            new WorkspaceContextErrorWebEvent(
                RequestId,
                7,
                WorkspaceContextOperation.FileRead),
        ];

        foreach (var webEvent in events)
        {
            var json = WebMessageProtocol.SerializeEvent(webEvent);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Assert.Equal(7, root.GetProperty("workspaceGeneration").GetInt32());
            Assert.Equal(RequestId, root.GetProperty("requestId").GetString());
            Assert.DoesNotContain("workspacePath", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("exception", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:\\\\", json, StringComparison.OrdinalIgnoreCase);
        }

        using var read = JsonDocument.Parse(WebMessageProtocol.SerializeEvent(events[1]));
        Assert.Equal("# Rules\n", read.RootElement.GetProperty("content").GetString());
        using var error = JsonDocument.Parse(WebMessageProtocol.SerializeEvent(events[4]));
        Assert.Equal("file-read", error.RootElement.GetProperty("operation").GetString());
        Assert.False(error.RootElement.TryGetProperty("message", out _));
    }

    [Fact]
    public void SerializeEventRejectsInvalidWorkspaceContextBounds()
    {
        var now = DateTimeOffset.Parse("2026-07-18T08:30:00Z");
        var tooMany = Enumerable.Range(0, 101)
            .Select(index => new WorkspaceContextFile($"src/file-{index}.cs", 1, now))
            .ToArray();

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.SerializeEvent(
            new WorkspaceInstructionsListWebEvent(RequestId, -1, [])));
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.SerializeEvent(
            new WorkspaceFileReadWebEvent(
                RequestId,
                7,
                "AGENTS.md",
                new string('x', 512 * 1024 + 1))));
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.SerializeEvent(
            new WorkspaceFileSearchWebEvent(RequestId, 7, "", tooMany)));
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.SerializeEvent(
            new WorkspaceInstructionsWriteWebEvent(RequestId, 7, "../AGENTS.md")));
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.SerializeEvent(
            new WorkspaceFileReadWebEvent(RequestId, 7, "notes.md", "private")));
    }

    [Fact]
    public void WorkspaceContextContentUsesTheNativeUtf8ByteBudget()
    {
        var content = new string(
            '\u754c',
            (WorkspaceContextService.MaximumReadableFileBytes / 3) + 1);
        Assert.True(content.Length < WorkspaceContextService.MaximumReadableFileBytes);
        Assert.True(
            Encoding.UTF8.GetByteCount(content) >
            WorkspaceContextService.MaximumReadableFileBytes);

        Assert.Throws<InvalidDataException>(() => Parse(
            "workspace/context/instructions/write",
            $",\"relativePath\":\"AGENTS.md\",\"content\":{JsonSerializer.Serialize(content)}"));
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.SerializeEvent(
            new WorkspaceFileReadWebEvent(RequestId, 7, "AGENTS.md", content)));
    }

    private static WebCommand Parse(string type, string fields) =>
        WebMessageProtocol.ParseCommand(
            $$"""
            {
              "schemaVersion": 1,
              "type": "{{type}}",
              "requestId": "{{RequestId}}",
              "workspaceGeneration": 7{{fields}}
            }
            """);
}
