using System.Text.Json;

namespace AgentDesk.Engine.Transport;

public sealed record JsonRpcNotification(string Method, JsonElement Parameters);
