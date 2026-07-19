using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using AgentDesk.Core.Engine;
using AgentDesk.Engine.Acp;
using AgentDesk.Engine.Transport;

namespace AgentDesk.Engine.Tests;

public sealed class AcpEngineClientWorktreeTests
{
    [Fact]
    public async Task CreateWorktreeAsync_SendsTheRustCamelCaseShapeAndParsesExists()
    {
        await using var peer = new AcpPeer();
        var request = new WorktreeCreateRequest(
            new SessionId("session-42"),
            "C:\\repo",
            DestinationPath: "C:\\worktrees\\feature",
            CopyMode: WorktreeCopyMode.Clean,
            GitReference: "refs/heads/feature/worktree",
            CopyIgnoredInBackground: true,
            IgnoredSkipPatterns: ["*.log", ".cache/**"],
            CreationType: WorktreeCreationType.Standalone,
            Label: "feature-worktree");

        var operation = peer.Client.CreateWorktreeAsync(request, peer.Timeout.Token);
        using var message = await peer.ReadMessageAsync();

        AssertRequest(message.RootElement, 1, "x.ai/git/worktree/create");
        var parameters = message.RootElement.GetProperty("params");
        AssertPropertyNames(
            parameters,
            "sessionId",
            "sourcePath",
            "worktreePath",
            "copyMode",
            "gitRef",
            "copyIgnoredInBackground",
            "ignoredSkipPatterns",
            "worktreeType",
            "label");
        Assert.Equal("session-42", parameters.GetProperty("sessionId").GetString());
        Assert.Equal("C:\\repo", parameters.GetProperty("sourcePath").GetString());
        Assert.Equal("C:\\worktrees\\feature", parameters.GetProperty("worktreePath").GetString());
        Assert.Equal("clean", parameters.GetProperty("copyMode").GetString());
        Assert.Equal("refs/heads/feature/worktree", parameters.GetProperty("gitRef").GetString());
        Assert.True(parameters.GetProperty("copyIgnoredInBackground").GetBoolean());
        Assert.Equal(
            ["*.log", ".cache/**"],
            parameters.GetProperty("ignoredSkipPatterns")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
        Assert.Equal("standalone", parameters.GetProperty("worktreeType").GetString());
        Assert.Equal("feature-worktree", parameters.GetProperty("label").GetString());

        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "status": "exists",
                "sessionId": "session-42",
                "worktreePath": "C:\\worktrees\\feature",
                "commit": "0123456789abcdef",
                "sourceGitRoot": "C:\\repo"
              }
            }
            """);

        var result = await operation;
        Assert.Equal(WorktreeCreateStatus.Exists, result.Status);
        Assert.Equal("session-42", result.SessionId.Value);
        Assert.Equal("C:\\worktrees\\feature", result.WorktreePath);
        Assert.Equal("0123456789abcdef", result.Commit);
        Assert.Equal("C:\\repo", result.SourceGitRoot);
    }

    [Fact]
    public async Task CreateWorktreeAsync_ParsesCreatingWithoutInventingACommit()
    {
        await using var peer = new AcpPeer();
        var operation = peer.Client.CreateWorktreeAsync(
            new WorktreeCreateRequest(new SessionId("session-42"), "/repo"),
            peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();

        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "status": "creating",
                "sessionId": "session-42",
                "worktreePath": "/home/user/.grok/worktrees/repo/feature",
                "sourceGitRoot": "/repo"
              }
            }
            """);

        var result = await operation;
        Assert.Equal(WorktreeCreateStatus.Creating, result.Status);
        Assert.Null(result.Commit);
    }

    [Fact]
    public async Task ListWorktreesAsync_UsesTheCurrentSnakeCaseListFieldAndParsesRecords()
    {
        await using var peer = new AcpPeer();
        var operation = peer.Client.ListWorktreesAsync(
            new WorktreeListRequest(
                Repository: "repo",
                Types: [WorktreeKind.Session, WorktreeKind.Subagent],
                IncludeAll: true),
            peer.Timeout.Token);
        using var message = await peer.ReadMessageAsync();

        AssertRequest(message.RootElement, 1, "x.ai/git/worktree/list");
        var parameters = message.RootElement.GetProperty("params");
        AssertPropertyNames(parameters, "repo", "type", "include_all");
        Assert.Equal("repo", parameters.GetProperty("repo").GetString());
        Assert.Equal(
            ["session", "subagent"],
            parameters.GetProperty("type")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
        Assert.True(parameters.GetProperty("include_all").GetBoolean());
        Assert.False(parameters.TryGetProperty("includeAll", out _));

        await peer.RespondAsync(
            1,
            """
            {
              "result": [{
                "id": "feature-0123456789abcdef",
                "path": "C:\\worktrees\\feature",
                "source_repo": "C:\\repo",
                "repo_name": "repo",
                "kind": "session",
                "creation_mode": "linked",
                "git_ref": "refs/heads/feature/worktree",
                "head_commit": "0123456789abcdef",
                "session_id": "session-42",
                "creator_pid": 1234,
                "created_at": 1735689600,
                "last_accessed_at": 1735689660,
                "status": "alive",
                "metadata": {
                  "label": "feature-worktree",
                  "user_provided": true
                }
              }]
            }
            """);

        var record = Assert.Single(await operation);
        Assert.Equal("feature-0123456789abcdef", record.Id);
        Assert.Equal("C:\\worktrees\\feature", record.Path);
        Assert.Equal("C:\\repo", record.SourceRepository);
        Assert.Equal("repo", record.RepositoryName);
        Assert.Equal(WorktreeKind.Session, record.Kind);
        Assert.Equal(WorktreeCreationType.Linked, record.CreationType);
        Assert.Equal("refs/heads/feature/worktree", record.GitReference);
        Assert.Equal("0123456789abcdef", record.HeadCommit);
        Assert.Equal("session-42", record.SessionId?.Value);
        Assert.Equal(1234u, record.CreatorProcessId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1735689600), record.CreatedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1735689660), record.LastAccessedAt);
        Assert.Equal(WorktreeRecordStatus.Alive, record.Status);
        Assert.Equal("feature-worktree", record.Metadata?.Label);
        Assert.True(record.Metadata?.UserProvided);
    }

    [Fact]
    public async Task ShowWorktreeAsync_UsesIdOrPathAndMapsANullResult()
    {
        await using var peer = new AcpPeer();
        var operation = peer.Client.ShowWorktreeAsync(
            new WorktreeShowRequest("feature-0123456789abcdef"),
            peer.Timeout.Token);
        using var message = await peer.ReadMessageAsync();

        AssertRequest(message.RootElement, 1, "x.ai/git/worktree/show");
        var parameters = message.RootElement.GetProperty("params");
        AssertPropertyNames(parameters, "idOrPath");
        Assert.Equal("feature-0123456789abcdef", parameters.GetProperty("idOrPath").GetString());

        await peer.RespondAsync(1, "{ \"result\": null }");

        Assert.Null(await operation);
    }

    [Fact]
    public async Task ApplyWorktreeAsync_SendsCamelCaseAndParsesConflicts()
    {
        await using var peer = new AcpPeer();
        var operation = peer.Client.ApplyWorktreeAsync(
            new WorktreeApplyRequest(
                new SessionId("session-42"),
                "C:\\worktrees\\feature",
                WorktreeApplyMode.Merge),
            peer.Timeout.Token);
        using var message = await peer.ReadMessageAsync();

        AssertRequest(message.RootElement, 1, "x.ai/git/worktree/apply");
        var parameters = message.RootElement.GetProperty("params");
        AssertPropertyNames(parameters, "sessionId", "worktreePath", "mode");
        Assert.Equal("session-42", parameters.GetProperty("sessionId").GetString());
        Assert.Equal("C:\\worktrees\\feature", parameters.GetProperty("worktreePath").GetString());
        Assert.Equal("merge", parameters.GetProperty("mode").GetString());

        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "status": "conflicts",
                "files": [{
                  "path": "src/App.cs",
                  "oldPath": null,
                  "type": "edit",
                  "staged": false,
                  "additions": 3,
                  "deletions": 1,
                  "patch": "@@ -1 +1 @@",
                  "patchBytes": 14,
                  "patchLines": 1,
                  "oldText": "old",
                  "newText": "new"
                }],
                "conflicts": [{
                  "path": "src/App.cs",
                  "type": "edit",
                  "base": "base",
                  "ours": "ours",
                  "theirs": "theirs"
                }]
              }
            }
            """);

        var result = await operation;
        Assert.Equal(WorktreeApplyStatus.Conflicts, result.Status);
        Assert.Null(result.GitRoot);
        var file = Assert.Single(result.Files);
        Assert.Equal(WorktreeChangeType.Edit, file.ChangeType);
        Assert.Equal(3UL, file.Additions);
        Assert.Equal("new", file.NewText);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("src/App.cs", conflict.Path);
        Assert.Equal("theirs", conflict.Theirs);
    }

    [Fact]
    public async Task RemoveWorktreeAsync_SendsOnlyTheUnambiguousSelector()
    {
        await using var peer = new AcpPeer();
        var operation = peer.Client.RemoveWorktreeAsync(
            new WorktreeRemoveRequest(
                "feature-0123456789abcdef",
                Force: true,
                DryRun: false),
            peer.Timeout.Token);
        using var message = await peer.ReadMessageAsync();

        AssertRequest(message.RootElement, 1, "x.ai/git/worktree/remove");
        var parameters = message.RootElement.GetProperty("params");
        AssertPropertyNames(parameters, "idOrPath", "force", "dryRun");
        Assert.Equal("feature-0123456789abcdef", parameters.GetProperty("idOrPath").GetString());
        Assert.True(parameters.GetProperty("force").GetBoolean());
        Assert.False(parameters.GetProperty("dryRun").GetBoolean());
        Assert.False(parameters.TryGetProperty("worktreePath", out _));

        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "removed": true,
                "resolvedPath": "C:\\worktrees\\feature"
              }
            }
            """);

        var result = await operation;
        Assert.True(result.Removed);
        Assert.Equal("C:\\worktrees\\feature", result.ResolvedPath);
    }

    [Fact]
    public async Task GcWorktreesAsync_UsesDurationSyntaxAndParsesTheSnakeCaseReport()
    {
        await using var peer = new AcpPeer();
        var operation = peer.Client.GcWorktreesAsync(
            new WorktreeGcRequest(
                DryRun: true,
                MaximumAge: TimeSpan.FromDays(7),
                Force: false),
            peer.Timeout.Token);
        using var message = await peer.ReadMessageAsync();

        AssertRequest(message.RootElement, 1, "x.ai/git/worktree/gc");
        var parameters = message.RootElement.GetProperty("params");
        AssertPropertyNames(parameters, "dryRun", "maxAge", "force");
        Assert.True(parameters.GetProperty("dryRun").GetBoolean());
        Assert.Equal("7d", parameters.GetProperty("maxAge").GetString());
        Assert.False(parameters.GetProperty("force").GetBoolean());

        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "dead_removed": 2,
                "expired_removed": 3,
                "skipped_alive": 5,
                "remove_failed": 1
              }
            }
            """);

        var result = await operation;
        Assert.Equal(2UL, result.DeadRemoved);
        Assert.Equal(3UL, result.ExpiredRemoved);
        Assert.Equal(5UL, result.SkippedAlive);
        Assert.Equal(1UL, result.RemoveFailed);
    }

    [Theory]
    [InlineData("repo")]
    [InlineData("C:\\repo\\..\\Windows")]
    [InlineData("/repo/../etc")]
    [InlineData("C:\\repo\nother")]
    [InlineData("C:\\repo:stream")]
    [InlineData("\\\\.\\pipe\\agentdesk")]
    [InlineData("C:\\repo\\CON")]
    public async Task CreateWorktreeAsync_RejectsRelativeTraversalAndControlPaths(string sourcePath)
    {
        await using var peer = new AcpPeer();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            peer.Client.CreateWorktreeAsync(
                new WorktreeCreateRequest(new SessionId("session-42"), sourcePath),
                cancelled.Token));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("../outside")]
    [InlineData("worktree\nother")]
    [InlineData("worktree; Remove-Item C:\\\\")]
    public async Task ShowWorktreeAsync_RejectsSelectorInjection(string selector)
    {
        await using var peer = new AcpPeer();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            peer.Client.ShowWorktreeAsync(
                new WorktreeShowRequest(selector),
                peer.Timeout.Token));
    }

    [Theory]
    [InlineData("--upload-pack=calc.exe")]
    [InlineData("main\nother")]
    [InlineData("main;calc.exe")]
    [InlineData("refs/heads/../outside")]
    public async Task CreateWorktreeAsync_RejectsGitReferenceInjection(string gitReference)
    {
        await using var peer = new AcpPeer();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            peer.Client.CreateWorktreeAsync(
                new WorktreeCreateRequest(
                    new SessionId("session-42"),
                    "C:\\repo",
                    GitReference: gitReference),
                peer.Timeout.Token));
    }

    [Fact]
    public async Task CreateWorktreeAsync_RejectsOverlongAndControlFields()
    {
        await using var peer = new AcpPeer();
        var tooManyPatterns = Enumerable.Repeat("*.tmp", 257).ToArray();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            peer.Client.CreateWorktreeAsync(
                new WorktreeCreateRequest(
                    new SessionId("session-42"),
                    "C:\\repo",
                    Label: new string('a', 257)),
                peer.Timeout.Token));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            peer.Client.CreateWorktreeAsync(
                new WorktreeCreateRequest(
                    new SessionId("session-42"),
                    "C:\\repo",
                    IgnoredSkipPatterns: tooManyPatterns),
                peer.Timeout.Token));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            peer.Client.CreateWorktreeAsync(
                new WorktreeCreateRequest(
                    new SessionId("session-42"),
                    "C:\\repo",
                    IgnoredSkipPatterns: [".cache/../outside"]),
                peer.Timeout.Token));
    }

    [Fact]
    public async Task GcWorktreesAsync_RejectsNonPositiveOrFractionalAges()
    {
        await using var peer = new AcpPeer();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            peer.Client.GcWorktreesAsync(
                new WorktreeGcRequest(MaximumAge: TimeSpan.Zero),
                peer.Timeout.Token));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            peer.Client.GcWorktreesAsync(
                new WorktreeGcRequest(MaximumAge: TimeSpan.FromMilliseconds(1500)),
                peer.Timeout.Token));
    }

    [Fact]
    public async Task CreateWorktreeAsync_RejectsUnknownResponseFields()
    {
        await using var peer = new AcpPeer();
        var operation = peer.Client.CreateWorktreeAsync(
            new WorktreeCreateRequest(new SessionId("session-42"), "C:\\repo"),
            peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();

        await peer.RespondAsync(
            1,
            """
            {
              "result": {
                "status": "creating",
                "sessionId": "session-42",
                "worktreePath": "C:\\worktrees\\feature",
                "sourceGitRoot": "C:\\repo",
                "command": "powershell -EncodedCommand ..."
              }
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => operation);
    }

    [Fact]
    public async Task ListWorktreesAsync_RejectsUnknownMetadataFields()
    {
        await using var peer = new AcpPeer();
        var operation = peer.Client.ListWorktreesAsync(
            new WorktreeListRequest(),
            peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();

        await peer.RespondAsync(
            1,
            """
            {
              "result": [{
                "id": "feature-0123456789abcdef",
                "path": "C:\\worktrees\\feature",
                "source_repo": "C:\\repo",
                "repo_name": "repo",
                "kind": "session",
                "creation_mode": "linked",
                "git_ref": null,
                "head_commit": null,
                "session_id": null,
                "creator_pid": null,
                "created_at": 1735689600,
                "last_accessed_at": null,
                "status": "alive",
                "metadata": {
                  "label": "feature",
                  "user_provided": true,
                  "environment": { "API_KEY": "secret" }
                }
              }]
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => operation);
    }

    [Fact]
    public async Task ListWorktreesAsync_RejectsDuplicateAndOverlongRecordIds()
    {
        await using var peer = new AcpPeer();
        var operation = peer.Client.ListWorktreesAsync(
            new WorktreeListRequest(),
            peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();
        var id = new string('a', 513);

        await peer.RespondAsync(
            1,
            $$"""
            {
              "result": [{
                "id": "{{id}}",
                "path": "C:\\worktrees\\feature",
                "source_repo": "C:\\repo",
                "repo_name": "repo",
                "kind": "session",
                "creation_mode": "linked",
                "git_ref": null,
                "head_commit": null,
                "session_id": null,
                "creator_pid": null,
                "created_at": 1735689600,
                "last_accessed_at": null,
                "status": "alive",
                "metadata": null
              }]
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => operation);
    }

    [Fact]
    public async Task ListWorktreesAsync_RejectsAnInjectedRecordId()
    {
        await using var peer = new AcpPeer();
        var operation = peer.Client.ListWorktreesAsync(
            new WorktreeListRequest(),
            peer.Timeout.Token);
        _ = await peer.ReadMessageAsync();

        await peer.RespondAsync(
            1,
            """
            {
              "result": [{
                "id": "../../outside",
                "path": "C:\\worktrees\\feature",
                "source_repo": "C:\\repo",
                "repo_name": "repo",
                "kind": "session",
                "creation_mode": "linked",
                "git_ref": null,
                "head_commit": null,
                "session_id": null,
                "creator_pid": null,
                "created_at": 1735689600,
                "last_accessed_at": null,
                "status": "alive",
                "metadata": null
              }]
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => operation);
    }

    [Fact]
    public async Task WorktreeMethods_RejectExtensionAndJsonRpcErrors()
    {
        await using var extensionPeer = new AcpPeer();
        var extensionOperation = extensionPeer.Client.GcWorktreesAsync(
            new WorktreeGcRequest(),
            extensionPeer.Timeout.Token);
        _ = await extensionPeer.ReadMessageAsync();
        await extensionPeer.RespondAsync(
            1,
            "{ \"result\": null, \"error\": \"worktree database unavailable\" }");
        await Assert.ThrowsAsync<InvalidDataException>(() => extensionOperation);

        await using var rpcPeer = new AcpPeer();
        var rpcOperation = rpcPeer.Client.RemoveWorktreeAsync(
            new WorktreeRemoveRequest("feature-0123456789abcdef"),
            rpcPeer.Timeout.Token);
        _ = await rpcPeer.ReadMessageAsync();
        await rpcPeer.RespondWithErrorAsync(1, -32602, "Invalid params");
        var exception = await Assert.ThrowsAsync<JsonRpcException>(() => rpcOperation);
        Assert.Equal(-32602, exception.Code);
    }

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

    private static void AssertPropertyNames(JsonElement element, params string[] expected)
    {
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

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

        public Task RespondWithErrorAsync(long id, int code, string message) => WriteToClientAsync(
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                error = new { code, message },
            }) + "\n");

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
