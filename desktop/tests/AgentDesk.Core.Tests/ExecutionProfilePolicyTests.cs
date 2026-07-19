using AgentDesk.Core.Execution;

namespace AgentDesk.Core.Tests;

public sealed class ExecutionProfilePolicyTests
{
    [Theory]
    [InlineData(true, true, ExecutionProfile.NativeProtected, false)]
    [InlineData(true, false, ExecutionProfile.NativeProtected, false)]
    [InlineData(false, true, ExecutionProfile.WslStrict, false)]
    [InlineData(false, false, ExecutionProfile.NativeProtected, true)]
    public void SelectDefault_UsesTrustAndWslAvailability(
        bool isTrustedWorkspace,
        bool isWslAvailable,
        ExecutionProfile expectedProfile,
        bool expectedAcknowledgement)
    {
        var selection = ExecutionProfilePolicy.SelectDefault(isTrustedWorkspace, isWslAvailable);

        Assert.Equal(expectedProfile, selection.Profile);
        Assert.Equal(expectedAcknowledgement, selection.RequiresSecurityAcknowledgement);
    }

    [Theory]
    [InlineData(ExecutionProfile.NativeProtected, true, false, false, true)]
    [InlineData(ExecutionProfile.NativeProtected, false, false, false, false)]
    [InlineData(ExecutionProfile.NativeProtected, false, false, true, true)]
    [InlineData(ExecutionProfile.WslStrict, false, false, true, false)]
    [InlineData(ExecutionProfile.WslStrict, false, true, false, true)]
    public void CanExecute_RequiresNativeAcknowledgementOrAvailableWsl(
        ExecutionProfile profile,
        bool isTrustedWorkspace,
        bool isWslAvailable,
        bool nativeRiskAcknowledged,
        bool expected)
    {
        var allowed = ExecutionProfilePolicy.CanExecute(
            profile,
            isTrustedWorkspace,
            isWslAvailable,
            nativeRiskAcknowledged);

        Assert.Equal(expected, allowed);
    }

    [Fact]
    public void CanExecute_RejectsUnknownExecutionProfiles()
    {
        var allowed = ExecutionProfilePolicy.CanExecute(
            (ExecutionProfile)int.MaxValue,
            isTrustedWorkspace: true,
            isWslAvailable: true,
            nativeRiskAcknowledged: true);

        Assert.False(allowed);
    }
}
