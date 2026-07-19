namespace AgentDesk.Cloud;

public sealed record EncryptedEnvelopeRequest(
    int Revision,
    string Algorithm,
    string Nonce,
    string Ciphertext);

public sealed record EncryptedEnvelopeResponse(
    string SessionId,
    int Revision,
    string Algorithm,
    string Nonce,
    string Ciphertext,
    DateTimeOffset UpdatedAt);

public sealed record SessionDeleteResponse(string SessionId, int Revision);

public sealed record RunnerRegistrationRequest(IReadOnlyList<string> Capabilities);

public sealed record JobQueueRequest(
    string JobId,
    string Kind,
    string RequiredCapability,
    string? AutomationId,
    string? RunId,
    string Algorithm,
    string Nonce,
    string Ciphertext);

public sealed record JobCreatedResponse(string JobId);

public sealed record JobClaimRequest(int LeaseSeconds = 60);

public sealed record JobClaimResponse(
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
    public override string ToString() => nameof(JobClaimResponse);
}

public sealed record JobCompleteRequest(
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
    public override string ToString() => nameof(JobCompleteRequest);
}

public sealed record TokenCreateRequest(string SubjectId, string Role);

public sealed record TokenCreateResponse(string Token, string SubjectId, string Role);

public sealed record HandoffCreateRequest(
    string HandoffId,
    string TargetDeviceId,
    string SessionId,
    string Algorithm,
    string Nonce,
    string Ciphertext);

public sealed record HandoffResponse(
    string HandoffId,
    string SourceDeviceId,
    string TargetDeviceId,
    string SessionId,
    string Algorithm,
    string Nonce,
    string Ciphertext,
    DateTimeOffset CreatedAt);

public sealed record TeamPolicy(
    int Version,
    IReadOnlyList<string> AllowedExecutionProfiles,
    bool RemoteRunnerEnabled,
    bool UiAutomationEnabled,
    int MaximumConcurrentJobs,
    IReadOnlyList<string> AllowedPluginPublishers);

public sealed record TeamPolicyUpdateRequest(
    IReadOnlyList<string> AllowedExecutionProfiles,
    bool RemoteRunnerEnabled,
    bool UiAutomationEnabled,
    int MaximumConcurrentJobs,
    IReadOnlyList<string> AllowedPluginPublishers);

public sealed record PublisherCreateRequest(string KeyId, string PublicKeyPem);

public sealed record PluginPublishRequest(
    string ManifestJson,
    string Sha256,
    string PublisherKeyId,
    string Signature);

public sealed record PluginRecord(
    string PluginId,
    string Version,
    string ManifestJson,
    string Sha256,
    string PublisherKeyId,
    string Signature,
    DateTimeOffset PublishedAt);

public sealed record AutomationCreateRequest(
    string AutomationId,
    string Kind,
    string Name,
    int IntervalSeconds,
    string RequiredCapability,
    string Algorithm,
    string Nonce,
    string Ciphertext);

public sealed record AutomationRecord(
    string AutomationId,
    string Name,
    int IntervalSeconds,
    bool Enabled,
    DateTimeOffset NextRunAt);

internal enum AutomationCreateStatus
{
    Created,
    RemoteRunnerDisabled,
    Duplicate,
}

internal sealed record AutomationCreateResult(
    AutomationCreateStatus Status,
    AutomationRecord? Automation = null);

internal sealed record CloudIdentity(string TeamId, string SubjectId, string Role);

internal enum SessionWriteResult
{
    Created,
    Updated,
    RevisionConflict,
}

internal enum SessionDeleteStatus
{
    Deleted,
    AlreadyDeleted,
    NotFound,
    RevisionConflict,
}

internal sealed record SessionDeleteResult(SessionDeleteStatus Status, int? Revision = null);

internal static class RunnerPayloadKinds
{
    public const string Task = "task";
    public const string Automation = "automation";
    public const string TaskResult = "task-result";
    public const string AutomationResult = "automation-result";
}

internal static class RunnerPayloadBindings
{
    public const string Current = "runner-v2";
    public const string LegacyUnbound = "legacy-unbound";
}

internal enum JobQueueStatus
{
    Created,
    RemoteRunnerDisabled,
    MaximumConcurrentJobsReached,
    Duplicate,
}

internal sealed record JobQueueResult(JobQueueStatus Status, string? JobId = null);

internal enum JobClaimStatus
{
    Claimed,
    Empty,
    RemoteRunnerDisabled,
    MaximumConcurrentJobsReached,
}

internal sealed record JobClaimResult(JobClaimStatus Status, JobClaimResponse? Job = null);

internal enum HandoffCreateStatus
{
    Created,
    Duplicate,
}

internal sealed record HandoffCreateResult(
    HandoffCreateStatus Status,
    HandoffResponse? Handoff = null);
