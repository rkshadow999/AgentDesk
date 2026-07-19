using System.Collections.Concurrent;

namespace AgentDesk.Cloud;

internal sealed class CloudNotificationConnectionRegistry
{
    private readonly ConcurrentDictionary<SubjectKey, SubjectState> _subjects = new();

    public CloudNotificationConnectionRegistration Register(
        string teamId,
        string subjectId,
        string connectionId,
        Action abort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(abort);
        var state = _subjects.GetOrAdd(new SubjectKey(teamId, subjectId), _ => new SubjectState());
        state.Connections[connectionId] = abort;
        if (Volatile.Read(ref state.Revoked) == 0)
        {
            return new CloudNotificationConnectionRegistration(
                rejected: false,
                () => state.Connections.TryRemove(connectionId, out _));
        }

        _ = state.Connections.TryRemove(connectionId, out _);
        TryAbort(abort);
        return new CloudNotificationConnectionRegistration(rejected: true, () => { });
    }

    public void Revoke(string teamId, string subjectId)
    {
        var state = _subjects.GetOrAdd(new SubjectKey(teamId, subjectId), _ => new SubjectState());
        Volatile.Write(ref state.Revoked, 1);
        foreach (var connection in state.Connections.ToArray())
        {
            if (state.Connections.TryRemove(connection.Key, out var abort))
            {
                TryAbort(abort);
            }
        }
    }

    public void Allow(string teamId, string subjectId)
    {
        var state = _subjects.GetOrAdd(new SubjectKey(teamId, subjectId), _ => new SubjectState());
        Volatile.Write(ref state.Revoked, 0);
    }

    private static void TryAbort(Action abort)
    {
        try
        {
            abort();
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
        }
    }

    private sealed record SubjectKey(string TeamId, string SubjectId);

    private sealed class SubjectState
    {
        public ConcurrentDictionary<string, Action> Connections { get; } =
            new(StringComparer.Ordinal);

        public int Revoked;
    }
}

internal sealed class CloudNotificationConnectionRegistration(
    bool rejected,
    Action dispose) : IDisposable
{
    private Action? _dispose = dispose;

    public bool Rejected { get; } = rejected;

    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
