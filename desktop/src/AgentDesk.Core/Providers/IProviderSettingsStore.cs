namespace AgentDesk.Core.Providers;

public interface IProviderSettingsStore
{
    Task<ProviderProfile?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        ProviderProfile profile,
        CancellationToken cancellationToken = default);
}
