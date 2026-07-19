namespace AgentDesk.Cloud.Client;

internal sealed record TokenCreateWire(string SubjectId, string Role);

internal sealed record TokenIssuedWire(string Token, string SubjectId, string Role);

internal sealed record PolicyUpdateWire(
    IReadOnlyList<string> AllowedExecutionProfiles,
    bool RemoteRunnerEnabled,
    bool UiAutomationEnabled,
    int MaximumConcurrentJobs,
    IReadOnlyList<string> AllowedPluginPublishers);

internal sealed record PolicyWire(
    int Version,
    string[] AllowedExecutionProfiles,
    bool RemoteRunnerEnabled,
    bool UiAutomationEnabled,
    int MaximumConcurrentJobs,
    string[] AllowedPluginPublishers);

internal sealed record EnvelopeWire(string Algorithm, string Nonce, string Ciphertext);

internal sealed record SessionWriteWire(
    int Revision,
    string Algorithm,
    string Nonce,
    string Ciphertext);

internal sealed record SessionWriteReceiptWire(string SessionId, int Revision);

internal sealed record SessionDeleteReceiptWire(string SessionId, int Revision);

internal sealed record SessionWire(
    string SessionId,
    int Revision,
    string Algorithm,
    string Nonce,
    string Ciphertext,
    DateTimeOffset UpdatedAt);

internal sealed record HandoffCreateWire(
    string HandoffId,
    string TargetDeviceId,
    string SessionId,
    string Algorithm,
    string Nonce,
    string Ciphertext);

internal sealed record HandoffWire(
    string HandoffId,
    string SourceDeviceId,
    string TargetDeviceId,
    string SessionId,
    string Algorithm,
    string Nonce,
    string Ciphertext,
    DateTimeOffset CreatedAt);

internal sealed record RunnerRegistrationWire(IReadOnlyList<string> Capabilities);

internal sealed record JobQueueWire(
    string JobId,
    string Kind,
    string RequiredCapability,
    string? AutomationId,
    string? RunId,
    string Algorithm,
    string Nonce,
    string Ciphertext);

internal sealed record JobReceiptWire(string JobId);

internal sealed record JobClaimWire(int LeaseSeconds);

internal sealed record RunnerJobWire(
    string JobId,
    string Kind,
    string RequiredCapability,
    string? AutomationId,
    string? RunId,
    string Algorithm,
    string Nonce,
    string Ciphertext,
    DateTimeOffset LeaseExpiresAt,
    string LeaseToken,
    long LeaseGeneration)
{
    public override string ToString() => nameof(RunnerJobWire);
}

internal sealed record AutomationCreateWire(
    string AutomationId,
    string Kind,
    string Name,
    int IntervalSeconds,
    string RequiredCapability,
    string Algorithm,
    string Nonce,
    string Ciphertext);

internal sealed record JobCompleteWire(
    string RunnerId,
    string LeaseToken,
    long LeaseGeneration,
    string Kind,
    string RequiredCapability,
    string? AutomationId,
    string? RunId,
    string Algorithm,
    string Nonce,
    string Ciphertext)
{
    public override string ToString() => nameof(JobCompleteWire);
}

internal sealed record AutomationWire(
    string AutomationId,
    string Name,
    int IntervalSeconds,
    bool Enabled,
    DateTimeOffset NextRunAt);
