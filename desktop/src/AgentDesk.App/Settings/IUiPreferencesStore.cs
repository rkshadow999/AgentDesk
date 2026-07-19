namespace AgentDesk.App.Settings;

public interface IUiPreferencesStore
{
    Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        UiPreferences preferences,
        CancellationToken cancellationToken = default);
}
