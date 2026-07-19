namespace AgentDesk.App.Tests;

public sealed class WebViewStartupCoordinatorTests
{
    [Fact]
    public async Task InitializeSequentiallyAsync_never_overlaps_surface_startup()
    {
        var activeInitializers = 0;
        var maximumConcurrency = 0;
        var order = new List<string>();

        async Task InitializeAsync(string name)
        {
            order.Add($"{name}:start");
            var active = Interlocked.Increment(ref activeInitializers);
            maximumConcurrency = Math.Max(maximumConcurrency, active);
            await Task.Delay(20);
            Interlocked.Decrement(ref activeInitializers);
            order.Add($"{name}:end");
        }

        await WebViewStartupCoordinator.InitializeSequentiallyAsync(
            () => InitializeAsync("workbench"),
            () => InitializeAsync("inspector"));

        Assert.Equal(1, maximumConcurrency);
        Assert.Equal(
            ["workbench:start", "workbench:end", "inspector:start", "inspector:end"],
            order);
    }

    [Fact]
    public async Task InitializeOnceAsync_reuses_an_existing_webview_environment()
    {
        var initializeCalls = 0;
        var configureCalls = 0;

        await WebViewStartupCoordinator.InitializeOnceAsync(
            isInitialized: () => true,
            initializeAsync: () =>
            {
                initializeCalls++;
                return Task.CompletedTask;
            },
            configure: () => configureCalls++);

        Assert.Equal(0, initializeCalls);
        Assert.Equal(1, configureCalls);
    }
}
