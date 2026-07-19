using System.Security.Cryptography;
using AgentDesk.Core.Engine;

namespace AgentDesk.Cloud.Client;

public sealed class EngineCloudSessionWorkflow
{
    private readonly CloudSyncCoordinator _syncCoordinator;
    private readonly EncryptedHandoffCoordinator _handoffCoordinator;

    public EngineCloudSessionWorkflow(
        CloudSyncCoordinator syncCoordinator,
        EncryptedHandoffCoordinator handoffCoordinator)
    {
        ArgumentNullException.ThrowIfNull(syncCoordinator);
        ArgumentNullException.ThrowIfNull(handoffCoordinator);
        _syncCoordinator = syncCoordinator;
        _handoffCoordinator = handoffCoordinator;
    }

    public async Task<int> UploadAsync(
        IEngineClient engine,
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(sessionId);

        var document = await engine
            .ExportSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        var bytes = document.ExportUtf8Json();
        try
        {
            return await _syncCoordinator
                .UploadAsync(
                    sessionId.Value,
                    SessionSyncDocument.FromUtf8Json(bytes),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task<EngineCloudImportResult?> DownloadAndImportAsync(
        IEngineClient engine,
        string remoteSessionId,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var downloaded = await _syncCoordinator
            .DownloadAsync(remoteSessionId, cancellationToken)
            .ConfigureAwait(false);
        if (downloaded is null)
        {
            return null;
        }

        var bytes = downloaded.Document.ExportUtf8Json();
        try
        {
            var importedSessionId = await engine
                .ImportSessionAsync(
                    EngineSessionDocument.FromUtf8Json(bytes),
                    workingDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            return new EngineCloudImportResult(
                downloaded.SessionId,
                downloaded.Revision,
                importedSessionId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public Task<int?> DeleteAsync(
        string remoteSessionId,
        CancellationToken cancellationToken = default) =>
        _syncCoordinator.DeleteAsync(remoteSessionId, cancellationToken);

    public async Task<CloudHandoff> CreateHandoffAsync(
        IEngineClient engine,
        SessionId sessionId,
        string targetDeviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(sessionId);

        var document = await engine
            .ExportSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        var bytes = document.ExportUtf8Json();
        try
        {
            return await _handoffCoordinator
                .CreateAsync(
                    targetDeviceId,
                    sessionId.Value,
                    SessionSyncDocument.FromUtf8Json(bytes),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task<IReadOnlyList<EngineCloudHandoffImportResult>> ReceiveHandoffsAsync(
        IEngineClient engine,
        string workingDirectory,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var handoffs = await _handoffCoordinator
            .ListAsync(limit, cancellationToken)
            .ConfigureAwait(false);
        if (handoffs.Count == 0)
        {
            return [];
        }

        var imported = new List<EngineCloudHandoffImportResult>(handoffs.Count);
        foreach (var handoff in handoffs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = handoff.Document.ExportUtf8Json();
            SessionId importedSessionId;
            try
            {
                importedSessionId = await engine
                    .ImportSessionAsync(
                        EngineSessionDocument.FromUtf8Json(bytes),
                        workingDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            var acknowledged = await _handoffCoordinator
                .AcknowledgeAsync(handoff.HandoffId, cancellationToken)
                .ConfigureAwait(false);
            if (!acknowledged)
            {
                throw new CloudClientException(
                    CloudClientErrorKind.InvalidResponse,
                    "The cloud did not acknowledge the imported handoff.");
            }

            imported.Add(new EngineCloudHandoffImportResult(
                handoff.HandoffId,
                handoff.SourceDeviceId,
                handoff.SessionId,
                importedSessionId));
        }

        return imported;
    }
}

public sealed record EngineCloudImportResult(
    string RemoteSessionId,
    int Revision,
    SessionId ImportedSessionId);

public sealed record EngineCloudHandoffImportResult(
    string HandoffId,
    string SourceDeviceId,
    string RemoteSessionId,
    SessionId ImportedSessionId);
