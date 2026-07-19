namespace AgentDesk.Cloud.Client;

public interface ICloudNotificationClient : IAsyncDisposable
{
    Task StartAsync(
        Func<CloudNotification, Task> notificationHandler,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
