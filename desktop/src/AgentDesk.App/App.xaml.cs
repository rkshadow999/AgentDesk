using Microsoft.UI.Xaml;
using Microsoft.Windows.Globalization;
using AgentDesk.App.Settings;

namespace AgentDesk.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        LanguagePolicy.ApplyPreferred(
            LoadSavedLanguage(),
            ApplicationLanguages.PrimaryLanguageOverride,
            value => ApplicationLanguages.PrimaryLanguageOverride = value);
        UnhandledException += (_, eventArgs) => WriteStartupFailure(eventArgs.Exception);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLaunchOptions options;
        string? launchError = null;

        try
        {
            options = AppLaunchOptions.Parse(
                Environment.GetCommandLineArgs().Skip(1).ToArray(),
                allowExternalWebRoot: AllowExternalWebRoot);
        }
        catch (ArgumentException exception)
        {
            options = new(null, null);
            launchError = exception.Message;
        }

        try
        {
            _window = new MainWindow(options, launchError);
            _window.Activate();
        }
        catch (Exception exception)
        {
            WriteStartupFailure(exception);
            throw;
        }
    }

#if DEBUG
    private const bool AllowExternalWebRoot = true;
#else
    private const bool AllowExternalWebRoot = false;
#endif

    private static void WriteStartupFailure(Exception exception)
    {
        var logPath = Environment.GetEnvironmentVariable("AGENTDESK_STARTUP_LOG");
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        try
        {
            File.WriteAllText(logPath, exception.ToString());
        }
        catch
        {
            // Best-effort diagnostics must not replace the original startup failure.
        }
    }

    private static string? LoadSavedLanguage()
    {
        if (!File.Exists(JsonUiPreferencesStore.DefaultSettingsPath))
        {
            return null;
        }

        try
        {
            return new JsonUiPreferencesStore().LoadAsync().GetAwaiter().GetResult().Language;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
