namespace AgentDesk.App.Cloud;

public sealed class AgentDeskCloudUnavailableException : InvalidOperationException
{
    public AgentDeskCloudUnavailableException()
        : base("AgentDesk cloud features are unavailable in local-only mode.")
    {
    }
}
