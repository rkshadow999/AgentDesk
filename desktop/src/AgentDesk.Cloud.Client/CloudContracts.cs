using System.Text.Json.Serialization;

namespace AgentDesk.Cloud.Client;

public static class CloudPolicyLimits
{
    public const int MaximumConcurrentJobs = 128;
    public const int MaximumPluginPublishers = 128;
    public const int MaximumPublisherIdCharacters = 128;
}

public static class CloudRunnerPayloadKinds
{
    public const string Task = "task";
    public const string Automation = "automation";
    public const string TaskResult = "task-result";
    public const string AutomationResult = "automation-result";
}

public enum CloudTokenRole
{
    Device,
    Service,
}

public sealed class CloudIssuedToken
{
    internal CloudIssuedToken(string token, string subjectId, CloudTokenRole role)
    {
        Token = token;
        SubjectId = subjectId;
        Role = role;
    }

    [JsonIgnore]
    public string Token { get; }

    public string SubjectId { get; }

    public CloudTokenRole Role { get; }

    public override string ToString() =>
        $"CloudIssuedToken {{ SubjectId = {SubjectId}, Role = {Role} }}";
}

public sealed class CloudTeamPolicy
{
    internal CloudTeamPolicy(
        int version,
        IReadOnlyList<string> allowedExecutionProfiles,
        bool remoteRunnerEnabled,
        bool uiAutomationEnabled,
        int maximumConcurrentJobs,
        IReadOnlyList<string> allowedPluginPublishers)
    {
        Version = version;
        AllowedExecutionProfiles = allowedExecutionProfiles;
        RemoteRunnerEnabled = remoteRunnerEnabled;
        UiAutomationEnabled = uiAutomationEnabled;
        MaximumConcurrentJobs = maximumConcurrentJobs;
        AllowedPluginPublishers = allowedPluginPublishers;
    }

    public int Version { get; }

    public IReadOnlyList<string> AllowedExecutionProfiles { get; }

    public bool RemoteRunnerEnabled { get; }

    public bool UiAutomationEnabled { get; }

    public int MaximumConcurrentJobs { get; }

    public IReadOnlyList<string> AllowedPluginPublishers { get; }
}

public sealed class CloudTeamPolicyUpdate
{
    public CloudTeamPolicyUpdate(
        IReadOnlyList<string> allowedExecutionProfiles,
        bool remoteRunnerEnabled,
        bool uiAutomationEnabled,
        int maximumConcurrentJobs,
        IReadOnlyList<string> allowedPluginPublishers)
    {
        ArgumentNullException.ThrowIfNull(allowedExecutionProfiles);
        ArgumentNullException.ThrowIfNull(allowedPluginPublishers);
        AllowedExecutionProfiles = allowedExecutionProfiles.ToArray();
        RemoteRunnerEnabled = remoteRunnerEnabled;
        UiAutomationEnabled = uiAutomationEnabled;
        MaximumConcurrentJobs = maximumConcurrentJobs;
        AllowedPluginPublishers = allowedPluginPublishers.ToArray();
    }

    public IReadOnlyList<string> AllowedExecutionProfiles { get; }

    public bool RemoteRunnerEnabled { get; }

    public bool UiAutomationEnabled { get; }

    public int MaximumConcurrentJobs { get; }

    public IReadOnlyList<string> AllowedPluginPublishers { get; }
}

public sealed record CloudSessionWriteReceipt(string SessionId, int Revision);

public sealed record CloudSessionDeleteReceipt(string SessionId, int Revision);

public sealed class CloudSyncedSession
{
    public CloudSyncedSession(
        string sessionId,
        int revision,
        EncryptedEnvelope envelope,
        DateTimeOffset updatedAt)
    {
        SessionId = CloudRequestGuard.Identifier(sessionId, 128, nameof(sessionId));
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }
        ArgumentNullException.ThrowIfNull(envelope);

        Revision = revision;
        Envelope = envelope;
        UpdatedAt = updatedAt;
    }

    public string SessionId { get; }

    public int Revision { get; }

    public EncryptedEnvelope Envelope { get; }

    public DateTimeOffset UpdatedAt { get; }
}

