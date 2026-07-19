namespace AgentDesk.Core.Engine;

public enum PermissionOptionKind
{
    AllowOnce,
    AllowAlways,
    RejectOnce,
    RejectAlways,
}

public sealed record PermissionOption(
    string OptionId,
    string Name,
    PermissionOptionKind Kind);
