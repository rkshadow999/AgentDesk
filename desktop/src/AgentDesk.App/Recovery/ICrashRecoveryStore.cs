namespace AgentDesk.App.Recovery;

public interface ICrashRecoveryStore
{
    Task<CrashRecoveryMarker?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        CrashRecoveryMarker marker,
        CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
