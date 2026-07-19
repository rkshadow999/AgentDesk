using AgentDesk.Cloud.Client;

namespace AgentDesk.Cloud.Client.Tests;

public sealed class SignalRCloudNotificationClientTests
{
    private static readonly CloudConnectionProfile Profile = new(
        new Uri("https://cloud.example.test/root/"),
        "team-1",
        "device-1");

    [Fact]
    public async Task StartUsesAnAuthorizationHeaderAndBoundedReconnectWithoutAQueryToken()
    {
        const string token = "test-token-must-not-appear-in-a-uri";
        var factory = new RecordingConnectionFactory();
        await using var client = new SignalRCloudNotificationClient(
            Profile,
            new StaticAccessTokenProvider(token),
            factory);

        await client.StartAsync(_ => Task.CompletedTask);

        var options = Assert.IsType<CloudNotificationConnectionOptions>(factory.Options);
        Assert.Equal(
            new Uri("https://cloud.example.test/root/hubs/notifications"),
            options.HubUri);
        Assert.Empty(options.HubUri.Query);
        Assert.DoesNotContain(token, options.HubUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal($"Bearer {token}", options.AuthorizationHeader);
        Assert.InRange(options.ReconnectDelays.Count, 1, 5);
        Assert.All(
            options.ReconnectDelays,
            delay => Assert.InRange(delay, TimeSpan.Zero, TimeSpan.FromSeconds(30)));
        Assert.DoesNotContain(token, options.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, factory.Connection.StartCount);
    }

    [Fact]
    public async Task IncomingEventsAreValidatedBeforeTheyReachTheSubscriber()
    {
        var received = new List<CloudNotification>();
        var factory = new RecordingConnectionFactory();
        await using var client = new SignalRCloudNotificationClient(
            Profile,
            new StaticAccessTokenProvider("test-token"),
            factory);
        await client.StartAsync(notification =>
        {
            received.Add(notification);
            return Task.CompletedTask;
        });

        await factory.Connection.DeliverAsync(
            CloudNotificationMethods.HandoffChanged,
            new CloudHandoffNotificationPayload("team-other", "device-1", "handoff-wrong-team"));
        await factory.Connection.DeliverAsync(
            CloudNotificationMethods.HandoffChanged,
            new CloudHandoffNotificationPayload("team-1", "device-other", "handoff-wrong-device"));
        await factory.Connection.DeliverAsync(
            CloudNotificationMethods.HandoffChanged,
            new CloudHandoffNotificationPayload("team-1", "device-1", "handoff-1"));
        await factory.Connection.DeliverAsync(
            CloudNotificationMethods.JobChanged,
            new CloudJobNotificationPayload("team-1", "job-1"));
        await factory.Connection.DeliverAsync(
            CloudNotificationMethods.PolicyChanged,
            new CloudPolicyNotificationPayload("team-1", 7));

        Assert.Equal(
            [
                new CloudNotification(CloudNotificationKind.HandoffChanged, "handoff-1"),
                new CloudNotification(CloudNotificationKind.JobChanged, "job-1"),
                new CloudNotification(
                    CloudNotificationKind.PolicyChanged,
                    ResourceId: null,
                    PolicyVersion: 7),
            ],
            received);
    }

    [Fact]
    public async Task StopIsIdempotentAndPreventsFurtherDelivery()
    {
        var received = new List<CloudNotification>();
        var factory = new RecordingConnectionFactory();
        await using var client = new SignalRCloudNotificationClient(
            Profile,
            new StaticAccessTokenProvider("test-token"),
            factory);
        await client.StartAsync(notification =>
        {
            received.Add(notification);
            return Task.CompletedTask;
        });

        await client.StopAsync();
        await client.StopAsync();
        await factory.Connection.DeliverAsync(
            CloudNotificationMethods.JobChanged,
            new CloudJobNotificationPayload("team-1", "job-after-stop"));

        Assert.Empty(received);
        Assert.Equal(1, factory.Connection.StopCount);
        Assert.Equal(1, factory.Connection.DisposeCount);
    }

    [Fact]
    public async Task CancelledStartDisposesTheIncompleteConnection()
    {
        var factory = new RecordingConnectionFactory
        {
            Connection = { BlockStartUntilCancellation = true },
        };
        await using var client = new SignalRCloudNotificationClient(
            Profile,
            new StaticAccessTokenProvider("test-token"),
            factory);
        using var cancellation = new CancellationTokenSource();

        var start = client.StartAsync(_ => Task.CompletedTask, cancellation.Token);
        await factory.Connection.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(1, factory.Connection.DisposeCount);
    }

    [Fact]
    public async Task DisposeCancelsAnInFlightStartAndWaitsForItsCleanup()
    {
        var factory = new RecordingConnectionFactory
        {
            Connection = { BlockStartUntilCancellation = true },
        };
        var client = new SignalRCloudNotificationClient(
            Profile,
            new StaticAccessTokenProvider("test-token"),
            factory);
        using var callerCancellation = new CancellationTokenSource();
        var start = client.StartAsync(_ => Task.CompletedTask, callerCancellation.Token);
        await factory.Connection.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var dispose = client.DisposeAsync().AsTask();
        try
        {
            await dispose.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            callerCancellation.Cancel();
            try
            {
                await start;
            }
            catch (OperationCanceledException)
            {
            }
            await dispose;
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(1, factory.Connection.DisposeCount);
    }

    [Fact]
    public async Task DisposeTimesOutANonCooperativeStopAndStillReleasesTheConnection()
    {
        var factory = new RecordingConnectionFactory
        {
            Connection = { BlockStopForever = true },
        };
        var client = new SignalRCloudNotificationClient(
            Profile,
            new StaticAccessTokenProvider("test-token"),
            factory,
            shutdownTimeout: TimeSpan.FromMilliseconds(50));
        await client.StartAsync(_ => Task.CompletedTask);

        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, factory.Connection.StopCount);
        Assert.Equal(1, factory.Connection.DisposeCount);
    }

    private sealed class StaticAccessTokenProvider(string token) : ICloudAccessTokenProvider
    {
        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(token);
        }
    }

    private sealed class RecordingConnectionFactory : ICloudNotificationConnectionFactory
    {
        public CloudNotificationConnectionOptions? Options { get; private set; }

        public RecordingConnection Connection { get; } = new();

        public ICloudNotificationConnection Create(CloudNotificationConnectionOptions options)
        {
            Options = options;
            return Connection;
        }
    }

    private sealed class RecordingConnection : ICloudNotificationConnection
    {
        private readonly Dictionary<string, Delegate> _handlers = new(StringComparer.Ordinal);

        public TaskCompletionSource StartEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BlockStartUntilCancellation { get; set; }

        public bool BlockStopForever { get; set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public IDisposable On<TPayload>(string method, Func<TPayload, Task> handler)
        {
            _handlers.Add(method, handler);
            return new CallbackRegistration(() => _handlers.Remove(method));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            StartEntered.TrySetResult();
            if (BlockStartUntilCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            if (BlockStopForever)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _handlers.Clear();
            return ValueTask.CompletedTask;
        }

        public Task DeliverAsync<TPayload>(string method, TPayload payload)
        {
            return _handlers.TryGetValue(method, out var handler)
                ? ((Func<TPayload, Task>)handler)(payload)
                : Task.CompletedTask;
        }

        private sealed class CallbackRegistration(Action dispose) : IDisposable
        {
            private Action? _dispose = dispose;

            public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}
