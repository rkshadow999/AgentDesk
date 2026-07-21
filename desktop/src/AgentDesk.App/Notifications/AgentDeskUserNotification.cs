namespace AgentDesk.App.Notifications;

public enum AgentDeskNotificationKind
{
    TaskCompleted,
    TaskFailed,
    PermissionRequired,
}

public sealed class AgentDeskUserNotification
{
    public const int MaximumSessionLabelLength = 80;

    public AgentDeskUserNotification(
        string sessionId,
        AgentDeskNotificationKind kind,
        string? sessionLabel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!IsValidSessionId(sessionId) || !Enum.IsDefined(kind))
        {
            throw new ArgumentException("The notification session or kind is invalid.");
        }
        SessionId = sessionId;
        Kind = kind;
        SessionLabel = SanitizeSessionLabel(sessionLabel);
    }

    public string SessionId { get; }

    public AgentDeskNotificationKind Kind { get; }

    /// <summary>
    /// Optional human-readable session title. Never includes prompt text or file bodies.
    /// </summary>
    public string? SessionLabel { get; }

    public string Title(string language) => (language, Kind) switch
    {
        ("zh-CN", AgentDeskNotificationKind.TaskCompleted) => "会话已完成",
        ("zh-CN", AgentDeskNotificationKind.TaskFailed) => "会话执行失败",
        ("zh-CN", AgentDeskNotificationKind.PermissionRequired) => "需要权限确认",
        (_, AgentDeskNotificationKind.TaskCompleted) => "Session completed",
        (_, AgentDeskNotificationKind.TaskFailed) => "Session failed",
        (_, AgentDeskNotificationKind.PermissionRequired) => "Permission required",
        _ => throw new ArgumentOutOfRangeException(),
    };

    public string Body(string language)
    {
        var label = SessionLabel;
        if (language == "zh-CN")
        {
            return Kind switch
            {
                AgentDeskNotificationKind.TaskCompleted => label is null
                    ? "一个会话的任务已完成。点击可打开该会话。"
                    : $"「{label}」已完成。点击可打开该会话。",
                AgentDeskNotificationKind.TaskFailed => label is null
                    ? "一个会话的任务失败。点击可打开该会话。"
                    : $"「{label}」执行失败。点击可打开该会话。",
                AgentDeskNotificationKind.PermissionRequired => label is null
                    ? "有会话正在等待权限确认。点击可打开该会话。"
                    : $"「{label}」正在等待权限确认。点击可打开。",
                _ => throw new ArgumentOutOfRangeException(),
            };
        }

        return Kind switch
        {
            AgentDeskNotificationKind.TaskCompleted => label is null
                ? "A session finished. Click to open it."
                : $"\"{label}\" finished. Click to open it.",
            AgentDeskNotificationKind.TaskFailed => label is null
                ? "A session failed. Click to open it."
                : $"\"{label}\" failed. Click to open it.",
            AgentDeskNotificationKind.PermissionRequired => label is null
                ? "A session needs permission. Click to open it."
                : $"\"{label}\" needs permission. Click to open it.",
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    public override string ToString() =>
        $"AgentDeskUserNotification {{ SessionId = {SessionId}, Kind = {Kind}, Label = {SessionLabel ?? "(none)"} }}";

    internal static bool IsValidSessionId(string sessionId) =>
        sessionId.Length is > 0 and <= 128 &&
        sessionId.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    internal static string? SanitizeSessionLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var builder = new System.Text.StringBuilder(label.Length);
        foreach (var character in label.Trim())
        {
            if (char.IsControl(character))
            {
                continue;
            }
            builder.Append(character);
            if (builder.Length >= MaximumSessionLabelLength)
            {
                break;
            }
        }

        if (builder.Length == 0)
        {
            return null;
        }

        if (builder.Length == MaximumSessionLabelLength)
        {
            builder.Append('…');
        }

        return builder.ToString();
    }
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
