namespace AgentDesk.App.Bridge;

public static class WebEventRoutingPolicy
{
    public static WebEvent? ProjectForInspector(WebEvent webEvent)
    {
        ArgumentNullException.ThrowIfNull(webEvent);
        return webEvent switch
        {
            EngineStatusWebEvent => webEvent,
            SessionUpdateWebEvent => webEvent,
            UiPreferencesChangedWebEvent preferences => preferences with
            {
                Preferences = preferences.Preferences with { ComposerDraft = string.Empty },
            },
            _ => null,
        };
    }
}
