namespace AgentDesk.Cloud.Client;

public interface IAgentDeskCloudClient
{
    Task<CloudIssuedToken> CreateTokenAsync(
        string subjectId,
        CloudTokenRole role,
        CancellationToken cancellationToken = default);

    Task RevokeTokenAsync(
        string subjectId,
        CancellationToken cancellationToken = default);

    Task<CloudTeamPolicy> GetPolicyAsync(CancellationToken cancellationToken = default);

    Task<CloudTeamPolicy> UpdatePolicyAsync(
        CloudTeamPolicyUpdate update,
        CancellationToken cancellationToken = default);

    Task<CloudSessionWriteReceipt> PutSessionAsync(
        string sessionId,
        int revision,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task<CloudSyncedSession?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<CloudSessionDeleteReceipt> DeleteSessionAsync(
        string sessionId,
        int knownRevision,
        CancellationToken cancellationToken = default);

    Task<CloudHandoff> CreateHandoffAsync(
        string handoffId,
        string targetDeviceId,
        string sessionId,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudHandoff>> ListHandoffsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<bool> AcknowledgeHandoffAsync(
        string handoffId,
        CancellationToken cancellationToken = default);

    Task RegisterRunnerAsync(
        string runnerId,
        IReadOnlyList<string> capabilities,
        CancellationToken cancellationToken = default);

    Task<CloudJobReceipt> QueueJobAsync(
        CloudRunnerJobIdentity identity,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task<CloudRunnerJob?> ClaimJobAsync(
        string runnerId,
        int leaseSeconds = 60,
        CancellationToken cancellationToken = default);

    Task CompleteJobAsync(
        CloudRunnerJobIdentity identity,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task<CloudAutomation> CreateAutomationAsync(
        string automationId,
        string name,
        int intervalSeconds,
        string requiredCapability,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudAutomation>> ListAutomationsAsync(
        CancellationToken cancellationToken = default);

    Task<bool> DisableAutomationAsync(
        string automationId,
        CancellationToken cancellationToken = default);
}
