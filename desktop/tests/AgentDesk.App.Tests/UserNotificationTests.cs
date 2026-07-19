using AgentDesk.App.Notifications;

namespace AgentDesk.App.Tests;

public sealed class UserNotificationTests
{
    [Theory]
    [InlineData(AgentDeskNotificationKind.TaskCompleted, "任务已完成", "Task completed")]
    [InlineData(AgentDeskNotificationKind.TaskFailed, "任务执行失败", "Task failed")]
    [InlineData(AgentDeskNotificationKind.PermissionRequired, "需要权限确认", "Permission required")]
    public void ContentUsesOnlyGenericStatusAndTheBoundedSessionId(
        AgentDeskNotificationKind kind,
        string chineseTitle,
        string englishTitle)
    {
        var notification = new AgentDeskUserNotification("session-42", kind);

        Assert.Equal(chineseTitle, notification.Title("zh-CN"));
        Assert.Equal(englishTitle, notification.Title("en-US"));
        Assert.Contains("session-42", notification.Body("zh-CN"), StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", notification.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file", notification.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("session\n42")]
    [InlineData("session/42")]
    [InlineData("session\u200B42")]
    [InlineData("session\u202E42")]
    public void ContentRejectsInvalidSessionIds(string sessionId)
    {
        Assert.Throws<ArgumentException>(() =>
            new AgentDeskUserNotification(sessionId, AgentDeskNotificationKind.TaskCompleted));
    }

    [Fact]
    public async Task WindowsServicePublishesOnlyTheGenericLocalizedPayload()
    {
        var publisher = new RecordingPublisher();
        var service = new WindowsUserNotificationService(publisher);

        await service.ShowAsync(
            new AgentDeskUserNotification("session-42", AgentDeskNotificationKind.TaskCompleted),
            "zh-CN");

        var payload = Assert.Single(publisher.Payloads);
        Assert.Equal("任务已完成", payload.Title);
        Assert.Equal("AgentDesk 会话 session-42 的状态已更新。", payload.Body);
        Assert.Equal("session-42", payload.SessionId);
        Assert.DoesNotContain("prompt", payload.Title + payload.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsServiceInitializesThePublisherAndForwardsActivation()
    {
        var publisher = new RecordingPublisher();
        var service = new WindowsUserNotificationService(publisher);
        string? activatedSessionId = null;
        service.NotificationInvoked += (_, args) => activatedSessionId = args.SessionId;

        service.Initialize();
        publisher.Invoke("session-42");

        Assert.Equal(1, publisher.InitializeCalls);
        Assert.Equal("session-42", activatedSessionId);
    }

    [Fact]
    public async Task ActivationCoordinatorRegistersAtStartupAndActivatesBeforeOpeningTheIndexedSession()
    {
        var publisher = new RecordingPublisher();
        var service = new WindowsUserNotificationService(publisher);
        var calls = new List<string>();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new WindowsNotificationActivationCoordinator(
            service,
            _ =>
            {
                calls.Add("activate");
                return Task.CompletedTask;
            },
            (sessionId, _) =>
            {
                calls.Add($"open:{sessionId}");
                opened.TrySetResult();
                return Task.FromResult(true);
            });

        coordinator.Start();
        publisher.Invoke("session-42");
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, publisher.InitializeCalls);
        Assert.Equal(["activate", "open:session-42"], calls);
    }

    [Fact]
    public void WindowsPublisherSubscribesBeforeRegisteringAndRegistersOnlyOnce()
    {
        var runtime = new RecordingNotificationRuntime();
        var publisher = new WindowsAppNotificationPublisher(runtime);

        publisher.Publish(new WindowsNotificationPayload(
            "Task completed",
            "Session updated.",
            "session-42"));
        publisher.Publish(new WindowsNotificationPayload("Task failed", "Session updated.", "session-42"));

        Assert.Equal(["subscribe", "register", "show", "show"], runtime.Calls);
    }

    [Fact]
    public void WindowsPublisherCanRegisterAtStartupWithoutShowingANotification()
    {
        var runtime = new RecordingNotificationRuntime();
        var publisher = new WindowsAppNotificationPublisher(runtime);

        publisher.Initialize();

        Assert.Equal(["subscribe", "register"], runtime.Calls);
        publisher.Publish(new WindowsNotificationPayload(
            "Task completed",
            "Session updated.",
            "session-42"));
        Assert.Equal(["subscribe", "register", "show"], runtime.Calls);
    }

    [Fact]
    public void WindowsPublisherRetriesRegistrationWithoutDuplicatingTheActivationHandler()
    {
        var runtime = new RecordingNotificationRuntime
        {
            RegisterFailuresRemaining = 1,
        };
        var publisher = new WindowsAppNotificationPublisher(runtime);
        var payload = new WindowsNotificationPayload(
            "Task completed",
            "Session updated.",
            "session-42");

        Assert.Throws<InvalidOperationException>(() => publisher.Publish(payload));
        publisher.Publish(payload);

        Assert.Equal(["subscribe", "register", "register", "show"], runtime.Calls);
    }

    [Fact]
    public void WindowsPublisherSkipsExplicitRegistrationForPackagedApps()
    {
        var runtime = new RecordingNotificationRuntime
        {
            RequiresRegistration = false,
        };
        var publisher = new WindowsAppNotificationPublisher(runtime);

        publisher.Publish(new WindowsNotificationPayload(
            "Task completed",
            "Session updated.",
            "session-42"));

        Assert.Equal(["subscribe", "show"], runtime.Calls);
    }

    [Fact]
    public void WindowsPublisherProjectsTheBoundedSessionIdFromActivationArguments()
    {
        var runtime = new RecordingNotificationRuntime();
        var publisher = new WindowsAppNotificationPublisher(runtime);
        string? activatedSessionId = null;
        publisher.NotificationInvoked += (_, args) => activatedSessionId = args.SessionId;
        publisher.Publish(new WindowsNotificationPayload(
            "Task completed",
            "Session updated.",
            "session-42"));

        runtime.Invoke("session-42");

        Assert.Equal("session-42", activatedSessionId);
    }

    private sealed class RecordingPublisher :
        IWindowsAppNotificationPublisher,
        IWindowsNotificationActivationSource
    {
        public List<WindowsNotificationPayload> Payloads { get; } = [];

        public int InitializeCalls { get; private set; }

        public event EventHandler<WindowsNotificationInvokedEventArgs>? NotificationInvoked;

        public void Publish(WindowsNotificationPayload payload) => Payloads.Add(payload);

        public void Initialize() => InitializeCalls++;

        public void Invoke(string sessionId) => NotificationInvoked?.Invoke(
            this,
            new WindowsNotificationInvokedEventArgs(sessionId));
    }

    private sealed class RecordingNotificationRuntime : IWindowsAppNotificationRuntime
    {
        public List<string> Calls { get; } = [];

        public bool RequiresRegistration { get; init; } = true;

        public int RegisterFailuresRemaining { get; set; }

        private Action<string?>? InvokedHandler { get; set; }

        public void SubscribeInvoked(Action<string?> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            Calls.Add("subscribe");
            InvokedHandler = handler;
        }

        public void Register()
        {
            Calls.Add("register");
            if (RegisterFailuresRemaining > 0)
            {
                RegisterFailuresRemaining--;
                throw new InvalidOperationException("transient registration failure");
            }
        }

        public void Show(WindowsNotificationPayload payload) => Calls.Add("show");

        public void Invoke(string arguments) => InvokedHandler?.Invoke(arguments);
    }
}
