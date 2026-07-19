using AgentDesk.Cloud.Client;
using AgentDesk.Core.Engine;

namespace AgentDesk.App.Cloud;

public interface IAgentDeskCloudDesktopService
{
    Task StartNotificationsAsync(
        Func<CloudNotification, CancellationToken, Task> notificationHandler,
        CancellationToken cancellationToken = default);

    Task StopNotificationsAsync(CancellationToken cancellationToken = default);

    Task<AgentDeskCloudProfileSnapshot> LoadProfileAsync(
        CancellationToken cancellationToken = default);

    Task<AgentDeskCloudProfileSnapshot> SaveRemoteProfileAsync(
        Uri baseUri,
        string teamId,
        string deviceId,
        string accessToken,
        CancellationToken cancellationToken = default);

    Task<AgentDeskCloudProfileSnapshot> SaveLocalOnlyProfileAsync(
        CancellationToken cancellationToken = default);

    Task<RecoveryKeyPairingPackage> ExportRecoveryKeyPairingPackageAsync(
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken = default);

    Task ImportRecoveryKeyPairingPackageAsync(
        RecoveryKeyPairingPackage package,
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken = default);

    Task<int> UploadSessionAsync(
        IEngineClient engine,
        SessionId sessionId,
        CancellationToken cancellationToken = default);

    Task<int?> DeleteSessionAsync(
        string remoteSessionId,
        CancellationToken cancellationToken = default);

    Task<EngineSessionDocument> ExportSessionAsync(
        IEngineClient engine,
        SessionId sessionId,
        CancellationToken cancellationToken = default);

    Task<EngineCloudImportResult?> DownloadAndImportSessionAsync(
        IEngineClient engine,
        string remoteSessionId,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    Task<CloudHandoff> CreateHandoffAsync(
        IEngineClient engine,
        SessionId sessionId,
        string targetDeviceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EngineCloudHandoffImportResult>> ReceiveHandoffsAsync(
        IEngineClient engine,
        string workingDirectory,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<CloudTeamPolicy> GetPolicyAsync(CancellationToken cancellationToken = default);

    Task<CloudTeamPolicy> UpdatePolicyAsync(
        CloudTeamPolicyUpdate update,
        CancellationToken cancellationToken = default);

    Task RegisterRunnerAsync(
        string runnerId,
        IReadOnlyList<string> capabilities,
        CancellationToken cancellationToken = default);

    Task<CloudJobReceipt> QueueRunnerJobAsync(
        string requiredCapability,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task<CloudJobReceipt> QueueRunnerTaskAsync(
        string requiredCapability,
        string task,
        CancellationToken cancellationToken = default);

    Task<AgentDeskCloudRunnerJobClaim?> ClaimRunnerJobAsync(
        string runnerId,
        int leaseSeconds = 60,
        CancellationToken cancellationToken = default);

    Task<AgentDeskCloudRunnerTask?> ClaimRunnerTaskAsync(
        string runnerId,
        int leaseSeconds = 60,
        CancellationToken cancellationToken = default);

    Task CompleteRunnerJobAsync(
        string claimHandle,
        string jobId,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task CompleteRunnerTaskAsync(
        string claimHandle,
        string jobId,
        string result,
        CancellationToken cancellationToken = default);

    Task<CloudAutomation> CreateAutomationAsync(
        string name,
        int intervalSeconds,
        string requiredCapability,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task<CloudAutomation> CreateAutomationTaskAsync(
        string name,
        int intervalSeconds,
        string requiredCapability,
        string task,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudAutomation>> ListAutomationsAsync(
        CancellationToken cancellationToken = default);

    Task<bool> DisableAutomationAsync(
        string automationId,
        CancellationToken cancellationToken = default);
}
