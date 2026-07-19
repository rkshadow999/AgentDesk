using System.Reflection;

namespace AgentDesk.App.Tests;

public sealed class ExternalLinkPolicyTests
{
    [Theory]
    [InlineData("https://example.com/docs", "https")]
    [InlineData("http://localhost:8080/status?ready=1#health", "http")]
    public void MainWindow_AllowsOnlyAbsoluteWebLinks(string rawUri, string expectedScheme)
    {
        var method = FindUriPolicy();
        object?[] arguments = [rawUri, null];

        var allowed = Assert.IsType<bool>(method.Invoke(null, arguments));

        Assert.True(allowed);
        var resolved = Assert.IsType<Uri>(arguments[1]);
        Assert.Equal(expectedScheme, resolved.Scheme);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("docs/getting-started")]
    [InlineData("mailto:support@example.com")]
    [InlineData("file:///C:/Windows/System32/notepad.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,unsafe")]
    [InlineData("https://user@example.com/private")]
    public void MainWindow_RejectsLinksThatMustNotLeaveTheWebView(string? rawUri)
    {
        var method = FindUriPolicy();
        object?[] arguments = [rawUri, null];

        var allowed = Assert.IsType<bool>(method.Invoke(null, arguments));

        Assert.False(allowed);
        Assert.Null(arguments[1]);
    }

    [Fact]
    public void MainWindow_NewWindowHandlerUsesTheSystemUriLauncher()
    {
        var source = File.ReadAllText(FindMainWindowSource());

        Assert.Contains("Launcher.LaunchUriAsync", source, StringComparison.Ordinal);
        Assert.Contains("args.GetDeferral()", source, StringComparison.Ordinal);
    }

    private static MethodInfo FindUriPolicy()
    {
        var method = typeof(MainWindow).GetMethod(
            "TryCreateExternalLinkUri",
            BindingFlags.Static | BindingFlags.NonPublic);

        return Assert.IsAssignableFrom<MethodInfo>(method);
    }

    private static string FindMainWindowSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "desktop",
                "src",
                "AgentDesk.App",
                "MainWindow.xaml.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Unable to locate MainWindow.xaml.cs for the source contract test.");
    }
}
