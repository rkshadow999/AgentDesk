namespace AgentDesk.Core.Engine;

public enum PermissionDecisionKind
{
    Selected,
    Cancelled,
}

public sealed record PermissionDecision
{
    private PermissionDecision(PermissionDecisionKind kind, string? optionId)
    {
        Kind = kind;
        OptionId = optionId;
    }

    public PermissionDecisionKind Kind { get; }

    public string? OptionId { get; }

    public static PermissionDecision Cancelled { get; } =
        new(PermissionDecisionKind.Cancelled, optionId: null);

    public static PermissionDecision Selected(string optionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optionId);
        return new PermissionDecision(PermissionDecisionKind.Selected, optionId);
    }
}
