using System.Collections.Concurrent;
using AgentDesk.App.Bridge;
using AgentDesk.App.Workspace;
using AgentDesk.Core.Security;

namespace AgentDesk.App.Tests;

public sealed class WorkspaceContextControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"agentdesk-workspace-controller-{Guid.NewGuid():N}");

    [Fact]
    public async Task ControllerListsReadsSearchesAndWritesCurrentWorkspaceContext()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "root rules");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "Parser.cs"), "class Parser {}");
        await using var controller = CreateController();
        var events = new List<WebEvent>();
        controller.EventProduced += (_, webEvent) => events.Add(webEvent);

        await controller.HandleAsync(new WorkspaceInstructionsListWebCommand(RequestId(1), 1));
        await controller.HandleAsync(new WorkspaceFileReadWebCommand(RequestId(2), 1, "AGENTS.md"));
        await controller.HandleAsync(new WorkspaceFileSearchWebCommand(RequestId(3), 1, "parser"));
        await controller.HandleAsync(new WorkspaceInstructionsWriteWebCommand(
            RequestId(4),
            1,
            "AGENTS.md",
            "updated rules"));

        Assert.Equal(
            ["AGENTS.md"],
            Assert.Single(events.OfType<WorkspaceInstructionsListWebEvent>())
                .Files.Select(file => file.RelativePath));
        Assert.Equal(
            "root rules",
            Assert.Single(events.OfType<WorkspaceFileReadWebEvent>()).Content);
        Assert.Equal(
            ["src/Parser.cs"],
            Assert.Single(events.OfType<WorkspaceFileSearchWebEvent>())
                .Files.Select(file => file.RelativePath));
        Assert.Equal(
            "AGENTS.md",
            Assert.Single(events.OfType<WorkspaceInstructionsWriteWebEvent>()).RelativePath);
        Assert.Equal("updated rules", await File.ReadAllTextAsync(Path.Combine(_root, "AGENTS.md")));
        Assert.All(events, webEvent => Assert.Equal(1, WorkspaceGeneration(webEvent)));
    }

    [Fact]
    public async Task ControllerDropsStaleResultsAndSanitizesWorkspaceContextFailures()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "rules");
        await using var controller = CreateController();
        var events = new List<WebEvent>();
        controller.EventProduced += (_, webEvent) => events.Add(webEvent);

        await controller.HandleAsync(new WorkspaceFileSearchWebCommand(RequestId(1), 0, "agents"));
        Assert.Empty(events);

        await controller.HandleAsync(new WorkspaceFileReadWebCommand(
            RequestId(2),
            1,
            "missing.txt"));

        var error = Assert.Single(events.OfType<WorkspaceContextErrorWebEvent>());
        Assert.Equal(WorkspaceContextOperation.FileRead, error.Operation);
        Assert.Equal(1, error.WorkspaceGeneration);
        var json = WebMessageProtocol.SerializeEvent(error);
        Assert.DoesNotContain("missing.txt", json, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ControllerCancelsASupersededWorkspaceFileSearch()
    {
        Directory.CreateDirectory(_root);
        var service = new ControlledWorkspaceContextService();
        await using var controller = CreateController(service);
        var events = new ConcurrentQueue<WebEvent>();
        controller.EventProduced += (_, webEvent) => events.Enqueue(webEvent);

        var first = controller.HandleAsync(new WorkspaceFileSearchWebCommand(
            RequestId(1),
            1,
            "first"));
        await service.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = controller.HandleAsync(new WorkspaceFileSearchWebCommand(
            RequestId(2),
            1,
            "second"));

        await service.FirstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        var result = Assert.Single(events.OfType<WorkspaceFileSearchWebEvent>());
        Assert.Equal("second", result.Query);
        Assert.DoesNotContain(events, item => item is WorkspaceContextErrorWebEvent);
    }

    [Fact]
    public async Task ControllerCancelsWorkspaceFileSearchOnWorkspaceChangeAndDispose()
    {
        Directory.CreateDirectory(_root);
        var other = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-workspace-controller-other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(other);
        try
        {
            var switchService = new ControlledWorkspaceContextService();
            await using (var controller = CreateController(switchService))
            {
                var search = controller.HandleAsync(new WorkspaceFileSearchWebCommand(
                    RequestId(1),
                    1,
                    "switch"));
                await switchService.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(await controller.UpdateWorkspaceAsync(other));
                await switchService.FirstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await search.WaitAsync(TimeSpan.FromSeconds(5));
            }

            var disposeService = new ControlledWorkspaceContextService();
            var disposable = CreateController(disposeService);
            var pending = disposable.HandleAsync(new WorkspaceFileSearchWebCommand(
                RequestId(2),
                1,
                "dispose"));
            await disposeService.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await disposable.DisposeAsync();
            await disposeService.FirstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task ControllerPublishesWorkspaceChangeWhenSearchCancellationCallbackFails()
    {
        Directory.CreateDirectory(_root);
        var other = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-workspace-controller-fault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(other);
        try
        {
            var service = new ControlledWorkspaceContextService(
                throwOnFirstCancellation: true);
            await using var controller = CreateController(service);
            var events = new ConcurrentQueue<WebEvent>();
            controller.EventProduced += (_, webEvent) => events.Enqueue(webEvent);
            var search = controller.HandleAsync(new WorkspaceFileSearchWebCommand(
                RequestId(1),
                1,
                "switch"));
            await service.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var updated = await controller.UpdateWorkspaceAsync(other);
            await search.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(updated);
            Assert.Contains(
                new WorkspaceSelectedWebEvent(other, 2),
                events);
            Assert.Contains(
                events,
                item => item is EngineStatusWebEvent { Status: "idle" });
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task ControllerCleansUpSearchAfterSupersededCancellationCallbackFails()
    {
        Directory.CreateDirectory(_root);
        var service = new ControlledWorkspaceContextService(
            throwOnFirstCancellation: true);
        await using var controller = CreateController(service);
        var first = controller.HandleAsync(new WorkspaceFileSearchWebCommand(
            RequestId(1),
            1,
            "first"));
        await service.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = controller.HandleAsync(new WorkspaceFileSearchWebCommand(
            RequestId(2),
            1,
            "second"));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        var completedSearchToken = service.CompletedSearchToken;

        await controller.HandleAsync(new WorkspaceFileSearchWebCommand(
            RequestId(3),
            1,
            "third"));

        Assert.False(completedSearchToken.IsCancellationRequested);
    }

    private AgentDeskHostController CreateController(
        IWorkspaceContextService? workspaceContextService = null) => new(
        new AgentDeskHostOptions(_root)
        {
            WorkspaceContextService = workspaceContextService,
        },
        new EmptyCredentialStore(),
        new FailingSidecarHostFactory());

    private static string RequestId(int value) =>
        $"00000000-0000-4000-8000-{value:000000000000}";

    private static int WorkspaceGeneration(WebEvent webEvent) => webEvent switch
    {
        WorkspaceInstructionsListWebEvent value => value.WorkspaceGeneration,
        WorkspaceFileReadWebEvent value => value.WorkspaceGeneration,
        WorkspaceInstructionsWriteWebEvent value => value.WorkspaceGeneration,
        WorkspaceFileSearchWebEvent value => value.WorkspaceGeneration,
        _ => throw new ArgumentOutOfRangeException(nameof(webEvent)),
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class EmptyCredentialStore : ICredentialStore
    {
        public void Save(string name, string secret)
        {
        }

        public string? Read(string name) => null;

        public bool Delete(string name) => false;
    }

    private sealed class FailingSidecarHostFactory : IAgentDeskSidecarHostFactory
    {
        public IAgentDeskSidecarHost Create() =>
            throw new InvalidOperationException("Workspace context must not start the sidecar.");
    }

    private sealed class ControlledWorkspaceContextService(
        bool throwOnFirstCancellation = false) : IWorkspaceContextService
    {
        private int _searchCount;

        public TaskCompletionSource FirstStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCancelled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken CompletedSearchToken { get; private set; }

        public Task<IReadOnlyList<WorkspaceContextFile>> ListInstructionFilesAsync(
            string workspacePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkspaceContextFile>>([]);

        public Task<string> ReadTextFileAsync(
            string workspacePath,
            string relativePath,
            CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

        public Task WriteInstructionFileAsync(
            string workspacePath,
            string relativePath,
            string content,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<IReadOnlyList<WorkspaceContextFile>> SearchFilesAsync(
            string workspacePath,
            string query,
            int limit = 50,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _searchCount);
            if (call == 1)
            {
                var cancelled = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.Register(() =>
                {
                    FirstCancelled.TrySetResult();
                    cancelled.TrySetCanceled(cancellationToken);
                    if (throwOnFirstCancellation)
                    {
                        throw new InvalidOperationException("cancellation callback failed");
                    }
                });
                FirstStarted.TrySetResult();
                await cancelled.Task;
            }

            CompletedSearchToken = cancellationToken;

            return
            [
                new WorkspaceContextFile(
                    $"src/{query}.cs",
                    1,
                    DateTimeOffset.Parse("2026-07-18T08:30:00Z")),
            ];
        }
    }
}
