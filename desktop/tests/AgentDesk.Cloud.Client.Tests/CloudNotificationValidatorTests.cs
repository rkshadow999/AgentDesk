using AgentDesk.Cloud.Client;

namespace AgentDesk.Cloud.Client.Tests;

public sealed class CloudNotificationValidatorTests
{
    private static readonly CloudConnectionProfile Profile = new(
        new Uri("https://cloud.example.test/root/"),
        "team-1",
        "device-1");

    [Fact]
    public void DeviceScopedHandoffRequiresTheConfiguredTeamAndDevice()
    {
        Assert.Null(CloudNotificationValidator.ValidateHandoff(
            Profile,
            new CloudHandoffNotificationPayload("team-other", "device-1", "handoff-1")));
        Assert.Null(CloudNotificationValidator.ValidateHandoff(
            Profile,
            new CloudHandoffNotificationPayload("team-1", "device-other", "handoff-1")));

        var notification = CloudNotificationValidator.ValidateHandoff(
            Profile,
            new CloudHandoffNotificationPayload("team-1", "device-1", "handoff-1"));

        Assert.Equal(
            new CloudNotification(CloudNotificationKind.HandoffChanged, "handoff-1"),
            notification);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("contains/slash")]
    public void HandoffRejectsInvalidResourceIdentifiers(string handoffId)
    {
        Assert.Null(CloudNotificationValidator.ValidateHandoff(
            Profile,
            new CloudHandoffNotificationPayload("team-1", "device-1", handoffId)));
    }

    [Fact]
    public void TeamScopedJobRequiresTheConfiguredTeam()
    {
        Assert.Null(CloudNotificationValidator.ValidateJob(
            Profile,
            new CloudJobNotificationPayload("team-other", "job-1")));

        var notification = CloudNotificationValidator.ValidateJob(
            Profile,
            new CloudJobNotificationPayload("team-1", "job-1"));

        Assert.Equal(
            new CloudNotification(CloudNotificationKind.JobChanged, "job-1"),
            notification);
    }

    [Fact]
    public void PolicyRequiresTheConfiguredTeamAndAPositiveVersion()
    {
        Assert.Null(CloudNotificationValidator.ValidatePolicy(
            Profile,
            new CloudPolicyNotificationPayload("team-other", 2)));
        Assert.Null(CloudNotificationValidator.ValidatePolicy(
            Profile,
            new CloudPolicyNotificationPayload("team-1", 0)));

        var notification = CloudNotificationValidator.ValidatePolicy(
            Profile,
            new CloudPolicyNotificationPayload("team-1", 2));

        Assert.Equal(
            new CloudNotification(
                CloudNotificationKind.PolicyChanged,
                ResourceId: null,
                PolicyVersion: 2),
            notification);
    }
}
