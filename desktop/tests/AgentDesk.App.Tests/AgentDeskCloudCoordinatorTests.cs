using System.Reflection;
using AgentDesk.App.Bridge;
using AgentDesk.App.Cloud;
using AgentDesk.Cloud.Client;
using AgentDesk.Core.Engine;

namespace AgentDesk.App.Tests;

public sealed class AgentDeskCloudCoordinatorTests
{
    private const string RequestId = "5f70f2bf-c3ad-4a13-9ca0-61b847f52f0d";

    [Fact]
    public void DisposeReleasesAnOwnedDisposableCloudService()
    {
        var service = new RecordingCloudService();
        var coordinator = new AgentDeskCloudCoordinator(
            service,
            new RecordingCloudEngineHost(),
            _ => Task.CompletedTask,
            ownsService: true);

        coordinator.Dispose();
        coordinator.Dispose();

        Assert.True(service.IsDisposed);
    }

    [Fact]
    public async Task ProfilePolicyRunnerAndAutomationCommandsPublishOnlySafeSummaries()
    {
        var service = new RecordingCloudService
        {
            Profile = Snapshot(
                new CloudConnectionProfile(
                    new Uri("https://cloud.example.test/"),
                    "team-1",
                    "device-1"),
                hasAccessToken: true),
        };
        var events = new List<WebEvent>();
        var policyGate = new AgentDeskCloudPolicyGate();
        using var coordinator = new AgentDeskCloudCoordinator(
            service,
            new RecordingCloudEngineHost(),
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            },
            policyGate);

        await coordinator.HandleAsync(new CloudProfileGetWebCommand(RequestId));
        await coordinator.HandleAsync(new CloudPolicyGetWebCommand(NewRequestId(2)));
        await coordinator.HandleAsync(new CloudPolicyUpdateWebCommand(
            NewRequestId(3),
            ["NativeProtected", "WslStrict"],
            RemoteRunnerEnabled: true,
            UiAutomationEnabled: false,
            MaximumConcurrentJobs: 4,
            AllowedPluginPublishers: ["publisher-1"]));
        await coordinator.HandleAsync(new CloudRunnerRegisterWebCommand(
            NewRequestId(4),
            "runner-1",
            ["windows", "wsl"]));
        await coordinator.HandleAsync(new CloudAutomationListWebCommand(NewRequestId(5)));
        await coordinator.HandleAsync(new CloudAutomationDisableWebCommand(
            NewRequestId(6),
            "automation-1"));

        Assert.Collection(
            events,
            item => Assert.IsType<CloudProfileWebEvent>(item),
            item => Assert.IsType<CloudPolicyWebEvent>(item),
            item => Assert.IsType<CloudPolicyWebEvent>(item),
            item => Assert.IsType<CloudRunnerRegisteredWebEvent>(item),
            item => Assert.IsType<CloudAutomationsWebEvent>(item),
            item => Assert.IsType<CloudAutomationDisabledWebEvent>(item));
        var json = string.Join('\n', events.Select(WebMessageProtocol.SerializeEvent));
        Assert.DoesNotContain("\"accessToken\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recovery", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ciphertext", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(("runner-1", new[] { "windows", "wsl" }), service.RunnerRegistration);
        Assert.Equal(AgentDeskCloudPolicyMode.RemoteVerified, policyGate.Mode);
    }

    [Fact]
    public async Task RunnerCommandsFailClosedUntilTheRemotePolicyIsVerified()
    {
        var service = new RecordingCloudService
        {
            Profile = Snapshot(
                new CloudConnectionProfile(
                    new Uri("https://cloud.example.test/"),
                    "team-1",
                    "device-1"),
                hasAccessToken: true),
        };
        var events = new List<WebEvent>();
        var gate = new AgentDeskCloudPolicyGate();
        using var coordinator = new AgentDeskCloudCoordinator(
            service,
            new RecordingCloudEngineHost(),
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            },
            gate);

