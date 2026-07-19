namespace AgentDesk.Cloud;

public sealed class CloudOptions
{
    public const string SectionName = "AgentDeskCloud";

    public string DatabasePath { get; set; } = Path.Combine(
        AppContext.BaseDirectory,
        "data",
        "agentdesk-cloud.db");

    public string BootstrapToken { get; set; } = string.Empty;

    public string? PreviousBootstrapToken { get; set; }

    public bool RequireHttps { get; set; } = true;

    public int MaximumCiphertextBytes { get; set; } = 16 * 1024 * 1024;

    public int AutomationPollingIntervalSeconds { get; set; } = 5;
}
