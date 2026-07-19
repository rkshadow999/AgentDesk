using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AgentDesk.Cloud.Client;
using Microsoft.AspNetCore.SignalR.Client;

namespace AgentDesk.Cloud.Client.IntegrationTests;

public sealed class RealCloudClientIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SignalRNotificationsRoundTripAsBoundedMetadataOverHeaderAuthentication()
    {
        await using var host = await KestrelCloudTestHost.StartAsync();
        var admin = host.CreateCloudClient(host.BootstrapToken);
        var sourceToken = await admin.CreateTokenAsync("device-source", CloudTokenRole.Device);
        var targetToken = await admin.CreateTokenAsync("device-target", CloudTokenRole.Device);
        var source = host.CreateCloudClient(sourceToken.Token);
        await using var notifications = host.CreateNotificationClient(
            targetToken.Token,
            "device-target");
        var received = new List<CloudNotification>();
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await notifications.StartAsync(notification =>
        {
            lock (received)
            {
                received.Add(notification);
                if (received.Count == 3)
                {
                    completed.TrySetResult();
                }
            }
            return Task.CompletedTask;
        });

        var initialPolicy = await admin.GetPolicyAsync();
        var updatedPolicy = await admin.UpdatePolicyAsync(new CloudTeamPolicyUpdate(
            ["NativeProtected"],
            remoteRunnerEnabled: true,
            uiAutomationEnabled: false,
            maximumConcurrentJobs: 2,
            allowedPluginPublishers: []));
        Assert.Equal(initialPolicy.Version + 1, updatedPolicy.Version);
        await source.CreateHandoffAsync(
            "handoff-signalr",
            "device-target",
            "session-signalr",
            ValidEnvelope());
        await source.QueueJobAsync(
            new CloudRunnerJobIdentity(
                "job-signalr",
                CloudRunnerPayloadKinds.Task,
                "windows"),
            ValidEnvelope());

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await notifications.StopAsync();

        CloudNotification[] snapshot;
        lock (received)
        {
            snapshot = received.ToArray();
        }
        Assert.Contains(
            new CloudNotification(CloudNotificationKind.HandoffChanged, "handoff-signalr"),
            snapshot);
        Assert.Contains(
            new CloudNotification(CloudNotificationKind.JobChanged, "job-signalr"),
            snapshot);
        Assert.Contains(
            new CloudNotification(
                CloudNotificationKind.PolicyChanged,
                PolicyVersion: updatedPolicy.Version),
            snapshot);
        var projected = string.Join('\n', snapshot.Select(item => item.ToString()));
        Assert.DoesNotContain(sourceToken.Token, projected, StringComparison.Ordinal);
        Assert.DoesNotContain(targetToken.Token, projected, StringComparison.Ordinal);
        Assert.DoesNotContain("ciphertext", projected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RevokingATokenActivelyClosesItsExistingSignalRConnection()
    {
        await using var host = await KestrelCloudTestHost.StartAsync();
        var admin = host.CreateCloudClient(host.BootstrapToken);
        var token = await admin.CreateTokenAsync("device-revoked", CloudTokenRole.Device);
        var closed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(host.BaseAddress, "hubs/notifications"),
                options => options.Headers["Authorization"] = $"Bearer {token.Token}")
            .Build();
        connection.Closed += exception =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await connection.StartAsync();

        await admin.RevokeTokenAsync("device-revoked");

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EncryptedDesktopWorkflowsRoundTripThroughRealKestrelWithoutPlaintextAtRest()
    {
        await using var host = await KestrelCloudTestHost.StartAsync();
        Assert.True(host.BaseAddress.IsLoopback);
        Assert.Equal(Uri.UriSchemeHttp, host.BaseAddress.Scheme);
        Assert.True(host.BaseAddress.Port > 0);
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(host.BaseAddress.Host, host.BaseAddress.Port);
            Assert.True(tcp.Connected);
        }

        var suffix = Guid.NewGuid().ToString("N");
        var sessionV1Text = $"agentdesk-e2e-session-v1-{suffix}";
        var sessionV2Text = $"agentdesk-e2e-session-v2-{suffix}";
        var handoffText = $"agentdesk-e2e-handoff-{suffix}";
        var queuedJobText = $"agentdesk-e2e-queued-job-{suffix}";
        var completedJobText = $"agentdesk-e2e-completed-job-{suffix}";
        var automationText = $"agentdesk-e2e-automation-{suffix}";
        var recoveryKey = RandomNumberGenerator.GetBytes(32);
        var codec = new AesGcmEnvelopeCodec();
        var sensitiveValues = new List<string>
        {
            host.BootstrapToken,
            sessionV1Text,
            sessionV2Text,
            handoffText,
            queuedJobText,
            completedJobText,
            automationText,
            Convert.ToBase64String(recoveryKey),
        };
        sensitiveValues.AddRange(
            new[]
            {
                sessionV1Text,
                sessionV2Text,
                handoffText,
                queuedJobText,
                completedJobText,
                automationText,
            }.Select(value => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))));

        try
        {
            var admin = host.CreateCloudClient(host.BootstrapToken);
            var sourceToken = await admin.CreateTokenAsync("device-source", CloudTokenRole.Device);
            var targetToken = await admin.CreateTokenAsync("device-target", CloudTokenRole.Device);
            var serviceToken = await admin.CreateTokenAsync("runner-service", CloudTokenRole.Service);
            sensitiveValues.Add(sourceToken.Token);
            sensitiveValues.Add(targetToken.Token);
            sensitiveValues.Add(serviceToken.Token);

            var initialPolicy = await admin.GetPolicyAsync();
            Assert.True(initialPolicy.RemoteRunnerEnabled);
            var updatedPolicy = await admin.UpdatePolicyAsync(
                new CloudTeamPolicyUpdate(
                    ["WslStrict"],
                    remoteRunnerEnabled: true,
                    uiAutomationEnabled: false,
                    maximumConcurrentJobs: 2,
                    allowedPluginPublishers: []));
            Assert.Equal(initialPolicy.Version + 1, updatedPolicy.Version);
            Assert.Equal(["WslStrict"], updatedPolicy.AllowedExecutionProfiles);

            var source = host.CreateCloudClient(sourceToken.Token);
            var target = host.CreateCloudClient(targetToken.Token);
            var service = host.CreateCloudClient(serviceToken.Token);

            var sessionV1 = codec.Encrypt(
                Encoding.UTF8.GetBytes(sessionV1Text),
                recoveryKey,
                new EnvelopeBinding("default", "device-source", "session-e2e", 1));
            var sessionV2 = codec.Encrypt(
                Encoding.UTF8.GetBytes(sessionV2Text),
                recoveryKey,
                new EnvelopeBinding("default", "device-source", "session-e2e", 2));
            await source.PutSessionAsync("session-e2e", 1, sessionV1);
            await source.PutSessionAsync("session-e2e", 2, sessionV2);
            var rollback = await Assert.ThrowsAsync<CloudClientException>(
                () => source.PutSessionAsync("session-e2e", 1, sessionV1));
            Assert.Equal(CloudClientErrorKind.Conflict, rollback.Kind);
            var synced = await source.GetSessionAsync("session-e2e");
            Assert.Equal(2, synced!.Revision);
            Assert.Equal(
                sessionV2Text,
                Encoding.UTF8.GetString(
                    codec.Decrypt(
                        synced.Envelope,
                        recoveryKey,
                        new EnvelopeBinding(
                            "default",
                            "device-source",
                            "session-e2e",
                            synced.Revision))));
            var deleteReceipt = await source.DeleteSessionAsync("session-e2e", synced.Revision);
            Assert.Equal(3, deleteReceipt.Revision);
            var tombstone = await Assert.ThrowsAsync<CloudSessionDeletedException>(
                () => target.GetSessionAsync("session-e2e"));
            Assert.Equal(3, tombstone.Revision);

            const string handoffId = "handoff-e2e";
            var handoffEnvelope = codec.Encrypt(
                Encoding.UTF8.GetBytes(handoffText),
                recoveryKey,
                new HandoffEnvelopeBinding(
                    "default",
                    "device-source",
                    "device-target",
                    "handoff-session",
                    handoffId,
                    1));
            var createdHandoff = await source.CreateHandoffAsync(
                handoffId,
                "device-target",
                "handoff-session",
                handoffEnvelope);
            var targetInbox = await target.ListHandoffsAsync();
            var deliveredHandoff = Assert.Single(targetInbox);
            Assert.Equal(createdHandoff.HandoffId, deliveredHandoff.HandoffId);
            Assert.Equal(
                handoffText,
                Encoding.UTF8.GetString(
                    codec.Decrypt(
                        deliveredHandoff.Envelope,
                        recoveryKey,
                        new HandoffEnvelopeBinding(
                            "default",
                            deliveredHandoff.SourceDeviceId,
                            deliveredHandoff.TargetDeviceId,
                            deliveredHandoff.SessionId,
                            deliveredHandoff.HandoffId,
                            1))));
            Assert.True(await target.AcknowledgeHandoffAsync(deliveredHandoff.HandoffId));
            Assert.Empty(await target.ListHandoffsAsync());

            await service.RegisterRunnerAsync("runner-service", ["windows"]);
            var queuedEnvelope = codec.Encrypt(
                Encoding.UTF8.GetBytes(queuedJobText),
                recoveryKey,
                new EnvelopeBinding("default", "device-source", "queued-job", 1));
            var queuedIdentity = new CloudRunnerJobIdentity(
                "job-integration-1",
                CloudRunnerPayloadKinds.Task,
                "windows");
            var queued = await source.QueueJobAsync(queuedIdentity, queuedEnvelope);
            var claimed = await service.ClaimJobAsync("runner-service", leaseSeconds: 30);
            Assert.Equal(queued.JobId, claimed!.JobId);
            Assert.Equal(
                queuedJobText,
                Encoding.UTF8.GetString(
                    codec.Decrypt(
                        claimed.Envelope,
                        recoveryKey,
                        new EnvelopeBinding("default", "device-source", "queued-job", 1))));
            var completedEnvelope = codec.Encrypt(
                Encoding.UTF8.GetBytes(completedJobText),
                recoveryKey,
                new EnvelopeBinding("default", "runner-service", "completed-job", 1));
            await service.CompleteJobAsync(claimed.Identity, completedEnvelope);

            var automationEnvelope = codec.Encrypt(
                Encoding.UTF8.GetBytes(automationText),
                recoveryKey,
                new EnvelopeBinding("default", "device-source", "automation-job", 1));
            var automation = await source.CreateAutomationAsync(
                "automation-integration-1",
                "E2E encrypted automation",
                60,
                "windows",
                automationEnvelope);
            Assert.Contains(
                await source.ListAutomationsAsync(),
                item => item.AutomationId == automation.AutomationId && item.Enabled);
            var automationJob = await ClaimEventuallyAsync(service);
            Assert.Equal(
                automationText,
                Encoding.UTF8.GetString(
                    codec.Decrypt(
                        automationJob.Envelope,
                        recoveryKey,
                        new EnvelopeBinding("default", "device-source", "automation-job", 1))));
            await service.CompleteJobAsync(automationJob.Identity, completedEnvelope);
            Assert.True(await source.DisableAutomationAsync(automation.AutomationId));

            await admin.RevokeTokenAsync("device-source");
            await admin.RevokeTokenAsync("device-target");
            await admin.RevokeTokenAsync("runner-service");
            var revoked = await Assert.ThrowsAsync<CloudClientException>(
                () => source.GetPolicyAsync());
            Assert.Equal(CloudClientErrorKind.Authentication, revoked.Kind);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoveryKey);
        }

        await host.StopAsync();
        host.AssertArtifactsDoNotContain(sensitiveValues);
    }

    private static async Task<CloudRunnerJob> ClaimEventuallyAsync(
        IAgentDeskCloudClient service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var job = await service.ClaimJobAsync(
                "runner-service",
                leaseSeconds: 30,
                timeout.Token);
            if (job is not null)
            {
                return job;
            }
            await Task.Delay(100, timeout.Token);
        }
    }

    private static EncryptedEnvelope ValidEnvelope() => new(
        EncryptedEnvelope.Aes256GcmAlgorithm,
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)),
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
}