public sealed class CloudHandoff
{
    public CloudHandoff(
        string handoffId,
        string sourceDeviceId,
        string targetDeviceId,
        string sessionId,
        EncryptedEnvelope envelope,
        DateTimeOffset createdAt)
    {
        HandoffId = CloudRequestGuard.Identifier(handoffId, 128, nameof(handoffId));
        SourceDeviceId = CloudRequestGuard.Identifier(
            sourceDeviceId,
            128,
            nameof(sourceDeviceId));
        TargetDeviceId = CloudRequestGuard.Identifier(
            targetDeviceId,
            128,
            nameof(targetDeviceId));
        SessionId = CloudRequestGuard.Identifier(sessionId, 128, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(envelope);

        Envelope = envelope;
        CreatedAt = createdAt;
    }

    public string HandoffId { get; }

    public string SourceDeviceId { get; }

    public string TargetDeviceId { get; }

    public string SessionId { get; }

    public EncryptedEnvelope Envelope { get; }

    public DateTimeOffset CreatedAt { get; }
}

public sealed record CloudJobReceipt(string JobId);

public sealed class CloudRunnerJobIdentity
{
    public CloudRunnerJobIdentity(
        string jobId,
        string kind,
        string requiredCapability,
        string? automationId = null,
        string? runId = null)
        : this(
            jobId,
            kind,
            requiredCapability,
            automationId,
            runId,
            leaseRunnerId: null,
            leaseToken: null,
            leaseGeneration: 0)
    {
    }

    private CloudRunnerJobIdentity(
        string jobId,
        string kind,
        string requiredCapability,
        string? automationId,
        string? runId,
        string? leaseRunnerId,
        string? leaseToken,
        long leaseGeneration)
    {
        JobId = CloudRequestGuard.Identifier(jobId, 128, nameof(jobId));
        RequiredCapability = CloudRequestGuard.Identifier(
            requiredCapability,
            64,
            nameof(requiredCapability));
        if (string.Equals(kind, CloudRunnerPayloadKinds.Task, StringComparison.Ordinal))
        {
            if (automationId is not null || runId is not null)
            {
                throw new ArgumentException("A direct task cannot carry automation run metadata.");
            }
        }
        else if (string.Equals(kind, CloudRunnerPayloadKinds.Automation, StringComparison.Ordinal))
        {
            automationId = CloudRequestGuard.Identifier(
                automationId!,
                128,
                nameof(automationId));
            runId = CloudRequestGuard.Identifier(runId!, 128, nameof(runId));
        }
        else
        {
            throw new ArgumentException("The cloud runner payload kind is invalid.", nameof(kind));
        }

        Kind = kind;
        AutomationId = automationId;
        RunId = runId;
        if ((leaseRunnerId is null) != (leaseToken is null) ||
            (leaseRunnerId is null) != (leaseGeneration == 0))
        {
            throw new ArgumentException("The cloud runner lease identity is incomplete.");
        }
        if (leaseRunnerId is not null)
        {
            LeaseRunnerId = CloudRequestGuard.Identifier(
                leaseRunnerId,
                128,
                nameof(leaseRunnerId));
            LeaseToken = CloudRequestGuard.Identifier(leaseToken!, 128, nameof(leaseToken));
            LeaseGeneration = leaseGeneration;
        }
    }

    internal static CloudRunnerJobIdentity Claimed(
        string jobId,
        string kind,
        string requiredCapability,
        string? automationId,
        string? runId,
        string leaseRunnerId,
        string leaseToken,
        long leaseGeneration) => new(
            jobId,
            kind,
            requiredCapability,
            automationId,
            runId,
            leaseRunnerId,
            leaseToken,
            leaseGeneration);

    public string JobId { get; }

    public string Kind { get; }

    public string RequiredCapability { get; }

    public string? AutomationId { get; }

    public string? RunId { get; }

    public string ResultKind => Kind == CloudRunnerPayloadKinds.Task
        ? CloudRunnerPayloadKinds.TaskResult
        : CloudRunnerPayloadKinds.AutomationResult;

    public long LeaseGeneration { get; }

    internal string? LeaseRunnerId { get; }

    internal string? LeaseToken { get; }
}

public sealed class CloudRunnerJob
{
    internal CloudRunnerJob(
        CloudRunnerJobIdentity identity,
        EncryptedEnvelope envelope,
        DateTimeOffset leaseExpiresAt)
    {
        Identity = identity;
        Envelope = envelope;
        LeaseExpiresAt = leaseExpiresAt;
    }

    public CloudRunnerJobIdentity Identity { get; }

    public string JobId => Identity.JobId;

    public string Kind => Identity.Kind;

    public string RequiredCapability => Identity.RequiredCapability;

    public string? AutomationId => Identity.AutomationId;

    public string? RunId => Identity.RunId;

    public EncryptedEnvelope Envelope { get; }

    public DateTimeOffset LeaseExpiresAt { get; }
}

public sealed record CloudAutomation(
    string AutomationId,
    string Name,
    int IntervalSeconds,
    bool Enabled,
    DateTimeOffset NextRunAt);
