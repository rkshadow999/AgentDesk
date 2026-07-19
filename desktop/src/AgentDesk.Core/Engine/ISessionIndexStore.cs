namespace AgentDesk.Core.Engine;

public interface ISessionIndexStore
{
    Task UpsertAsync(
        IReadOnlyCollection<SessionSummary> sessions,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionSummary>> SearchAsync(
        string? workspacePath,
        string? query,
        bool archived,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);

    Task<bool> SetArchivedAsync(
        SessionId sessionId,
        bool archived,
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetArchivedIdsAsync(
        IReadOnlyCollection<SessionId> sessionIds,
        CancellationToken cancellationToken = default);

    async Task<SessionSummary?> FindByIdAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        const int pageSize = 100;
        foreach (var archived in new[] { false, true })
        {
            for (var offset = 0; ; offset += pageSize)
            {
                var page = await SearchAsync(
                        workspacePath: null,
                        query: null,
                        archived,
                        pageSize,
                        offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                var match = page.FirstOrDefault(
                    item => string.Equals(
                        item.SessionId.Value,
                        sessionId.Value,
                        StringComparison.Ordinal));
                if (match is not null)
                {
                    return match;
                }
                if (page.Count < pageSize)
                {
                    break;
                }
            }
        }
        return null;
    }
}
