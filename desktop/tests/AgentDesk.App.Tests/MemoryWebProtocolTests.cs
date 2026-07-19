using System.Text;
using System.Text.Json;
using AgentDesk.App.Bridge;
using AgentDesk.Core.Engine;

namespace AgentDesk.App.Tests;

public sealed class MemoryWebProtocolTests
{
    private const string RequestId = "865b214e-9411-43f6-a3a0-0cff2f52b5a2";

    [Fact]
    public void ParseCommandMapsBoundedMemoryOperations()
    {
        var list = Assert.IsType<MemoryListWebCommand>(Parse("memory/list"));
        Assert.Equal(RequestId, list.RequestId);
        Assert.Equal(7, list.WorkspaceGeneration);
        Assert.Equal("session-42", list.SessionId);

        var read = Assert.IsType<MemoryReadWebCommand>(
            Parse("memory/read", ",\"fileId\":\"workspace\""));
        Assert.Equal("workspace", read.FileId.Value);

        var write = Assert.IsType<MemoryWriteWebCommand>(
            Parse(
                "memory/write",
                ",\"fileId\":\"global\",\"content\":\"# Memory\\n\",\"confirmed\":false"));
        Assert.Equal("# Memory\n", write.Content);
        Assert.False(write.Confirmed);

        var delete = Assert.IsType<MemoryDeleteWebCommand>(
            Parse(
                "memory/delete",
                ",\"fileId\":\"session/2026-07-19.md\",\"confirmed\":true"));
        Assert.Equal("session/2026-07-19.md", delete.FileId.Value);
        Assert.True(delete.Confirmed);
    }

    [Fact]
    public void ParseCommandRejectsUnsafeOrUnboundedMemoryInput()
    {
        Assert.Throws<InvalidDataException>(() => Parse(
            "memory/read",
            ",\"fileId\":\"../MEMORY.md\""));
        Assert.Throws<InvalidDataException>(() => Parse(
            "memory/delete",
            ",\"fileId\":\"session/nested/log.md\",\"confirmed\":true"));
        Assert.Throws<InvalidDataException>(() => Parse(
            "memory/list",
            ",\"workspacePath\":\"C:\\\\private\""));

        var content = new string('\u754c', (64 * 1024 / 3) + 1);
        Assert.True(content.Length < 64 * 1024);
        Assert.True(Encoding.UTF8.GetByteCount(content) > 64 * 1024);
        Assert.Throws<InvalidDataException>(() => Parse(
            "memory/write",
            $",\"fileId\":\"workspace\",\"content\":{JsonSerializer.Serialize(content)},\"confirmed\":true"));
    }

    [Fact]
    public void ParseCommandAcceptsOnlyBoundedHostMemoryConfirmationTokens()
    {
        var token = new string('a', 64);
        var write = Assert.IsType<MemoryWriteWebCommand>(Parse(
            "memory/write",
            $",\"fileId\":\"workspace\",\"content\":\"updated\",\"confirmed\":true," +
            $"\"confirmationToken\":\"{token}\""));
        var tokenProperty = write.GetType().GetProperty("ConfirmationToken");
        Assert.NotNull(tokenProperty);
        Assert.Equal(token, tokenProperty.GetValue(write));

        Assert.Throws<InvalidDataException>(() => Parse(
            "memory/delete",
            ",\"fileId\":\"workspace\",\"confirmed\":true,\"confirmationToken\":\"short\""));
    }

