using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AgentDesk.Cloud;

[Authorize(AuthenticationSchemes = CloudAuthenticationHandler.SchemeName)]
internal sealed class CloudNotificationHub(
    CloudNotificationConnectionRegistry connectionRegistry) : Hub
{
    private const string RegistrationKey = "agentdesk.notification-registration";
    public const string Route = "/hubs/notifications";
    public const string HandoffChangedEvent = "handoffChanged";
    public const string JobChangedEvent = "jobChanged";
    public const string PolicyChangedEvent = "policyChanged";

    public override async Task OnConnectedAsync()
    {
        var identity = Context.User!.CloudIdentity();
        var registration = connectionRegistry.Register(
            identity.TeamId,
            identity.SubjectId,
            Context.ConnectionId,
            Context.Abort);
        Context.Items[RegistrationKey] = registration;
        if (registration.Rejected)
        {
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, TeamGroup(identity.TeamId));
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            SubjectGroup(identity.TeamId, identity.SubjectId));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.Remove(RegistrationKey, out var registration))
        {
            (registration as IDisposable)?.Dispose();
        }
        await base.OnDisconnectedAsync(exception);
    }

    internal static string TeamGroup(string teamId) => $"team:{teamId}";

    internal static string SubjectGroup(string teamId, string subjectId) =>
        $"subject:{teamId}:{subjectId}";
}

internal sealed class CloudNotifier(
    IHubContext<CloudNotificationHub> hubContext,
    ILogger<CloudNotifier> logger)
{
    public Task HandoffChangedAsync(
        string teamId,
        string targetDeviceId,
        string handoffId,
        CancellationToken cancellationToken) =>
        SendAsync(
            hubContext.Clients.Group(
                CloudNotificationHub.SubjectGroup(teamId, targetDeviceId)),
            CloudNotificationHub.HandoffChangedEvent,
            new HandoffChangedNotification(teamId, targetDeviceId, handoffId),
            cancellationToken);

    public Task JobChangedAsync(
        string teamId,
        string jobId,
        CancellationToken cancellationToken) =>
        SendAsync(
            hubContext.Clients.Group(CloudNotificationHub.TeamGroup(teamId)),
            CloudNotificationHub.JobChangedEvent,
            new JobChangedNotification(teamId, jobId),
            cancellationToken);

    public Task PolicyChangedAsync(
        string teamId,
        int version,
        CancellationToken cancellationToken) =>
        SendAsync(
            hubContext.Clients.Group(CloudNotificationHub.TeamGroup(teamId)),
            CloudNotificationHub.PolicyChangedEvent,
            new PolicyChangedNotification(teamId, version),
            cancellationToken);

    private async Task SendAsync(
        IClientProxy clients,
        string method,
        object payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await clients.SendAsync(method, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Cloud notification {Method} could not be delivered.", method);
        }
    }
}

internal sealed record HandoffChangedNotification(
    string TeamId,
    string TargetDeviceId,
    string HandoffId);

internal sealed record JobChangedNotification(string TeamId, string JobId);

internal sealed record PolicyChangedNotification(string TeamId, int Version);
