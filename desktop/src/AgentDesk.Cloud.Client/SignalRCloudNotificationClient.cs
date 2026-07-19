namespace AgentDesk.Cloud.Client;

public sealed class SignalRCloudNotificationClient : ICloudNotificationClient
{
    private static readonly IReadOnlyList<TimeSpan> DefaultReconnectDelays =
        Array.AsReadOnly(
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(10),
        ]);

    private readonly CloudConnectionProfile _profile;
    private readonly ICloudAccessTokenProvider _tokenProvider;
    private readonly ICloudNotificationConnectionFactory _connectionFactory;
    private readonly TimeSpan _shutdownTimeout;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private ICloudNotificationConnection? _connection;
    private IReadOnlyList<IDisposable> _registrations = [];
    private Func<CloudNotification, Task>? _notificationHandler;
    private long _generation;
    private int _disposed;

    public SignalRCloudNotificationClient(
        CloudConnectionProfile profile,
        ICloudAccessTokenProvider tokenProvider)
        : this(
            profile,
            tokenProvider,
            SignalRCloudNotificationConnectionFactory.Instance,
            TimeSpan.FromSeconds(5))
    {
    }

    internal SignalRCloudNotificationClient(
        CloudConnectionProfile profile,
        ICloudAccessTokenProvider tokenProvider,
        ICloudNotificationConnectionFactory connectionFactory,
        TimeSpan? shutdownTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        if (profile.IsLocalOnly)
        {
            throw new ArgumentException(
                "A remote cloud profile is required for notifications.",
                nameof(profile));
        }

        _profile = profile;
        _tokenProvider = tokenProvider;
        _connectionFactory = connectionFactory;
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5);
        if (_shutdownTimeout <= TimeSpan.Zero || _shutdownTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));
        }
    }

    public async Task StartAsync(
        Func<CloudNotification, Task> notificationHandler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notificationHandler);
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var startCancellationToken = linkedCancellation.Token;
        await _lifecycleGate.WaitAsync(startCancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_connection is not null)
            {
                if (!ReferenceEquals(_notificationHandler, notificationHandler))
                {
                    throw new InvalidOperationException(
                        "The cloud notification client is already running.");
                }
                return;
            }

            var accessToken = await _tokenProvider
                .GetAccessTokenAsync(startCancellationToken)
                .ConfigureAwait(false);
            var options = CreateConnectionOptions(accessToken);
            var connection = _connectionFactory.Create(options);
            var generation = checked(_generation + 1);
            var registrations = RegisterHandlers(connection, generation);
            _generation = generation;
            _notificationHandler = notificationHandler;
            try
            {
                await connection
                    .StartAsync(startCancellationToken)
                    .WaitAsync(startCancellationToken)
                    .ConfigureAwait(false);
                _connection = connection;
                _registrations = registrations;
            }
            catch
            {
                _generation = checked(_generation + 1);
                _notificationHandler = null;
                DisposeRegistrations(registrations);
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await StopCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();

        try
        {
            using var timeout = new CancellationTokenSource(_shutdownTimeout);
            try
            {
                await StopCoreAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // Connection disposal below still releases transport resources.
            }
        }
        finally
        {
            _lifetimeCancellation.Dispose();
        }
    }

    public override string ToString() =>
        $"SignalRCloudNotificationClient {{ BaseUri = {_profile.BaseUri} }}";

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is null)
            {
                return;
            }

            var connection = _connection;
            var registrations = _registrations;
            _connection = null;
            _registrations = [];
            _notificationHandler = null;
            _generation = checked(_generation + 1);
            DisposeRegistrations(registrations);
            try
            {
                await connection
                    .StopAsync(cancellationToken)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private IReadOnlyList<IDisposable> RegisterHandlers(
        ICloudNotificationConnection connection,
        long generation) =>
    [
        connection.On<CloudHandoffNotificationPayload>(
            CloudNotificationMethods.HandoffChanged,
            payload => DispatchAsync(
                generation,
                CloudNotificationValidator.ValidateHandoff(_profile, payload))),
        connection.On<CloudJobNotificationPayload>(
            CloudNotificationMethods.JobChanged,
            payload => DispatchAsync(
                generation,
                CloudNotificationValidator.ValidateJob(_profile, payload))),
        connection.On<CloudPolicyNotificationPayload>(
            CloudNotificationMethods.PolicyChanged,
            payload => DispatchAsync(
                generation,
                CloudNotificationValidator.ValidatePolicy(_profile, payload))),
    ];

    private async Task DispatchAsync(long generation, CloudNotification? notification)
    {
        if (notification is null || Volatile.Read(ref _generation) != generation)
        {
            return;
        }

        var handler = Volatile.Read(ref _notificationHandler);
        if (handler is null)
        {
            return;
        }

        try
        {
            await handler(notification).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A projection failure must not terminate or leak details through the transport.
        }
    }

    private CloudNotificationConnectionOptions CreateConnectionOptions(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken) ||
            accessToken.Length > 8 * 1024 ||
            accessToken.Any(char.IsWhiteSpace))
        {
            throw new CloudAccessTokenStoreException();
        }

        var hubUri = new Uri(
            _profile.CreateConnectionOptions().BaseUri,
            "hubs/notifications");
        return new CloudNotificationConnectionOptions(
            hubUri,
            $"Bearer {accessToken}",
            DefaultReconnectDelays);
    }

    private static void DisposeRegistrations(IReadOnlyList<IDisposable> registrations)
    {
        foreach (var registration in registrations)
        {
            registration.Dispose();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
