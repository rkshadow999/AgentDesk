namespace AgentDesk.Cloud.Client;

public interface ICloudSyncMetadataStore
{
    ValueTask<CloudConnectionProfile?> ReadProfileAsync(
        CancellationToken cancellationToken = default);

    ValueTask SaveProfileAsync(
        CloudConnectionProfile profile,
        CancellationToken cancellationToken = default);

    ValueTask<int?> ReadRevisionAsync(
        CloudSyncMetadataScope scope,
        string sessionId,
        CancellationToken cancellationToken = default);

    ValueTask SaveRevisionAsync(
        CloudSyncMetadataScope scope,
        string sessionId,
        int revision,
        CancellationToken cancellationToken = default);

    ValueTask DeleteRevisionAsync(
        CloudSyncMetadataScope scope,
        string sessionId,
        CancellationToken cancellationToken = default);
}
