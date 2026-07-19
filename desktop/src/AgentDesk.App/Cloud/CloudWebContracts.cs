using AgentDesk.App.Bridge;

namespace AgentDesk.App.Cloud;

public abstract record CloudWebCommand(string RequestId) : WebCommand;

public sealed record CloudProfileGetWebCommand(string RequestId) : CloudWebCommand(RequestId);

public sealed record CloudProfileSaveLocalWebCommand(string RequestId) : CloudWebCommand(RequestId);

public sealed record CloudProfileSaveRemoteWebCommand(
    string RequestId,
    Uri BaseUri,
    string TeamId,
    string DeviceId) : CloudWebCommand(RequestId);

public sealed record CloudPairingExportWebCommand(string RequestId) : CloudWebCommand(RequestId);

public sealed record CloudPairingImportWebCommand(string RequestId) : CloudWebCommand(RequestId);

public sealed record CloudSessionUploadWebCommand(
    string RequestId,
    string SessionId) : CloudWebCommand(RequestId);

public sealed record CloudSessionDownloadWebCommand(
    string RequestId,
    string RemoteSessionId) : CloudWebCommand(RequestId);

public sealed record CloudSessionDeleteWebCommand(
    string RequestId,
    string RemoteSessionId) : CloudWebCommand(RequestId);

public sealed record CloudSessionExportWebCommand(
    string RequestId,
    string SessionId) : CloudWebCommand(RequestId);

public sealed record CloudHandoffCreateWebCommand(
    string RequestId,
    string SessionId,
    string TargetDeviceId) : CloudWebCommand(RequestId);

public sealed record CloudHandoffReceiveWebCommand(string RequestId) : CloudWebCommand(RequestId);

public sealed record CloudPolicyGetWebCommand(string RequestId) : CloudWebCommand(RequestId);

public sealed record CloudPolicyUpdateWebCommand(
    string RequestId,
    IReadOnlyList<string> AllowedExecutionProfiles,
    bool RemoteRunnerEnabled,
    bool UiAutomationEnabled,
    int MaximumConcurrentJobs,
    IReadOnlyList<string> AllowedPluginPublishers) : CloudWebCommand(RequestId);

public sealed record CloudRunnerRegisterWebCommand(
    string RequestId,
    string RunnerId,
    IReadOnlyList<string> Capabilities) : CloudWebCommand(RequestId);

public sealed record CloudRunnerQueueWebCommand(
    string RequestId,
    string RequiredCapability,
    string Task) : CloudWebCommand(RequestId);

public sealed record CloudRunnerClaimWebCommand(
    string RequestId,
    string RunnerId,
    int LeaseSeconds) : CloudWebCommand(RequestId);

public sealed record CloudRunnerCompleteWebCommand(
    string RequestId,
    string ClaimHandle,
    string JobId,
    string Result) : CloudWebCommand(RequestId);

public sealed record CloudAutomationListWebCommand(string RequestId) : CloudWebCommand(RequestId);

public sealed record CloudAutomationDisableWebCommand(
    string RequestId,
    string AutomationId) : CloudWebCommand(RequestId);

public sealed record CloudAutomationCreateWebCommand(
    string RequestId,
    string Name,
    int IntervalSeconds,
    string RequiredCapability,
    string Task) : CloudWebCommand(RequestId);

public sealed record CloudProfileWebEvent(
    string RequestId,
    bool LocalOnly,
    string? BaseUri,
    string? TeamId,
    string? DeviceId,
    bool HasAccessToken) : WebEvent;

public sealed record CloudPairingCompletedWebEvent(
    string RequestId,
    string Operation) : WebEvent;

public sealed record CloudSessionUploadedWebEvent(
    string RequestId,
    string SessionId,
    int Revision) : WebEvent;

public sealed record CloudSessionImportedWebEvent(
    string RequestId,
    string RemoteSessionId,
    bool Found,
    int? Revision = null,
    string? ImportedSessionId = null) : WebEvent;

public sealed record CloudSessionDeletedWebEvent(
    string RequestId,
    string RemoteSessionId,
    bool Found,
    int? Revision = null) : WebEvent;

public sealed record CloudSessionExportedWebEvent(
    string RequestId,
    string SessionId,
    string FileName) : WebEvent;

public sealed record CloudHandoffCreatedWebEvent(
    string RequestId,
    string HandoffId,
    string SessionId,
    string TargetDeviceId) : WebEvent;

public sealed record CloudHandoffImportWebSummary(
    string HandoffId,
    string SourceDeviceId,
    string RemoteSessionId,
    string ImportedSessionId);

public sealed record CloudHandoffsReceivedWebEvent(
    string RequestId,
    IReadOnlyList<CloudHandoffImportWebSummary> Imports) : WebEvent;

public sealed record CloudPolicyWebEvent(
    string RequestId,
    int Version,
    IReadOnlyList<string> AllowedExecutionProfiles,
    bool RemoteRunnerEnabled,
    bool UiAutomationEnabled,
    int MaximumConcurrentJobs,
    IReadOnlyList<string> AllowedPluginPublishers) : WebEvent;

public sealed record CloudNotificationWebEvent(
    string Kind,
    string? ResourceId = null,
    int? PolicyVersion = null) : WebEvent;

public sealed record CloudRunnerRegisteredWebEvent(
    string RequestId,
    string RunnerId,
    IReadOnlyList<string> Capabilities) : WebEvent;

public sealed record CloudRunnerQueuedWebEvent(
    string RequestId,
    string JobId) : WebEvent;

public sealed record CloudRunnerClaimedWebEvent(
    string RequestId,
    bool Found,
    string? JobId = null,
    string? RequiredCapability = null,
    string? Task = null,
    DateTimeOffset? LeaseExpiresAt = null,
    string? ClaimHandle = null) : WebEvent;

public sealed record CloudRunnerCompletedWebEvent(
    string RequestId,
    string ClaimHandle,
    string JobId) : WebEvent;

public sealed record CloudAutomationWebSummary(
    string AutomationId,
    string Name,
    int IntervalSeconds,
    bool Enabled,
    DateTimeOffset NextRunAt);

public sealed record CloudAutomationsWebEvent(
    string RequestId,
    IReadOnlyList<CloudAutomationWebSummary> Automations) : WebEvent;

public sealed record CloudAutomationDisabledWebEvent(
    string RequestId,
    string AutomationId,
    bool Disabled) : WebEvent;

public sealed record CloudAutomationCreatedWebEvent(
    string RequestId,
    CloudAutomationWebSummary Automation) : WebEvent;

public sealed record CloudErrorWebEvent(
    string RequestId,
    string Operation) : WebEvent;

public sealed record CloudCancelledWebEvent(
    string RequestId,
    string Operation) : WebEvent;
