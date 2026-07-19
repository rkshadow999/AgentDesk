namespace AgentDesk.Core.Engine;

public sealed record EngineCapabilities(
    int ProtocolVersion,
    bool LoadSession,
    bool ImagePrompts,
    bool AudioPrompts,
    bool EmbeddedContextPrompts,
    bool AgentDeskExtensions,
    bool AgentDeskHealth,
    bool StrictSandboxActive = false)
{
    public IReadOnlyCollection<SessionMode> SessionModes { get; init; } = [SessionMode.Default];

    public MemoryManagementCapabilities Memory { get; init; } =
        MemoryManagementCapabilities.Unsupported;

    public bool Supports(SessionMode mode) =>
        mode is SessionMode.Default || SessionModes.Contains(mode);

    public static EngineCapabilities Uninitialized { get; } = new(
        0,
        LoadSession: false,
        ImagePrompts: false,
        AudioPrompts: false,
        EmbeddedContextPrompts: false,
        AgentDeskExtensions: false,
        AgentDeskHealth: false);
}
