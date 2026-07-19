using System.Text.Json;

namespace AgentDesk.Core.Engine;

public sealed class EngineEvent(
    SessionId sessionId,
    string updateKind,
    JsonElement update,
    JsonElement? metadata) : EventArgs
{
    public SessionId SessionId { get; } = sessionId;

    public string UpdateKind { get; } = updateKind;

    public JsonElement Update { get; } = update;

    public JsonElement? Metadata { get; } = metadata;
}
