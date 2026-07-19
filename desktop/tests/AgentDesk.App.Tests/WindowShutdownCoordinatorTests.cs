using System.Reflection;
using System.Runtime.CompilerServices;
using AgentDesk.App.Windowing;

namespace AgentDesk.App.Tests;

public sealed class WindowShutdownCoordinatorTests
{
    [Fact]
    public void MainWindow_UsesCancelableClosingAndKeepsClosedSynchronous()
    {
        var closing = typeof(MainWindow).GetMethod(
            "MainWindow_Closing",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var closed = typeof(MainWindow).GetMethod(
            "MainWindow_Closed",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(closing);
        Assert.Contains(
            closing.GetParameters(),
            parameter => parameter.ParameterType.Name == "AppWindowClosingEventArgs");
        Assert.NotNull(closed);
        Assert.Null(closed.GetCustomAttribute<AsyncStateMachineAttribute>());
    }

    [Fact]
    public async Task RequestShutdownAsync_KeepsCloseBlockedUntilCallerAuthorizesIt()
    {
        var cleanupStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = CreateCoordinator(async () =>
        {
            cleanupStarted.SetResult();
            await releaseCleanup.Task;
        });

        var shutdown = coordinator.RequestShutdownAsync();
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(coordinator.TryConsumeCloseAuthorization());
        Assert.False(shutdown.IsCompleted);

        releaseCleanup.SetResult();
        var result = await shutdown.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Succeeded);
        Assert.False(coordinator.TryConsumeCloseAuthorization());
        Assert.True(coordinator.TryAuthorizeClose());
        Assert.False(coordinator.TryAuthorizeClose());
        Assert.True(coordinator.TryConsumeCloseAuthorization());
        Assert.False(coordinator.TryConsumeCloseAuthorization());
    }

    [Fact]
    public async Task RequestShutdownAsync_DeduplicatesConcurrentCloseRequests()
    {
        var cleanupStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var coordinator = CreateCoordinator(async () =>
        {
            attempts++;
            cleanupStarted.SetResult();
            await releaseCleanup.Task;
        });

        var first = coordinator.RequestShutdownAsync();
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.RequestShutdownAsync();

        Assert.Same(first, second);
        Assert.Equal(1, attempts);

        releaseCleanup.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RequestShutdownAsync_RetriesCleanupWithinTheConfiguredBound()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var coordinator = new WindowShutdownCoordinator(
            () =>
            {
                attempts++;
                return attempts < 3
                    ? ValueTask.FromException(new InvalidOperationException("cleanup failed"))
                    : ValueTask.CompletedTask;
            },
            maxAttempts: 3,
            retryDelay: TimeSpan.FromMilliseconds(25),
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await coordinator.RequestShutdownAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(3, attempts);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25)],
            delays);
        Assert.True(coordinator.TryAuthorizeClose());
        Assert.True(coordinator.TryConsumeCloseAuthorization());
    }

    [Fact]
    public async Task RequestShutdownAsync_AfterRetriesFail_RemainsBlockedAndCanBeRetried()
    {
        var attempts = 0;
        var allowCleanup = false;
        var coordinator = CreateCoordinator(() =>
        {
            attempts++;
            return allowCleanup
                ? ValueTask.CompletedTask
                : ValueTask.FromException(new InvalidOperationException("cleanup failed"));
        });

        var failed = await coordinator.RequestShutdownAsync();

        Assert.False(failed.Succeeded);
        Assert.IsType<InvalidOperationException>(failed.Error);
        Assert.Equal(3, attempts);
        Assert.True(coordinator.ShouldReportFailure(failed));
        Assert.False(coordinator.TryAuthorizeClose());
        Assert.False(coordinator.TryConsumeCloseAuthorization());

        allowCleanup = true;
        var succeeded = await coordinator.RequestShutdownAsync();

        Assert.True(succeeded.Succeeded);
        Assert.Equal(4, attempts);
        Assert.False(coordinator.ShouldReportFailure(failed));
        Assert.True(coordinator.TryAuthorizeClose());
        Assert.True(coordinator.TryConsumeCloseAuthorization());
    }

    [Fact]
    public async Task RequestShutdownAsync_NewAttemptMakesAnOlderFailureStale()
    {
        var secondCleanupStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = 0;
        var coordinator = new WindowShutdownCoordinator(
            async () =>
            {
                attempt++;
                if (attempt == 1)
                {
                    throw new InvalidOperationException("first cleanup failed");
                }

                secondCleanupStarted.SetResult();
                await releaseSecondCleanup.Task;
            },
            maxAttempts: 1,
            retryDelay: TimeSpan.Zero,
            delayAsync: (_, _) => Task.CompletedTask);

        var firstTask = coordinator.RequestShutdownAsync();
        var firstResult = await firstTask;

        Assert.True(firstTask.IsCompleted);
        Assert.True(coordinator.ShouldReportFailure(firstResult));

        var secondTask = coordinator.RequestShutdownAsync();
        await secondCleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotSame(firstTask, secondTask);
        Assert.False(coordinator.ShouldReportFailure(firstResult));

        releaseSecondCleanup.SetResult();
        var secondResult = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(secondResult.Succeeded);
        Assert.True(coordinator.TryAuthorizeClose());
        Assert.True(coordinator.TryConsumeCloseAuthorization());
    }

    private static WindowShutdownCoordinator CreateCoordinator(Func<ValueTask> cleanupAsync) =>
        new(
            cleanupAsync,
            maxAttempts: 3,
            retryDelay: TimeSpan.Zero,
            delayAsync: (_, _) => Task.CompletedTask);
}