    [Fact]
    public void SerializeEventProjectsMemoryDataWithoutFilesystemPaths()
    {
        var file = File("workspace", MemoryFileScope.Workspace, "MEMORY.md", 9);
        WebEvent[] events =
        [
            new MemoryListedWebEvent(
                RequestId,
                7,
                "session-42",
                new MemoryFileListing([file], Truncated: false)),
            new MemoryDocumentWebEvent(
                RequestId,
                7,
                "session-42",
                new MemoryFileDocument(file, "# Memory\n")),
            new MemoryMutationWebEvent(
                RequestId,
                7,
                "session-42",
                MemoryOperation.Write,
                new MemoryFileId("workspace"),
                new MemoryMutationResult(
                    MemoryMutationStatus.Success,
                    "Memory file updated.",
                    file)),
            new MemoryErrorWebEvent(
                RequestId,
                7,
                "session-42",
                MemoryOperation.Read,
                "Memory files could not be read or updated.",
                new MemoryFileId("workspace")),
        ];

        var types = new[] { "memory/listed", "memory/document", "memory/mutation", "memory/error" };
        for (var index = 0; index < events.Length; index++)
        {
            var json = WebMessageProtocol.SerializeEvent(events[index]);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal(types[index], root.GetProperty("type").GetString());
            Assert.Equal(RequestId, root.GetProperty("requestId").GetString());
            Assert.Equal(7, root.GetProperty("workspaceGeneration").GetInt32());
            Assert.Equal("session-42", root.GetProperty("sessionId").GetString());
            Assert.DoesNotContain("workspacePath", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:\\\\", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("exception", json, StringComparison.OrdinalIgnoreCase);
        }

        using var listed = JsonDocument.Parse(WebMessageProtocol.SerializeEvent(events[0]));
        Assert.Equal(
            "workspace",
            listed.RootElement.GetProperty("files")[0].GetProperty("id").GetString());
        Assert.Equal(
            "workspace",
            listed.RootElement.GetProperty("files")[0].GetProperty("scope").GetString());

        using var mutation = JsonDocument.Parse(WebMessageProtocol.SerializeEvent(events[2]));
        Assert.Equal("write", mutation.RootElement.GetProperty("operation").GetString());
        Assert.Equal("success", mutation.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void SerializeEventRejectsInvalidMemoryBoundsAndRelationships()
    {
        var file = File("workspace", MemoryFileScope.Workspace, "MEMORY.md", 1);
        var tooMany = Enumerable.Range(0, 513)
            .Select(index => File(
                $"session/{index}.md",
                MemoryFileScope.Session,
                $"{index}.md",
                1))
            .ToArray();

        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.SerializeEvent(
            new MemoryListedWebEvent(
                RequestId,
                7,
                "session-42",
                new MemoryFileListing(tooMany, Truncated: true))));
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.SerializeEvent(
            new MemoryDocumentWebEvent(
                RequestId,
                7,
                "session-42",
                new MemoryFileDocument(
                    file,
                    new string('\u754c', (64 * 1024 / 3) + 1)))));
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.SerializeEvent(
            new MemoryMutationWebEvent(
                RequestId,
                7,
                "session-42",
                MemoryOperation.Write,
                new MemoryFileId("global"),
                new MemoryMutationResult(
                    MemoryMutationStatus.Success,
                    "updated",
                    file))));
    }

    [Fact]
    public void SerializeEventProjectsVersionedMemoryCapabilities()
    {
        var json = WebMessageProtocol.SerializeEvent(new MemoryCapabilitiesWebEvent(
            "session-42",
            new MemoryManagementCapabilities(
                1,
                List: true,
                Read: true,
                Write: true,
                Delete: true,
                MutationConfirmationRequired: true)));

        using var document = JsonDocument.Parse(json);
        Assert.Equal("memory/capabilities", document.RootElement.GetProperty("type").GetString());
        var memory = document.RootElement.GetProperty("memory");
        Assert.Equal(1, memory.GetProperty("schemaVersion").GetInt32());
        Assert.True(memory.GetProperty("list").GetBoolean());
        Assert.True(memory.GetProperty("read").GetBoolean());
        Assert.True(memory.GetProperty("write").GetBoolean());
        Assert.True(memory.GetProperty("delete").GetBoolean());
        Assert.True(memory.GetProperty("mutationConfirmationRequired").GetBoolean());
    }

    [Fact]
    public void SerializeEventRejectsMemoryMutationCapabilitiesWithoutConfirmation()
    {
        Assert.Throws<InvalidDataException>(() => WebMessageProtocol.SerializeEvent(
            new MemoryCapabilitiesWebEvent(
                "session-42",
                new MemoryManagementCapabilities(
                    1,
                    List: true,
                    Read: true,
                    Write: true,
                    Delete: false,
                    MutationConfirmationRequired: false))));
    }

    private static WebCommand Parse(string type, string fields = "") =>
        WebMessageProtocol.ParseCommand(
            $$"""
            {
              "schemaVersion": 1,
              "type": "{{type}}",
              "requestId": "{{RequestId}}",
              "workspaceGeneration": 7,
              "sessionId": "session-42"
              {{fields}}
            }
            """);

    private static MemoryFileDescriptor File(
        string id,
        MemoryFileScope scope,
        string name,
        ulong byteLength) =>
        new(
            new MemoryFileId(id),
            scope,
            name,
            byteLength,
            DateTimeOffset.Parse("2026-07-19T08:30:00Z"),
            Writable: true);
}
