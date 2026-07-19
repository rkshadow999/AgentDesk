namespace AgentDesk.App.Notifications;

public enum AgentDeskNotificationKind
{
    TaskCompleted,
    TaskFailed,
    PermissionRequired,
}

public sealed class AgentDeskUserNotification
{
    public AgentDeskUserNotification(string sessionId, AgentDeskNotificationKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!IsValidSessionId(sessionId) || !Enum.IsDefined(kind))
        {
            throw new ArgumentException("The notification session or kind is invalid.");
        }
        SessionId = sessionId;
        Kind = kind;
    }

    public string SessionId { get; }

    public AgentDeskNotificationKind Kind { get; }

    public string Title(string language) => (language, Kind) switch
    {
        ("zh-CN", AgentDeskNotificationKind.TaskCompleted) => "任务已完成",
        ("zh-CN", AgentDeskNotificationKind.TaskFailed) => "任务执行失败",
        ("zh-CN", AgentDeskNotificationKind.PermissionRequired) => "需要权限确认",
        (_, AgentDeskNotificationKind.TaskCompleted) => "Task completed",
        (_, AgentDeskNotificationKind.TaskFailed) => "Task failed",
        (_, AgentDeskNotificationKind.PermissionRequired) => "Permission required",
        _ => throw new ArgumentOutOfRangeException(),
    };

    public string Body(string language) => language == "zh-CN"
        ? $"AgentDesk 会话 {SessionId} 的状态已更新。"
        : $"AgentDesk session {SessionId} has a status update.";

    public override string ToString() =>
        $"AgentDeskUserNotification {{ SessionId = {SessionId}, Kind = {Kind} }}";

    internal static bool IsValidSessionId(string sessionId) =>
        sessionId.Length is > 0 and <= 128 &&
        sessionId.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
}

public interface IUserNotificationService
{
    Task ShowAsync(
        AgentDeskUserNotification notification,
        string language,
        CancellationToken cancellationToken = default);
}

public sealed class NullUserNotificationService : IUserNotificationService
{
    public Task ShowAsync(
        AgentDeskUserNotification notification,
        string language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
