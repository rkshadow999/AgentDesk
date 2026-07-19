using AgentDesk.App.Bridge;
using AgentDesk.App.Cloud;
using AgentDesk.App.Settings;

namespace AgentDesk.App.Tests;

public sealed class WebEventRoutingPolicyTests
{
    [Fact]
    public void InspectorReceivesOnlyItsExplicitEventAllowlist()
    {
        var engine = new EngineStatusWebEvent("running", SessionId: "session-1");
        var update = new SessionUpdateWebEvent("session-1", "plan", Update: null);

        Assert.Same(engine, WebEventRoutingPolicy.ProjectForInspector(engine));
        Assert.Same(update, WebEventRoutingPolicy.ProjectForInspector(update));
        Assert.Null(WebEventRoutingPolicy.ProjectForInspector(
            new ProviderStatusWebEvent(
                "loaded",
                "https://example.com/v1",
                "grok-4.5",
                "chat_completions",
                AllowInsecureTransport: false,
                HasCredential: true)));
        Assert.Null(WebEventRoutingPolicy.ProjectForInspector(
            new CredentialStatusWebEvent("saved")));
        Assert.Null(WebEventRoutingPolicy.ProjectForInspector(
            new PermissionRequestedWebEvent(
                "permission-1",
                "session-1",
                "tool-1",
                "Sensitive permission",
                [],
                [])));
        Assert.Null(WebEventRoutingPolicy.ProjectForInspector(
            new CloudErrorWebEvent("request-1", "profile-save")));
    }

    [Fact]
    public void InspectorPreferenceProjectionRemovesTheComposerDraft()
    {
        var preferences = UiPreferences.Default with
        {
            Language = "en-US",
            ComposerDraft = "private draft",
            WindowsAutomationEnabled = true,
        };

        var projected = Assert.IsType<UiPreferencesChangedWebEvent>(
            WebEventRoutingPolicy.ProjectForInspector(
                new UiPreferencesChangedWebEvent(preferences, RestartRequired: true)));

        Assert.Equal("en-US", projected.Preferences.Language);
        Assert.Equal(string.Empty, projected.Preferences.ComposerDraft);
        Assert.True(projected.RestartRequired);
    }
}
