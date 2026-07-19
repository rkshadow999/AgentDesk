namespace AgentDesk.Cloud.Client;

public interface ICloudConnectionProfileStore
{
    Task<CloudConnectionProfile> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        CloudConnectionProfile profile,
        CancellationToken cancellationToken = default);
}
