using AgentDesk.App.Windowing;

namespace AgentDesk.App.Tests;

public sealed class ContentDialogQueueTests
{
    [Fact]
    public async Task EnqueueAsync_SerializesDialogs()
    {
        var queue = new ContentDialogQueue();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var first = queue.EnqueueAsync(
            async cancellationToken =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return 1;
            },
            shutdownResult: -1,
            CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = queue.EnqueueAsync(
            _ =>
            {
                secondStarted.TrySetResult();
                return Task.FromResult(2);
            },
            shutdownResult: -1,
            CancellationToken.None);

        await Task.Yield();
        Assert.False(secondStarted.Task.IsCompleted);
        releaseFirst.TrySetResult();

        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
        Assert.True(secondStarted.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Close_RejectsActivePendingAndFutureDialogs()
    {
        var queue = new ContentDialogQueue();
        var activeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingInvoked = false;

        var active = queue.EnqueueAsync(
            async cancellationToken =>
            {
                activeStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            },
            shutdownResult: false,
            CancellationToken.None);
        await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = queue.EnqueueAsync(
            _ =>
            {
                pendingInvoked = true;
                return Task.FromResult(true);
            },
            shutdownResult: false,
            CancellationToken.None);

        queue.Close();

        Assert.False(await active);
        Assert.False(await pending);
        Assert.False(pendingInvoked);
        Assert.False(await queue.EnqueueAsync(
            _ => Task.FromResult(true),
            shutdownResult: false,
            CancellationToken.None));
    }

    [Fact]
    public async Task EnqueueAsync_PropagatesCallerCancellation()
    {
        var queue = new ContentDialogQueue();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.EnqueueAsync(
            _ => Task.FromResult(true),
            shutdownResult: false,
            cancellation.Token));
    }

    [Fact]
    public async Task Close_TakesPrecedenceOverConcurrentCallerCancellation()
    {
        var queue = new ContentDialogQueue();
        using var cancellation = new CancellationTokenSource();
        var activeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = queue.EnqueueAsync(
            async token =>
            {
                activeStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            },
            shutdownResult: false,
            cancellation.Token);
        await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        queue.Close();
        cancellation.Cancel();

        Assert.False(await active);
        Assert.False(await queue.EnqueueAsync(
            _ => Task.FromResult(true),
            shutdownResult: false,
            cancellation.Token));
    }
}
