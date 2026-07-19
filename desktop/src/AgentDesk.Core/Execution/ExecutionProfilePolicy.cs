namespace AgentDesk.Core.Execution;

public static class ExecutionProfilePolicy
{
    public static ExecutionProfileSelection SelectDefault(
        bool isTrustedWorkspace,
        bool isWslAvailable)
    {
        if (!isTrustedWorkspace && isWslAvailable)
        {
            return new(ExecutionProfile.WslStrict, false);
        }

        return new(
            ExecutionProfile.NativeProtected,
            RequiresSecurityAcknowledgement: !isTrustedWorkspace);
    }

    public static bool CanExecute(
        ExecutionProfile profile,
        bool isTrustedWorkspace,
        bool isWslAvailable,
        bool nativeRiskAcknowledged) => profile switch
        {
            ExecutionProfile.NativeProtected => isTrustedWorkspace || nativeRiskAcknowledged,
            ExecutionProfile.WslStrict => isWslAvailable,
            _ => false,
        };
}
