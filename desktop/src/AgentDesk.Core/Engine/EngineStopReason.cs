namespace AgentDesk.Core.Engine;

public enum EngineStopReason
{
    Unknown,
    EndTurn,
    MaxTokens,
    MaxTurnRequests,
    Refusal,
    Cancelled,
}
