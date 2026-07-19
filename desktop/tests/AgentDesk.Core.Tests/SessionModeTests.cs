using AgentDesk.Core.Engine;

namespace AgentDesk.Core.Tests;

public sealed class SessionModeTests
{
    [Fact]
    public void UninitializedCapabilitiesSupportOnlyDefaultMode()
    {
        Assert.True(EngineCapabilities.Uninitialized.Supports(SessionMode.Default));
        Assert.False(EngineCapabilities.Uninitialized.Supports(SessionMode.Plan));
    }

    [Fact]
    public void CapabilitiesNormalizeModesAndAlwaysRetainDefault()
    {
        var capabilities = EngineCapabilities.Uninitialized with
        {
            SessionModes = [SessionMode.Plan, SessionMode.Plan],
        };

        Assert.True(capabilities.Supports(SessionMode.Default));
        Assert.True(capabilities.Supports(SessionMode.Plan));
    }
}
