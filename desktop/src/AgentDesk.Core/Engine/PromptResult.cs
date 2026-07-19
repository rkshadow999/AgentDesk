namespace AgentDesk.Core.Engine;

public sealed record PromptResult(EngineStopReason StopReason, string RawStopReason);
