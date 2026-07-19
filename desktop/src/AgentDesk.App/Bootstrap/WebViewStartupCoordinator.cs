namespace AgentDesk.App;

public static class WebViewStartupCoordinator
{
    public static async Task InitializeSequentiallyAsync(
        params Func<Task>[] initializers)
    {
        ArgumentNullException.ThrowIfNull(initializers);

        foreach (var initializer in initializers)
        {
            ArgumentNullException.ThrowIfNull(initializer);
            await initializer();
        }
    }

    public static async Task InitializeOnceAsync(
        Func<bool> isInitialized,
        Func<Task> initializeAsync,
        Action configure)
    {
        ArgumentNullException.ThrowIfNull(isInitialized);
        ArgumentNullException.ThrowIfNull(initializeAsync);
        ArgumentNullException.ThrowIfNull(configure);

        if (!isInitialized())
        {
            await initializeAsync();
        }

        configure();
    }
}
