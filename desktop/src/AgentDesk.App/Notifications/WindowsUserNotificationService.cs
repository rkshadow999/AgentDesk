using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.ApplicationModel;

namespace AgentDesk.App.Notifications;

public sealed record WindowsNotificationPayload(string Title, string Body, string SessionId);

public sealed class WindowsNotificationInvokedEventArgs : EventArgs
{
    public WindowsNotificationInvokedEventArgs(string sessionId)
    {
        if (sessionId is null || !AgentDeskUserNotification.IsValidSessionId(sessionId))
        {
            throw new ArgumentException("The notification session ID is invalid.", nameof(sessionId));
        }
        SessionId = sessionId;
    }

    public string SessionId { get; }
}

public interface IWindowsAppNotificationPublisher
{
    void Publish(WindowsNotificationPayload payload);
}

public interface IWindowsNotificationActivationSource
{
    event EventHandler<WindowsNotificationInvokedEventArgs>? NotificationInvoked;

    void Initialize();
}

public interface IWindowsAppNotificationRuntime
{
    bool RequiresRegistration { get; }

    void SubscribeInvoked(Action<string?> handler);

    void Register();

    void Show(WindowsNotificationPayload payload);
}

public sealed class WindowsUserNotificationService :
    IUserNotificationService,
    IWindowsNotificationActivationSource
{
    private readonly IWindowsNotificationActivationSource? _activationSource;
    private readonly IWindowsAppNotificationPublisher _publisher;

    public WindowsUserNotificationService()
        : this(new WindowsAppNotificationPublisher())
    {
    }

    public WindowsUserNotificationService(IWindowsAppNotificationPublisher publisher)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _activationSource = publisher as IWindowsNotificationActivationSource;
        if (_activationSource is not null)
        {
            _activationSource.NotificationInvoked += ActivationSource_NotificationInvoked;
        }
    }

    public event EventHandler<WindowsNotificationInvokedEventArgs>? NotificationInvoked;

    public void Initialize() => _activationSource?.Initialize();

    public Task ShowAsync(
        AgentDeskUserNotification notification,
        string language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();
        _publisher.Publish(new WindowsNotificationPayload(
            notification.Title(language),
            notification.Body(language),
            notification.SessionId));
        return Task.CompletedTask;
    }

    private void ActivationSource_NotificationInvoked(
        object? sender,
        WindowsNotificationInvokedEventArgs eventArgs) =>
        NotificationInvoked?.Invoke(this, eventArgs);
}

public sealed class WindowsAppNotificationPublisher :
    IWindowsAppNotificationPublisher,
    IWindowsNotificationActivationSource
{
    private readonly object _gate = new();
    private readonly IWindowsAppNotificationRuntime _runtime;
    private bool _subscribed;
    private bool _registrationComplete;

    public WindowsAppNotificationPublisher()
        : this(new WindowsAppNotificationRuntime())
    {
    }

    public WindowsAppNotificationPublisher(IWindowsAppNotificationRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public event EventHandler<WindowsNotificationInvokedEventArgs>? NotificationInvoked;

    public void Initialize() => EnsureRegistered();

    public void Publish(WindowsNotificationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(payload.Title) ||
            string.IsNullOrWhiteSpace(payload.Body) ||
            payload.Title.Length > 256 ||
            payload.Body.Length > 1024 ||
            payload.Title.Any(char.IsControl) ||
            payload.Body.Any(character => char.IsControl(character) && character is not '\r' and not '\n') ||
            !AgentDeskUserNotification.IsValidSessionId(payload.SessionId))
        {
            throw new ArgumentException("The Windows notification payload is invalid.", nameof(payload));
        }

        EnsureRegistered();
        _runtime.Show(payload);
    }

    private void EnsureRegistered()
    {
        lock (_gate)
        {
            if (_registrationComplete)
            {
                return;
            }

            if (!_subscribed)
            {
                _runtime.SubscribeInvoked(OnNotificationInvoked);
                _subscribed = true;
            }

            if (_runtime.RequiresRegistration)
            {
                _runtime.Register();
            }
            _registrationComplete = true;
        }
    }

    private void OnNotificationInvoked(string? sessionId)
    {
        if (sessionId is null || !AgentDeskUserNotification.IsValidSessionId(sessionId))
        {
            return;
        }

        NotificationInvoked?.Invoke(this, new WindowsNotificationInvokedEventArgs(sessionId));
    }
}

public sealed class WindowsAppNotificationRuntime : IWindowsAppNotificationRuntime
{
    private readonly AppNotificationManager _manager = AppNotificationManager.Default;

    public bool RequiresRegistration
    {
        get
        {
            try
            {
                _ = Package.Current.Id.Name;
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public void SubscribeInvoked(Action<string?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _manager.NotificationInvoked += (_, args) =>
            handler(args.Arguments.TryGetValue("sessionId", out var sessionId)
                ? sessionId
                : null);
    }

    public void Register() => _manager.Register();

    public void Show(WindowsNotificationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        // Unique Tag per session so concurrent multi-session completions stack
        // instead of replacing each other in Action Center.
        var notification = new AppNotificationBuilder()
            .AddText(payload.Title)
            .AddText(payload.Body)
            .AddArgument("sessionId", payload.SessionId)
            .BuildNotification();
        notification.Tag = TruncateTag(payload.SessionId);
        notification.Group = "AgentDesk.SessionStatus";
        _manager.Show(notification);
    }

    private static string TruncateTag(string sessionId) =>
        sessionId.Length <= 64 ? sessionId : sessionId[..64];
}
