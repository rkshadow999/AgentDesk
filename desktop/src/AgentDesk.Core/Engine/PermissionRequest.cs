using System.Text.Json;

namespace AgentDesk.Core.Engine;

public sealed class PermissionRequest(
    string requestId,
    SessionId sessionId,
    string toolCallId,
    string title,
    IReadOnlyList<PermissionOption> options,
    IReadOnlyList<string> locations,
    string? toolKind = null,
    JsonElement? rawInput = null) : EventArgs
{
    public string RequestId { get; } = requestId;

    public SessionId SessionId { get; } = sessionId;

    public string ToolCallId { get; } = toolCallId;

    public string Title { get; } = title;

    public IReadOnlyList<PermissionOption> Options { get; } = options;

    public IReadOnlyList<string> Locations { get; } = locations;

    public string? ToolKind { get; } = toolKind;

    public JsonElement? RawInput { get; } = rawInput is { } value ? value.Clone() : null;
}
