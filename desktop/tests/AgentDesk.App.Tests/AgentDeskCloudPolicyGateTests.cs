using AgentDesk.App.Cloud;
using AgentDesk.Core.Execution;

namespace AgentDesk.App.Tests;

public sealed class AgentDeskCloudPolicyGateTests
{
    [Fact]
    public void RemoteProfileFailsClosedUntilAValidatedPolicyArrives()
    {
        var gate = new AgentDeskCloudPolicyGate();

        gate.ApplyRemoteProfile();

        Assert.Equal(AgentDeskCloudPolicyMode.RemoteUnverified, gate.Mode);
        Assert.False(gate.AllowsExecutionProfile(ExecutionProfile.NativeProtected));
        Assert.False(gate.AllowsRemoteRunner);
        Assert.False(gate.AllowsWindowsAutomation(localEnabled: true));
    }

    [Fact]
    public void ValidatedPolicyControlsExecutionRunnerAndAutomationCapabilities()
    {
        var gate = new AgentDeskCloudPolicyGate();
        gate.ApplyRemoteProfile();

        gate.ApplyPolicy(new AgentDeskCloudPolicySnapshot(
            AllowedExecutionProfiles: [ExecutionProfile.WslStrict],
            RemoteRunnerEnabled: true,
            UiAutomationEnabled: false,
            MaximumConcurrentJobs: 3,
            AllowedPluginPublishers: ["publisher-1"]));

        Assert.Equal(AgentDeskCloudPolicyMode.RemoteVerified, gate.Mode);
        Assert.False(gate.AllowsExecutionProfile(ExecutionProfile.NativeProtected));
        Assert.True(gate.AllowsExecutionProfile(ExecutionProfile.WslStrict));
        Assert.True(gate.AllowsRemoteRunner);
        Assert.False(gate.AllowsWindowsAutomation(localEnabled: true));
    }

    [Fact]
    public void LocalOnlyProfileRemovesRemoteTeamRestrictions()
    {
        var gate = new AgentDeskCloudPolicyGate();
        gate.ApplyRemoteProfile();
        gate.ApplyLocalOnlyProfile();

        Assert.Equal(AgentDeskCloudPolicyMode.LocalOnly, gate.Mode);
        Assert.True(gate.AllowsExecutionProfile(ExecutionProfile.NativeProtected));
        Assert.True(gate.AllowsExecutionProfile(ExecutionProfile.WslStrict));
        Assert.False(gate.AllowsRemoteRunner);
        Assert.True(gate.AllowsWindowsAutomation(localEnabled: true));
    }

    [Fact]
    public void PolicyVersionChangesOnlyWhenTheEffectivePolicyChanges()
    {
        var gate = new AgentDeskCloudPolicyGate();
        var localVersion = gate.CaptureVersion();

        gate.ApplyLocalOnlyProfile();
        Assert.Equal(localVersion, gate.CaptureVersion());

        gate.ApplyRemoteProfile();
        var remoteVersion = gate.CaptureVersion();
        Assert.NotEqual(localVersion, remoteVersion);

        gate.ApplyRemoteProfile();
        Assert.Equal(remoteVersion, gate.CaptureVersion());
        Assert.True(gate.IsCurrent(remoteVersion));
        Assert.False(gate.IsCurrent(localVersion));
    }

    [Fact]
    public void PolicySnapshotRejectsMorePublishersThanTheCloudServiceAccepts()
    {
        var publishers = Enumerable.Range(0, 129)
            .Select(index => $"publisher-{index:D3}")
            .ToArray();
        var policy = new AgentDeskCloudPolicySnapshot(
            AllowedExecutionProfiles: [ExecutionProfile.NativeProtected],
            RemoteRunnerEnabled: false,
            UiAutomationEnabled: false,
            MaximumConcurrentJobs: 1,
            AllowedPluginPublishers: publishers);

        Assert.Throws<ArgumentException>(() => policy.Validate());
    }

    [Fact]
    public async Task StablePolicyExecutionPropagatesCancellationWhileWaitingForTheGate()
    {
        var gate = new AgentDeskCloudPolicyGate();
        var version = gate.CaptureVersion();
        var activeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActive = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = gate.ExecuteIfCurrentAsync(
            version,
            async cancellationToken =>
            {
                activeStarted.TrySetResult();
                await releaseActive.Task.WaitAsync(cancellationToken);
                return "completed";
            },
            CancellationToken.None);
        await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();

        var waiting = gate.ExecuteIfCurrentAsync(
            version,
            _ => Task.FromResult("unexpected"),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        releaseActive.TrySetResult();
        Assert.Equal("completed", await active);
    }

    [Fact]
    public async Task PolicyUpdatePropagatesCancellationWhileStableExecutionIsActive()
    {
        var gate = new AgentDeskCloudPolicyGate();
        var version = gate.CaptureVersion();
        var activeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActive = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = gate.ExecuteIfCurrentAsync(
            version,
            async cancellationToken =>
            {
                activeStarted.TrySetResult();
                await releaseActive.Task.WaitAsync(cancellationToken);
                return "completed";
            },
            CancellationToken.None);
        await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();

        var update = gate.ApplyRemoteProfileAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        releaseActive.TrySetResult();
        Assert.Equal("completed", await active);
        Assert.Equal(AgentDeskCloudPolicyMode.LocalOnly, gate.Mode);
    }
}
