namespace AgentDesk.App.Settings;

public interface IRecentWorkspaceStore
{
    Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default);
}
