using Microsoft.AspNetCore.SignalR.Client;

namespace AgentDesk.Cloud.Client;

internal sealed record CloudNotificationConnectionOptions(
    Uri HubUri,
    string AuthorizationHeader,
    IReadOnlyList<TimeSpan> ReconnectDelays)
{
    public override string ToString() =>
        $"CloudNotificationConnectionOptions {{ HubUri = {HubUri}, " +
        $"Authorization = Present, ReconnectAttempts = {ReconnectDelays.Count} }}";
}

internal interface ICloudNotificationConnectionFactory
{
    ICloudNotificationConnection Create(CloudNotificationConnectionOptions options);
}

internal interface ICloudNotificationConnection : IAsyncDisposable
{
    IDisposable On<TPayload>(string method, Func<TPayload, Task> handler);

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

internal sealed class SignalRCloudNotificationConnectionFactory :
    ICloudNotificationConnectionFactory
{
    public static SignalRCloudNotificationConnectionFactory Instance { get; } = new();

    private SignalRCloudNotificationConnectionFactory()
    {
    }

    public ICloudNotificationConnection Create(CloudNotificationConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connection = new HubConnectionBuilder()
            .WithUrl(
                options.HubUri,
                transport => transport.Headers["Authorization"] = options.AuthorizationHeader)
            .WithAutomaticReconnect(options.ReconnectDelays.ToArray())
            .Build();
        return new SignalRCloudNotificationConnection(connection);
    }
}

internal sealed class SignalRCloudNotificationConnection(HubConnection connection) :
    ICloudNotificationConnection
{
    private readonly HubConnection _connection = connection ??
        throw new ArgumentNullException(nameof(connection));

    public IDisposable On<TPayload>(string method, Func<TPayload, Task> handler) =>
        _connection.On(method, handler);

    public Task StartAsync(CancellationToken cancellationToken) =>
        _connection.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _connection.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
