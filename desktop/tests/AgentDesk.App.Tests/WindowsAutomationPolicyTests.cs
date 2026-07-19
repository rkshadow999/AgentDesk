using AgentDesk.App.Automation;

namespace AgentDesk.App.Tests;

public sealed class WindowsAutomationPolicyTests
{
    [Theory]
    [InlineData(false, false, false, WindowsAutomationAvailability.DisabledByUser)]
    [InlineData(false, true, true, WindowsAutomationAvailability.DisabledByUser)]
    [InlineData(true, true, false, WindowsAutomationAvailability.DisabledByTeamPolicy)]
    [InlineData(true, false, false, WindowsAutomationAvailability.Enabled)]
    [InlineData(true, true, true, WindowsAutomationAvailability.Enabled)]
    public void EvaluateRequiresTheLocalOptInAndAnyActiveTeamPolicy(
        bool localEnabled,
        bool teamPolicyActive,
        bool teamAllows,
        WindowsAutomationAvailability expected)
    {
        var decision = WindowsAutomationPolicy.Evaluate(
            localEnabled,
            teamPolicyActive,
            teamAllows);

        Assert.Equal(expected, decision.Availability);
        Assert.Equal(expected is WindowsAutomationAvailability.Enabled, decision.IsEnabled);
    }
}
