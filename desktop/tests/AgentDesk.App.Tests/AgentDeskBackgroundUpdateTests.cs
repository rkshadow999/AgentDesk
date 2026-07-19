using System.Collections.Concurrent;
using AgentDesk.App.Bridge;
using AgentDesk.App.Maintenance;
using AgentDesk.Updater.Core;

namespace AgentDesk.App.Tests;

public sealed class AgentDeskBackgroundUpdateTests
{
    [Fact]
    public void OptionsUseABoundedSixHourPortableSchedule()
    {
        var options = AgentDeskBackgroundUpdateMonitorOptions.CreateDefault(
            AgentDeskPackageMode.Portable);

        Assert.Equal(TimeSpan.FromSeconds(30), options.InitialDelay);
        Assert.Equal(TimeSpan.FromHours(6), options.CheckInterval);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AgentDeskBackgroundUpdateMonitorOptions(
                AgentDeskPackageMode.Portable,
                TimeSpan.FromMinutes(-1),
                TimeSpan.FromHours(6)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AgentDeskBackgroundUpdateMonitorOptions(
                AgentDeskPackageMode.Portable,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(14)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AgentDeskBackgroundUpdateMonitorOptions(
                AgentDeskPackageMode.Portable,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromHours(25)));
    }

    [Fact]
    public async Task CheckCoordinatorPublishesOnlyDistinctTrustedStagedVersions()
    {
        var checker = new ScriptedUpdateChecker(
            _ => Task.FromException<AgentDeskUpdateAvailability?>(
                new IOException("private update endpoint failed")),
            _ => Task.FromResult<AgentDeskUpdateAvailability?>(null),
            _ => Available("2.0.0"),
            _ => Available("2.0.0"),
            _ => Available("3.0.0"));
        var published = new List<WebEvent>();
        var coordinator = new AgentDeskBackgroundUpdateCheckCoordinator(
            checker,
            webEvent =>
            {
                published.Add(webEvent);
                return Task.CompletedTask;
            });

        for (var index = 0; index < 5; index++)
        {
            await coordinator.CheckAsync();
        }

        Assert.Collection(
            published,
            webEvent => Assert.Equal(
                "2.0.0",
                Assert.IsType<BackgroundUpdateAvailableWebEvent>(webEvent).Version),
            webEvent => Assert.Equal(
                "3.0.0",
                Assert.IsType<BackgroundUpdateAvailableWebEvent>(webEvent).Version));
    }

    [Fact]
    public async Task CheckCoordinatorSerializesConcurrentChecks()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var calls = 0;
        var checker = new CallbackUpdateChecker(async cancellationToken =>
        {
            var call = Interlocked.Increment(ref calls);
            var current = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximumActive, current);
            try
            {
                if (call == 1)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                return null;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        var coordinator = new AgentDeskBackgroundUpdateCheckCoordinator(
            checker,
            _ => Task.CompletedTask);

        var first = coordinator.CheckAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.CheckAsync();
        await Task.Yield();

        Assert.Equal(1, calls);
        Assert.False(second.IsCompleted);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, calls);
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task PortableMonitorWaitsForInitialDelayThenChecksPeriodically()
    {
        var delay = new ControlledDelay();
        var check = new CountingBackgroundCheck();
        await using var monitor = new AgentDeskBackgroundUpdateMonitor(
            check,
            new AgentDeskBackgroundUpdateMonitorOptions(
                AgentDeskPackageMode.Portable,
                TimeSpan.FromSeconds(45),
                TimeSpan.FromHours(6)),
            delay.DelayAsync);

        Assert.True(monitor.Start());
        Assert.False(monitor.Start());
        var initial = await delay.NextAsync();

        Assert.Equal(TimeSpan.FromSeconds(45), initial.Delay);
        Assert.Equal(0, check.Count);

        initial.Release();
        await check.WaitForCountAsync(1);
        var periodic = await delay.NextAsync();

        Assert.Equal(TimeSpan.FromHours(6), periodic.Delay);
        Assert.Equal(1, check.Count);

        await monitor.StopAsync();
        Assert.True(periodic.CancellationRequested);
    }

    [Fact]
    public async Task PortableMonitorRequiresOptInAndCanBeDisabledThenReenabled()
    {
        var delay = new ControlledDelay();
        var check = new CountingBackgroundCheck();
        await using var monitor = new AgentDeskBackgroundUpdateMonitor(
            check,
            new AgentDeskBackgroundUpdateMonitorOptions(
                AgentDeskPackageMode.Portable,
                TimeSpan.FromSeconds(45),
                TimeSpan.FromHours(6)),
            delay.DelayAsync);

        await Task.Yield();
        Assert.Equal(0, delay.Count);
        Assert.Equal(0, check.Count);

        await monitor.SetEnabledAsync(true);
        var firstInitialDelay = await delay.NextAsync();
        firstInitialDelay.Release();
        await check.WaitForCountAsync(1);
        var firstPeriodicDelay = await delay.NextAsync();

        await monitor.SetEnabledAsync(false);
        Assert.True(firstPeriodicDelay.CancellationRequested);

        await monitor.SetEnabledAsync(true);
        var secondInitialDelay = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(45), secondInitialDelay.Delay);
        secondInitialDelay.Release();
        await check.WaitForCountAsync(2);

        await monitor.SetEnabledAsync(false);
    }

    [Fact]
    public async Task MsixMonitorNeverSchedulesPortableUpdateWork()
    {
        var delay = new ControlledDelay();
        var check = new CountingBackgroundCheck();
        await using var monitor = new AgentDeskBackgroundUpdateMonitor(
            check,
            AgentDeskBackgroundUpdateMonitorOptions.CreateDefault(AgentDeskPackageMode.Msix),
            delay.DelayAsync);

        Assert.False(monitor.Start());
        await Task.Yield();

        Assert.Equal(0, delay.Count);
        Assert.Equal(0, check.Count);
    }

    [Fact]
    public async Task StopAsyncCancelsAnInFlightBackgroundCheck()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var check = new CallbackBackgroundCheck(async cancellationToken =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                throw;
            }
        });
        await using var monitor = new AgentDeskBackgroundUpdateMonitor(
            check,
            AgentDeskBackgroundUpdateMonitorOptions.CreateDefault(
                AgentDeskPackageMode.Portable),
            (_, cancellationToken) => Task.Delay(TimeSpan.Zero, cancellationToken));

        Assert.True(monitor.Start());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await monitor.StopAsync();

        Assert.True(cancelled);
    }

    private static Task<AgentDeskUpdateAvailability?> Available(string version) =>
        Task.FromResult<AgentDeskUpdateAvailability?>(
            new AgentDeskUpdateAvailability(SemanticVersion.Parse(version)));

    private sealed class ScriptedUpdateChecker(
        params Func<CancellationToken, Task<AgentDeskUpdateAvailability?>>[] checks)
        : IAgentDeskUpdateChecker
    {
        private readonly Queue<Func<CancellationToken, Task<AgentDeskUpdateAvailability?>>> _checks =
            new(checks);

        public Task<AgentDeskUpdateAvailability?> CheckAsync(
            CancellationToken cancellationToken = default) =>
            _checks.Dequeue()(cancellationToken);
    }

    private sealed class CallbackUpdateChecker(
        Func<CancellationToken, Task<AgentDeskUpdateAvailability?>> check)
        : IAgentDeskUpdateChecker
    {
        public Task<AgentDeskUpdateAvailability?> CheckAsync(
            CancellationToken cancellationToken = default) => check(cancellationToken);
    }

    private sealed class CountingBackgroundCheck : IAgentDeskBackgroundUpdateCheck
    {
        private readonly SemaphoreSlim _called = new(0);

        public int Count { get; private set; }

        public Task CheckAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            _called.Release();
            return Task.CompletedTask;
        }

        public async Task WaitForCountAsync(int expected)
        {
            while (Count < expected)
            {
                await _called.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private sealed class CallbackBackgroundCheck(
        Func<CancellationToken, Task> check) : IAgentDeskBackgroundUpdateCheck
    {
        public Task CheckAsync(CancellationToken cancellationToken = default) =>
            check(cancellationToken);
    }

    private sealed class ControlledDelay
    {
        private readonly ConcurrentQueue<DelayCall> _calls = new();
        private readonly SemaphoreSlim _available = new(0);

        public int Count => _calls.Count;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var call = new DelayCall(delay, cancellationToken);
            _calls.Enqueue(call);
            _available.Release();
            return call.Completion;
        }

        public async Task<DelayCall> NextAsync()
        {
            Assert.True(await _available.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(_calls.TryDequeue(out var call));
            return call!;
        }
    }

    private sealed class DelayCall
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        public DelayCall(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delay = delay;
            _registration = cancellationToken.Register(() =>
                _completion.TrySetCanceled(cancellationToken));
        }

        public TimeSpan Delay { get; }

        public Task Completion => _completion.Task;

        public bool CancellationRequested => _completion.Task.IsCanceled;

        public void Release()
        {
            _registration.Dispose();
            _completion.TrySetResult();
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (current < value)
            {
                var previous = Interlocked.CompareExchange(ref location, value, current);
                if (previous == current)
                {
                    return;
                }
                current = previous;
            }
        }
    }
}
