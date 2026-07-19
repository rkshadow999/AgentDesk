namespace AgentDesk.App.Automation;

public enum WindowsAutomationAvailability
{
    DisabledByUser,
    DisabledByTeamPolicy,
    Enabled,
}

public sealed record WindowsAutomationDecision(WindowsAutomationAvailability Availability)
{
    public bool IsEnabled => Availability is WindowsAutomationAvailability.Enabled;
}

public static class WindowsAutomationPolicy
{
    public static WindowsAutomationDecision Evaluate(
        bool localEnabled,
        bool teamPolicyActive,
        bool teamAllows)
    {
        if (!localEnabled)
        {
            return new WindowsAutomationDecision(
                WindowsAutomationAvailability.DisabledByUser);
        }
        if (teamPolicyActive && !teamAllows)
        {
            return new WindowsAutomationDecision(
                WindowsAutomationAvailability.DisabledByTeamPolicy);
        }
        return new WindowsAutomationDecision(WindowsAutomationAvailability.Enabled);
    }
}
