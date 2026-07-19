using AgentDesk.App.Automation;
using AgentDesk.App.Bridge;
using AgentDesk.Core.Engine;

namespace AgentDesk.App.Tests;

public sealed class WindowsAutomationCoordinatorTests
{
    [Fact]
    public async Task DisabledPolicyRejectsTheOperationBeforeApprovalOrExecution()
    {
        var executor = new RecordingExecutor();
        var events = new List<WebEvent>();
        var approvalCalls = 0;
        using var coordinator = new WindowsAutomationCoordinator(
            executor,
            _ => Task.FromResult(false),
            (_, _) =>
            {
                approvalCalls++;
                return Task.FromResult(true);
            },
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            });

        await coordinator.ExecuteAsync(Command());

        Assert.Equal(0, approvalCalls);
        Assert.Empty(executor.Requests);
        var denied = Assert.Single(events);
        Assert.IsType<WindowsAutomationErrorWebEvent>(denied);
    }

    [Fact]
    public async Task ApprovedOperationExecutesOnlyAfterNativeApproval()
    {
        var executor = new RecordingExecutor();
        var events = new List<WebEvent>();
        var approval = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        WindowsAutomationApprovalRequest? approvalRequest = null;
        using var coordinator = new WindowsAutomationCoordinator(
            executor,
            _ => Task.FromResult(true),
            (request, _) =>
            {
                approvalRequest = request;
                return approval.Task;
            },
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            });

        var execution = coordinator.ExecuteAsync(Command());
        await WaitForAsync(() => approvalRequest is not null);

        Assert.Empty(executor.Requests);
        Assert.NotNull(approvalRequest);
        Assert.Equal(WindowsAutomationAction.SetValue, approvalRequest.Action);
        Assert.Equal(4242, approvalRequest.ProcessId);
        Assert.Equal("SearchBox", approvalRequest.Target);
        Assert.Equal("sensitive value".Length, approvalRequest.ValueCharacters);
        Assert.DoesNotContain("sensitive value", approvalRequest.ToString(), StringComparison.Ordinal);

        approval.SetResult(true);
        await execution;

        Assert.Single(executor.Requests);
        Assert.IsType<WindowsAutomationCompletedWebEvent>(Assert.Single(events));
    }

    [Fact]
    public async Task RejectedOperationNeverReachesTheExecutor()
    {
        var executor = new RecordingExecutor();
        var events = new List<WebEvent>();
        using var coordinator = new WindowsAutomationCoordinator(
            executor,
            _ => Task.FromResult(true),
            (_, _) => Task.FromResult(false),
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            });

        await coordinator.ExecuteAsync(Command());

        Assert.Empty(executor.Requests);
        Assert.IsType<WindowsAutomationCancelledWebEvent>(Assert.Single(events));
    }

    [Fact]
    public async Task PolicyRevokedWhileApprovalIsPendingPreventsExecution()
    {
        var executor = new RecordingExecutor();
        var events = new List<WebEvent>();
        var enabled = true;
        var approval = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new WindowsAutomationCoordinator(
            executor,
            _ => Task.FromResult(enabled),
            (_, _) =>
            {
                approvalStarted.TrySetResult();
                return approval.Task;
            },
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            });
        var execution = coordinator.ExecuteAsync(Command());
        await approvalStarted.Task;

        enabled = false;
        approval.SetResult(true);
        await execution;

        Assert.Empty(executor.Requests);
        var error = Assert.IsType<WindowsAutomationErrorWebEvent>(Assert.Single(events));
        Assert.Equal("disabled", error.Reason);
    }

    [Fact]
    public async Task TryHandleRoutesAutomationButNeverConsumesWebPermissionResponses()
    {
        var executor = new RecordingExecutor();
        var approval = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new WindowsAutomationCoordinator(
            executor,
            _ => Task.FromResult(true),
            (_, _) =>
            {
                approvalStarted.TrySetResult();
                return approval.Task;
            },
            _ => Task.CompletedTask);

        Assert.True(coordinator.TryHandle(Command(), CancellationToken.None, out var execution));
        await approvalStarted.Task;

        Assert.False(coordinator.TryHandle(
            new PermissionRespondWebCommand(
                "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d",
                PermissionDecision.Selected("allow-once")),
            CancellationToken.None,
            out _));
        Assert.Empty(executor.Requests);

        approval.SetResult(true);
        await execution;
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task DisposeWhileNativeApprovalIsPendingCancelsWithoutAReleaseRace()
    {
        var events = new List<WebEvent>();
        var approvalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new WindowsAutomationCoordinator(
            new RecordingExecutor(),
            _ => Task.FromResult(true),
            async (_, cancellationToken) =>
            {
                approvalStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            },
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            });
        var execution = coordinator.ExecuteAsync(Command());
        await approvalStarted.Task;

        coordinator.Dispose();
        await execution;

        Assert.IsType<WindowsAutomationCancelledWebEvent>(Assert.Single(events));
    }

    [Fact]
    public async Task DisposeWhilePolicyRecheckIsPendingPublishesCancellation()
    {
        var executor = new RecordingExecutor();
        var events = new List<WebEvent>();
        var policyChecks = 0;
        var recheckStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new WindowsAutomationCoordinator(
            executor,
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref policyChecks) == 1)
                {
                    return true;
                }
                recheckStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            },
            (_, _) => Task.FromResult(true),
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            });
        var execution = coordinator.ExecuteAsync(Command());
        await recheckStarted.Task;

        coordinator.Dispose();
        await execution;

        Assert.Empty(executor.Requests);
        Assert.IsType<WindowsAutomationCancelledWebEvent>(Assert.Single(events));
    }

    private static WindowsAutomationWebCommand Command() => new(
        "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d",
        WindowsAutomationAction.SetValue,
        processId: 4242,
        automationId: "SearchBox",
        name: null,
        value: "sensitive value");

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("The Windows Automation approval did not start.");
    }

    private sealed class RecordingExecutor : IWindowsAutomationExecutor
    {
        public List<WindowsAutomationRequest> Requests { get; } = [];

        public Task<WindowsAutomationResult> ExecuteAsync(
            WindowsAutomationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new WindowsAutomationResult(
                request.Action,
                request.ProcessId,
                request.AutomationId ?? request.Name ?? "window"));
        }
    }
}
