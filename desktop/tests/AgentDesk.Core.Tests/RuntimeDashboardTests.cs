using AgentDesk.Core.Engine;

namespace AgentDesk.Core.Tests;

public sealed class RuntimeDashboardTests
{
    [Fact]
    public void BackgroundTaskSnapshot_PrefersTheOriginalDisplayCommand()
    {
        var task = new BackgroundTaskSnapshot(
            "task-1",
            "wrapped internal command",
            "dotnet test",
            "C:\\repo",
            DateTimeOffset.UnixEpoch,
            null,
            string.Empty,
            "C:\\temp\\task.log",
            Truncated: false,
            ExitCode: null,
            Signal: null,
            Completed: false,
            BackgroundTaskKind.Bash,
            ExplicitlyKilled: false,
            OwnerSessionId: "session-1");

        Assert.Equal("dotnet test", task.UserFacingCommand);
    }

    [Theory]
    [InlineData(SubagentStatus.Initializing, false)]
    [InlineData(SubagentStatus.Running, false)]
    [InlineData(SubagentStatus.Completed, true)]
    [InlineData(SubagentStatus.Failed, true)]
    [InlineData(SubagentStatus.Cancelled, true)]
    public void SubagentSnapshot_ReportsWhetherTheStatusIsTerminal(
        SubagentStatus status,
        bool expected)
    {
        var snapshot = new SubagentSnapshot(
            "subagent-1",
            "session-parent",
            "session-child",
            "worker",
            "Run tests",
            DateTimeOffset.UnixEpoch,
            TimeSpan.Zero,
            status);

        Assert.Equal(expected, snapshot.IsTerminal);
    }
}