        await coordinator.HandleAsync(new CloudProfileGetWebCommand(RequestId));
        await coordinator.HandleAsync(new CloudRunnerRegisterWebCommand(
            NewRequestId(2),
            "runner-blocked",
            ["windows"]));
        await coordinator.HandleAsync(new CloudPolicyGetWebCommand(NewRequestId(3)));
        await coordinator.HandleAsync(new CloudRunnerRegisterWebCommand(
            NewRequestId(4),
            "runner-allowed",
            ["windows"]));

        Assert.Collection(
            events,
            item => Assert.IsType<CloudProfileWebEvent>(item),
            item => Assert.IsType<CloudErrorWebEvent>(item),
            item => Assert.IsType<CloudPolicyWebEvent>(item),
            item => Assert.IsType<CloudRunnerRegisteredWebEvent>(item));
        Assert.Equal(("runner-allowed", new[] { "windows" }), service.RunnerRegistration);
    }

    [Fact]
    public async Task AutomationListAndDisableRemainAvailableWhenStartingRemoteWorkIsDisabled()
    {
        var service = new RecordingCloudService();
        var events = new List<WebEvent>();
        var gate = new AgentDeskCloudPolicyGate();
        gate.ApplyPolicy(new AgentDeskCloudPolicySnapshot(
            [AgentDesk.Core.Execution.ExecutionProfile.NativeProtected],
            RemoteRunnerEnabled: false,
            UiAutomationEnabled: false,
            MaximumConcurrentJobs: 1,
            AllowedPluginPublishers: []));
        using var coordinator = new AgentDeskCloudCoordinator(
            service,
            new RecordingCloudEngineHost(),
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            },
            gate);

        await coordinator.HandleAsync(new CloudAutomationListWebCommand(NewRequestId(7)));
        await coordinator.HandleAsync(new CloudAutomationDisableWebCommand(
            NewRequestId(8),
            "automation-1"));

        Assert.Collection(
            events,
            item => Assert.IsType<CloudAutomationsWebEvent>(item),
            item => Assert.IsType<CloudAutomationDisabledWebEvent>(item));
    }

    [Fact]
    public async Task RunnerTaskAndAutomationCommandsPublishSafeSummaries()
    {
        var service = new RecordingCloudService();
        var events = new List<WebEvent>();
        var gate = new AgentDeskCloudPolicyGate();
        gate.ApplyPolicy(new AgentDeskCloudPolicySnapshot(
            [AgentDesk.Core.Execution.ExecutionProfile.NativeProtected],
            RemoteRunnerEnabled: true,
            UiAutomationEnabled: false,
            MaximumConcurrentJobs: 2,
            AllowedPluginPublishers: []));
        using var coordinator = new AgentDeskCloudCoordinator(
            service,
            new RecordingCloudEngineHost(),
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            },
            gate);

        await coordinator.HandleAsync(new CloudRunnerQueueWebCommand(
            NewRequestId(10), "windows", "inspect active workspace"));
        await coordinator.HandleAsync(new CloudRunnerClaimWebCommand(
            NewRequestId(11), "runner-1", 30));
        await coordinator.HandleAsync(new CloudRunnerCompleteWebCommand(
            NewRequestId(12), "claim-1", "job-1", "completed safely"));
        await coordinator.HandleAsync(new CloudAutomationCreateWebCommand(
            NewRequestId(13), "nightly", 3600, "windows", "review default branch"));

        Assert.Collection(
            events,
            item => Assert.IsType<CloudRunnerQueuedWebEvent>(item),
            item => Assert.IsType<CloudRunnerClaimedWebEvent>(item),
            item => Assert.IsType<CloudRunnerCompletedWebEvent>(item),
            item => Assert.IsType<CloudAutomationCreatedWebEvent>(item));
    }

    [Fact]
    public async Task InitializePolicyLeavesRemoteProfileFailClosedWhenRefreshFails()
    {
        var service = new RecordingCloudService
        {
            Profile = Snapshot(
                new CloudConnectionProfile(
                    new Uri("https://cloud.example.test/"),
                    "team-1",
                    "device-1"),
                hasAccessToken: true),
            PolicyException = new HttpRequestException("policy unavailable"),
        };
        var gate = new AgentDeskCloudPolicyGate();
        using var coordinator = new AgentDeskCloudCoordinator(
            service,
            new RecordingCloudEngineHost(),
            _ => Task.CompletedTask,
            gate);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => coordinator.InitializePolicyAsync());

        Assert.Equal(1, service.PolicyRequestCount);
        Assert.Equal(AgentDeskCloudPolicyMode.RemoteUnverified, gate.Mode);
        Assert.False(gate.AllowsExecutionProfile(Core.Execution.ExecutionProfile.NativeProtected));
        Assert.False(gate.AllowsRemoteRunner);
        Assert.False(gate.AllowsWindowsAutomation(localEnabled: true));
    }

    [Fact]
    public async Task InitializePolicyKeepsLocalProfileLocalWithoutFetchingPolicy()
    {
        var service = new RecordingCloudService
        {
            Profile = Snapshot(new CloudConnectionProfile(), hasAccessToken: false),
            PolicyException = new InvalidOperationException("must not be requested"),
        };
        var gate = new AgentDeskCloudPolicyGate();
        using var coordinator = new AgentDeskCloudCoordinator(
            service,
            new RecordingCloudEngineHost(),
            _ => Task.CompletedTask,
            gate);

        await coordinator.InitializePolicyAsync();

        Assert.Equal(0, service.PolicyRequestCount);
        Assert.Equal(AgentDeskCloudPolicyMode.LocalOnly, gate.Mode);
        Assert.True(gate.AllowsExecutionProfile(Core.Execution.ExecutionProfile.NativeProtected));
        Assert.False(gate.AllowsRemoteRunner);
        Assert.True(gate.AllowsWindowsAutomation(localEnabled: true));
    }

    [Fact]
    public async Task RemoteProfileStartsNotificationsAndProjectsOnlySafeMetadata()
    {
        var service = new RecordingCloudService
        {
            Profile = Snapshot(
                new CloudConnectionProfile(
                    new Uri("https://cloud.example.test/"),
                    "team-1",
                    "device-1"),
                hasAccessToken: true),
        };
        var events = new List<WebEvent>();
        var gate = new AgentDeskCloudPolicyGate();
        using var coordinator = new AgentDeskCloudCoordinator(
            service,
            new RecordingCloudEngineHost(),
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            },
            gate);

        await coordinator.InitializePolicyAsync();
        await service.EmitNotificationAsync(new CloudNotification(
            CloudNotificationKind.HandoffChanged,
            "handoff-1"));
        await service.EmitNotificationAsync(new CloudNotification(
            CloudNotificationKind.JobChanged,
            "job-1"));
        await service.EmitNotificationAsync(new CloudNotification(
            CloudNotificationKind.PolicyChanged,
            PolicyVersion: 3));

        Assert.Equal(1, service.NotificationStartCount);
        Assert.Equal(2, service.PolicyRequestCount);
        Assert.Equal(AgentDeskCloudPolicyMode.RemoteVerified, gate.Mode);
        Assert.Collection(
            events,
            item => Assert.Equal(
                new CloudNotificationWebEvent("handoff-changed", "handoff-1"),
                item),
            item => Assert.Equal(
                new CloudNotificationWebEvent("job-changed", "job-1"),
                item),
            item => Assert.Equal(
                new CloudNotificationWebEvent(
                    "policy-changed",
                    ResourceId: null,
                    PolicyVersion: 3),
                item));
        var json = string.Join('\n', events.Select(WebMessageProtocol.SerializeEvent));
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ciphertext", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalProfileStopsNotificationsAndPreventsFurtherProjection()
    {
        var service = new RecordingCloudService
        {
            Profile = Snapshot(
                new CloudConnectionProfile(
                    new Uri("https://cloud.example.test/"),
                    "team-1",
                    "device-1"),
                hasAccessToken: true),
        };
        var events = new List<WebEvent>();
        using var coordinator = new AgentDeskCloudCoordinator(
            service,
            new RecordingCloudEngineHost(),
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            });
        await coordinator.InitializePolicyAsync();

        await coordinator.HandleAsync(new CloudProfileSaveLocalWebCommand(RequestId));
        await service.EmitNotificationAsync(new CloudNotification(
            CloudNotificationKind.JobChanged,
            "job-after-local"));

        Assert.Equal(1, service.NotificationStopCount);
        Assert.Single(events);
        Assert.IsType<CloudProfileWebEvent>(events[0]);
    }

    [Fact]
    public async Task PolicyNotificationStillProjectsWhenRefreshFailsClosed()
    {
        var service = new RecordingCloudService
        {
            Profile = Snapshot(
                new CloudConnectionProfile(
                    new Uri("https://cloud.example.test/"),
                    "team-1",
                    "device-1"),
                hasAccessToken: true),
        };
        var events = new List<WebEvent>();
        var gate = new AgentDeskCloudPolicyGate();
        using var coordinator = new AgentDeskCloudCoordinator(
            service,
            new RecordingCloudEngineHost(),
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            },
            gate);
        await coordinator.InitializePolicyAsync();
        service.PolicyException = new HttpRequestException("policy unavailable");

        await service.EmitNotificationAsync(new CloudNotification(
            CloudNotificationKind.PolicyChanged,
            PolicyVersion: 4));

        Assert.Equal(AgentDeskCloudPolicyMode.RemoteUnverified, gate.Mode);
        Assert.Equal(
            new CloudNotificationWebEvent("policy-changed", PolicyVersion: 4),
            Assert.Single(events));
    }

    [Fact]
    public async Task SyncAndHandoffCommandsUseTheEngineLeaseAndActivateImportedSessions()
    {
        var envelope = new EncryptedEnvelope(
            EncryptedEnvelope.Aes256GcmAlgorithm,
            "SENSITIVE_NONCE",
            "SENSITIVE_CIPHERTEXT");
        var service = new RecordingCloudService
        {
            UploadRevision = 7,
            DownloadResult = new EngineCloudImportResult(
                "remote-1",
                8,
                new SessionId("imported-1")),
            Handoff = new CloudHandoff(
                "handoff-1",
                "device-1",
                "device-2",
                "session-1",
                envelope,
                DateTimeOffset.Parse("2026-07-18T00:00:00Z")),
            HandoffImports =
            [
                new EngineCloudHandoffImportResult(
                    "handoff-2",
                    "device-3",
                    "remote-2",
                    new SessionId("imported-2")),
            ],
        };
        var host = new RecordingCloudEngineHost();
        var events = new List<WebEvent>();
        using var coordinator = new AgentDeskCloudCoordinator(
            service,
            host,
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            });

        await coordinator.HandleAsync(new CloudSessionUploadWebCommand(RequestId, "session-1"));
        await coordinator.HandleAsync(new CloudSessionDownloadWebCommand(NewRequestId(2), "remote-1"));
        await coordinator.HandleAsync(new CloudHandoffCreateWebCommand(
            NewRequestId(3),
            "session-1",
            "device-2"));
        await coordinator.HandleAsync(new CloudHandoffReceiveWebCommand(NewRequestId(4)));

        Assert.Equal(4, host.BeginCount);
        Assert.Equal(["imported-1", "imported-2"], host.ActivatedSessionIds);
        Assert.Collection(
            events,
            item => Assert.IsType<CloudSessionUploadedWebEvent>(item),
            item => Assert.IsType<CloudSessionImportedWebEvent>(item),
            item => Assert.IsType<CloudHandoffCreatedWebEvent>(item),
            item => Assert.IsType<CloudHandoffsReceivedWebEvent>(item));
        var json = string.Join('\n', events.Select(WebMessageProtocol.SerializeEvent));
        Assert.DoesNotContain("SENSITIVE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("envelope", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteAndNativeExportPublishBoundedSessionSummaries()
    {
        const string privateMarker = "SENSITIVE_SESSION_BODY";
        var service = new RecordingCloudService
        {
            DeletedRevision = 9,
            ExportedDocument = EngineSessionDocument.FromJson(
                $"{{\"private\":\"{privateMarker}\"}}"),
        };
        var events = new List<WebEvent>();
        using var coordinator = new AgentDeskCloudCoordinator(
            service,
            new RecordingCloudEngineHost(),
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            });
        var directory = Path.Combine(Path.GetTempPath(), $"AgentDesk-cloud-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var nativePath = Path.Combine(directory, "safe-name.agentdesk-session.json");
        try
        {
            await coordinator.HandleAsync(new CloudSessionDeleteWebCommand(
                RequestId,
                "remote-session-1"));
            await coordinator.ExportSessionAsync(
                new CloudSessionExportWebCommand(NewRequestId(20), "local-session-1"),
                nativePath);

            Assert.Contains(privateMarker, await File.ReadAllTextAsync(nativePath), StringComparison.Ordinal);
            Assert.Collection(
                events,
                item => Assert.Equal(
                    new CloudSessionDeletedWebEvent(
                        RequestId,
                        "remote-session-1",
                        Found: true,
                        Revision: 9),
                    item),
                item => Assert.Equal(
                    new CloudSessionExportedWebEvent(
                        NewRequestId(20),
                        "local-session-1",
                        "safe-name.agentdesk-session.json"),
                    item));
            var json = string.Join('\n', events.Select(WebMessageProtocol.SerializeEvent));
            Assert.DoesNotContain(privateMarker, json, StringComparison.Ordinal);
            Assert.DoesNotContain(directory, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("document", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NativeProfileAndPairingFlowsKeepSecretsAndPathsOutOfWebEvents()
    {
        var service = new RecordingCloudService
        {
            Profile = Snapshot(new CloudConnectionProfile(), hasAccessToken: false),
            PairingPackage = RecoveryKeyPairingPackage.FromBytes([1, 2, 3, 4]),
        };
        var events = new List<WebEvent>();
        using var coordinator = new AgentDeskCloudCoordinator(
            service,
            new RecordingCloudEngineHost(),
            webEvent =>
            {
                events.Add(webEvent);
                return Task.CompletedTask;
            });
        var directory = Path.Combine(Path.GetTempPath(), $"AgentDesk-cloud-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var packagePath = Path.Combine(directory, "pairing.agentdesk-pairing");
        var passphrase = "SENSITIVE_PASSPHRASE".ToCharArray();
        try
        {
            await coordinator.SaveRemoteProfileAsync(
                new CloudProfileSaveRemoteWebCommand(
                    RequestId,
                    new Uri("https://cloud.example.test/"),
                    "team-1",
                    "device-1"),
                "SENSITIVE_ACCESS_TOKEN");
            await coordinator.ExportPairingAsync(
                new CloudPairingExportWebCommand(NewRequestId(2)),
                packagePath,
                passphrase);
            await coordinator.ImportPairingAsync(
                new CloudPairingImportWebCommand(NewRequestId(3)),
                packagePath,
                passphrase);

            Assert.Equal("SENSITIVE_ACCESS_TOKEN", service.AccessToken);
            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(packagePath));
            Assert.Equal(4, service.ImportedPairingPackage?.ByteLength);
            var json = string.Join('\n', events.Select(WebMessageProtocol.SerializeEvent));
            Assert.DoesNotContain("SENSITIVE", json, StringComparison.Ordinal);
            Assert.DoesNotContain(packagePath, json, StringComparison.Ordinal);
            Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Array.Clear(passphrase);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string NewRequestId(int value) =>
        $"5f70f2bf-c3ad-4a13-9ca0-{value:D12}";

    private static AgentDeskCloudProfileSnapshot Snapshot(
        CloudConnectionProfile profile,
        bool hasAccessToken) =>
        (AgentDeskCloudProfileSnapshot)Activator.CreateInstance(
            typeof(AgentDeskCloudProfileSnapshot),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [profile, hasAccessToken],
            culture: null)!;

    private static CloudTeamPolicy Policy() =>
        (CloudTeamPolicy)Activator.CreateInstance(
            typeof(CloudTeamPolicy),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [3, new[] { "NativeProtected" }, true, false, 4, new[] { "publisher-1" }],
            culture: null)!;

    private sealed class RecordingCloudEngineHost : IAgentDeskCloudEngineHost
    {
        private readonly IEngineClient _engine = DispatchProxy.Create<IEngineClient, EngineProxy>();

        public int BeginCount { get; private set; }

        public List<string> ActivatedSessionIds { get; } = [];

        public Task<IAgentDeskCloudEngineLease> BeginCloudEngineOperationAsync(
            CancellationToken cancellationToken = default)
        {
            BeginCount++;
            return Task.FromResult<IAgentDeskCloudEngineLease>(new Lease(this, _engine));
        }

        private sealed class Lease(RecordingCloudEngineHost owner, IEngineClient engine)
            : IAgentDeskCloudEngineLease
        {
            public IEngineClient Engine { get; } = engine;

            public string WorkspacePath => "C:\\workspace";

            public string EngineWorkspacePath => "C:\\workspace";

            public Task ActivateSessionAsync(
                SessionId sessionId,
                CancellationToken cancellationToken = default)
            {
                owner.ActivatedSessionIds.Add(sessionId.Value);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private class EngineProxy : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args) => throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class RecordingCloudService : IAgentDeskCloudDesktopService, IDisposable
    {
        public AgentDeskCloudProfileSnapshot Profile { get; set; } =
            Snapshot(new CloudConnectionProfile(), hasAccessToken: false);

        public string? AccessToken { get; private set; }

        public int UploadRevision { get; init; } = 1;

        public EngineCloudImportResult? DownloadResult { get; init; }

        public CloudHandoff? Handoff { get; init; }

        public IReadOnlyList<EngineCloudHandoffImportResult> HandoffImports { get; init; } = [];

        public RecoveryKeyPairingPackage PairingPackage { get; init; } =
            RecoveryKeyPairingPackage.FromBytes([1]);

        public RecoveryKeyPairingPackage? ImportedPairingPackage { get; private set; }

        public (string RunnerId, IReadOnlyList<string> Capabilities)? RunnerRegistration { get; private set; }

        public Exception? PolicyException { get; set; }

        public int PolicyRequestCount { get; private set; }

        public int? DeletedRevision { get; init; }

        public EngineSessionDocument ExportedDocument { get; init; } =
            EngineSessionDocument.FromJson("{\"schemaVersion\":1}");

        public bool IsDisposed { get; private set; }

        public int NotificationStartCount { get; private set; }

        public int NotificationStopCount { get; private set; }

        private Func<CloudNotification, CancellationToken, Task>? NotificationHandler { get; set; }

        public void Dispose()
        {
            IsDisposed = true;
            NotificationHandler = null;
        }

        public Task StartNotificationsAsync(
            Func<CloudNotification, CancellationToken, Task> notificationHandler,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NotificationStartCount++;
            NotificationHandler = notificationHandler;
            return Task.CompletedTask;
        }

        public Task StopNotificationsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NotificationHandler is not null)
            {
                NotificationStopCount++;
                NotificationHandler = null;
            }
            return Task.CompletedTask;
        }

        public Task EmitNotificationAsync(CloudNotification notification) =>
            NotificationHandler?.Invoke(notification, CancellationToken.None) ?? Task.CompletedTask;

        public Task<AgentDeskCloudProfileSnapshot> LoadProfileAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Profile);

        public Task<AgentDeskCloudProfileSnapshot> SaveRemoteProfileAsync(
            Uri baseUri,
            string teamId,
            string deviceId,
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            AccessToken = accessToken;
            Profile = Snapshot(
                new CloudConnectionProfile(baseUri, teamId, deviceId),
                hasAccessToken: true);
            return Task.FromResult(Profile);
        }

        public Task<AgentDeskCloudProfileSnapshot> SaveLocalOnlyProfileAsync(
            CancellationToken cancellationToken = default)
        {
            Profile = Snapshot(new CloudConnectionProfile(), hasAccessToken: false);
            return Task.FromResult(Profile);
        }

        public Task<RecoveryKeyPairingPackage> ExportRecoveryKeyPairingPackageAsync(
            ReadOnlyMemory<char> passphrase,
            CancellationToken cancellationToken = default) => Task.FromResult(PairingPackage);

        public Task ImportRecoveryKeyPairingPackageAsync(
            RecoveryKeyPairingPackage package,
            ReadOnlyMemory<char> passphrase,
            CancellationToken cancellationToken = default)
        {
            ImportedPairingPackage = package;
            return Task.CompletedTask;
        }

        public Task<int> UploadSessionAsync(
            IEngineClient engine,
            SessionId sessionId,
            CancellationToken cancellationToken = default) => Task.FromResult(UploadRevision);

        public Task<int?> DeleteSessionAsync(
            string remoteSessionId,
            CancellationToken cancellationToken = default) => Task.FromResult(DeletedRevision);

        public Task<EngineSessionDocument> ExportSessionAsync(
            IEngineClient engine,
            SessionId sessionId,
            CancellationToken cancellationToken = default) => Task.FromResult(ExportedDocument);

        public Task<EngineCloudImportResult?> DownloadAndImportSessionAsync(
            IEngineClient engine,
            string remoteSessionId,
            string workingDirectory,
            CancellationToken cancellationToken = default) => Task.FromResult(DownloadResult);

        public Task<CloudHandoff> CreateHandoffAsync(
            IEngineClient engine,
            SessionId sessionId,
            string targetDeviceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Handoff ?? throw new InvalidOperationException());

        public Task<IReadOnlyList<EngineCloudHandoffImportResult>> ReceiveHandoffsAsync(
            IEngineClient engine,
            string workingDirectory,
            int limit = 50,
            CancellationToken cancellationToken = default) => Task.FromResult(HandoffImports);

        public Task<CloudTeamPolicy> GetPolicyAsync(
            CancellationToken cancellationToken = default)
        {
            PolicyRequestCount++;
            if (PolicyException is not null)
            {
                throw PolicyException;
            }
            return Task.FromResult(Policy());
        }

        public Task<CloudTeamPolicy> UpdatePolicyAsync(
            CloudTeamPolicyUpdate update,
            CancellationToken cancellationToken = default) => Task.FromResult(Policy());

        public Task RegisterRunnerAsync(
            string runnerId,
            IReadOnlyList<string> capabilities,
            CancellationToken cancellationToken = default)
        {
            RunnerRegistration = (runnerId, capabilities.ToArray());
            return Task.CompletedTask;
        }

        public Task<CloudJobReceipt> QueueRunnerJobAsync(
            string requiredCapability,
            EncryptedEnvelope envelope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CloudJobReceipt> QueueRunnerTaskAsync(
            string requiredCapability,
            string task,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudJobReceipt("job-1"));

        public Task<AgentDeskCloudRunnerJobClaim?> ClaimRunnerJobAsync(
            string runnerId,
            int leaseSeconds = 60,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AgentDeskCloudRunnerTask?> ClaimRunnerTaskAsync(
            string runnerId,
            int leaseSeconds = 60,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentDeskCloudRunnerTask?>(new(
                "claim-1",
                new CloudRunnerJobIdentity(
                    "job-1",
                    CloudRunnerPayloadKinds.Task,
                    "windows"),
                "task",
                DateTimeOffset.UtcNow));

        public Task CompleteRunnerJobAsync(
            string claimHandle,
            string jobId,
            EncryptedEnvelope envelope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CompleteRunnerTaskAsync(
            string claimHandle,
            string jobId,
            string result,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<CloudAutomation> CreateAutomationAsync(
            string name,
            int intervalSeconds,
            string requiredCapability,
            EncryptedEnvelope envelope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CloudAutomation> CreateAutomationTaskAsync(
            string name,
            int intervalSeconds,
            string requiredCapability,
            string task,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudAutomation(
                "automation-1",
                name,
                intervalSeconds,
                true,
                DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<CloudAutomation>> ListAutomationsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudAutomation>>(
                [new("automation-1", "nightly", 3600, true, DateTimeOffset.Parse("2026-07-19T00:00:00Z"))]);

        public Task<bool> DisableAutomationAsync(
            string automationId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
