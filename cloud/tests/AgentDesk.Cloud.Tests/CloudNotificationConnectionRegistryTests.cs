namespace AgentDesk.Cloud.Tests;

public sealed class CloudNotificationConnectionRegistryTests
{
    [Fact]
    public void RevokeAbortsExistingAndLateConnectionsUntilSubjectIsAllowedAgain()
    {
        var registry = new CloudNotificationConnectionRegistry();
        var existingAborts = 0;
        var lateAborts = 0;
        var allowedAborts = 0;
        using var existing = registry.Register(
            "team-1",
            "device-1",
            "connection-existing",
            () => existingAborts++);

        registry.Revoke("team-1", "device-1");
        using var late = registry.Register(
            "team-1",
            "device-1",
            "connection-late",
            () => lateAborts++);
        registry.Allow("team-1", "device-1");
        using var allowed = registry.Register(
            "team-1",
            "device-1",
            "connection-allowed",
            () => allowedAborts++);

        Assert.Equal(1, existingAborts);
        Assert.Equal(1, lateAborts);
        Assert.Equal(0, allowedAborts);
    }
}
