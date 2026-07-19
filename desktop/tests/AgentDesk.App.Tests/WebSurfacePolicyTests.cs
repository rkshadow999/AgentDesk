using System.Reflection;
using System.Xml.Linq;
using Microsoft.UI.Xaml;

namespace AgentDesk.App.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WebSurfacePolicyEnvironmentCollection
{
    public const string Name = nameof(WebSurfacePolicyEnvironmentCollection);
}

[Collection(WebSurfacePolicyEnvironmentCollection.Name)]
public sealed class WebSurfacePolicyTests : IDisposable
{
    private const string TestModeVariable = "AGENTDESK_WEBVIEW2_TEST_MODE";
    private const string TestRootVariable = "AGENTDESK_WEBVIEW2_TEST_USER_DATA_ROOT";
    private readonly string? _previousTestMode =
        Environment.GetEnvironmentVariable(TestModeVariable);
    private readonly string? _previousTestRoot =
        Environment.GetEnvironmentVariable(TestRootVariable);

    public WebSurfacePolicyTests()
    {
        Environment.SetEnvironmentVariable(TestModeVariable, null);
        Environment.SetEnvironmentVariable(TestRootVariable, null);
    }

    [Fact]
    public void Create_UsesDistinctOriginsProfilesAndSurfaceRoutes()
    {
        const string localAppData = "C:\\Users\\test\\AppData\\Local";

        var workbench = WebSurfacePolicy.Create(WebSurfaceKind.Workbench, localAppData);
        var inspector = WebSurfacePolicy.Create(WebSurfaceKind.Inspector, localAppData);

        Assert.NotEqual(workbench.VirtualHostName, inspector.VirtualHostName);
        Assert.Equal(workbench.UserDataFolder, inspector.UserDataFolder);
        Assert.NotEqual(workbench.ProfileName, inspector.ProfileName);
        Assert.Equal("workbench.agentdesk.local", workbench.VirtualHostName);
        Assert.Equal("inspector.agentdesk.local", inspector.VirtualHostName);
        Assert.Equal("workbench", workbench.ProfileName);
        Assert.Equal("inspector", inspector.ProfileName);
        Assert.Equal("?surface=workbench", workbench.Source.Query);
        Assert.Equal("?surface=inspector", inspector.Source.Query);
        Assert.StartsWith(Path.GetFullPath(localAppData), workbench.UserDataFolder);
        Assert.StartsWith(Path.GetFullPath(localAppData), inspector.UserDataFolder);
    }

    [Fact]
    public void IsAllowedSource_AcceptsOnlyTheSurfaceExactHttpsOrigin()
    {
        const string localAppData = "C:\\Users\\test\\AppData\\Local";
        var workbench = WebSurfacePolicy.Create(WebSurfaceKind.Workbench, localAppData);
        var inspector = WebSurfacePolicy.Create(WebSurfaceKind.Inspector, localAppData);

        Assert.True(WebSurfacePolicy.IsAllowedSource(
            workbench,
            "https://workbench.agentdesk.local/assets/app.js"));
        Assert.True(WebSurfacePolicy.IsAllowedSource(
            inspector,
            inspector.Source.AbsoluteUri));
        Assert.False(WebSurfacePolicy.IsAllowedSource(
            workbench,
            inspector.Source.AbsoluteUri));
        Assert.False(WebSurfacePolicy.IsAllowedSource(
            workbench,
            "http://workbench.agentdesk.local/index.html"));
        Assert.False(WebSurfacePolicy.IsAllowedSource(
            workbench,
            "https://workbench.agentdesk.local.example.com/index.html"));
        Assert.False(WebSurfacePolicy.IsAllowedSource(
            workbench,
            "https://workbench.agentdesk.local:444/index.html"));
        Assert.False(WebSurfacePolicy.IsAllowedSource(
            workbench,
            "https://user@workbench.agentdesk.local/index.html"));
        Assert.False(WebSurfacePolicy.IsAllowedSource(workbench, "not-a-uri"));
    }

    [Fact]
    public void IsSurfaceProfile_matches_the_isolated_profile_without_wrapper_identity()
    {
        const string localAppData = "C:\\Users\\test\\AppData\\Local";
        var workbench = WebSurfacePolicy.Create(WebSurfaceKind.Workbench, localAppData);
        var inspector = WebSurfacePolicy.Create(WebSurfaceKind.Inspector, localAppData);

        Assert.True(WebSurfacePolicy.IsSurfaceProfile(workbench, "WORKBENCH"));
        Assert.True(WebSurfacePolicy.IsSurfaceProfile(inspector, "inspector"));
        Assert.False(WebSurfacePolicy.IsSurfaceProfile(workbench, inspector.ProfileName));
        Assert.False(WebSurfacePolicy.IsSurfaceProfile(workbench, null));
    }

    [Fact]
    public void Create_UsesAnExistingTemporaryUserDataRootOnlyInExplicitTestMode()
    {
        var testRoot = CreateTemporaryDirectory();
        try
        {
            Environment.SetEnvironmentVariable(TestModeVariable, "1");
            Environment.SetEnvironmentVariable(TestRootVariable, testRoot);

            var workbench = WebSurfacePolicy.Create(
                WebSurfaceKind.Workbench,
                "C:\\Users\\test\\AppData\\Local");
            var inspector = WebSurfacePolicy.Create(
                WebSurfaceKind.Inspector,
                "C:\\Users\\test\\AppData\\Local");

            Assert.Equal(Path.GetFullPath(testRoot), workbench.UserDataFolder);
            Assert.Equal(workbench.UserDataFolder, inspector.UserDataFolder);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestModeVariable, null);
            Environment.SetEnvironmentVariable(TestRootVariable, null);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void Create_IgnoresATestRootWithoutExplicitTestMode()
    {
        var testRoot = CreateTemporaryDirectory();
        try
        {
            Environment.SetEnvironmentVariable(TestRootVariable, testRoot);

            var surface = WebSurfacePolicy.Create(
                WebSurfaceKind.Workbench,
                "C:\\Users\\test\\AppData\\Local");

            Assert.NotEqual(Path.GetFullPath(testRoot), surface.UserDataFolder);
            Assert.Equal(
                Path.GetFullPath("C:\\Users\\test\\AppData\\Local\\AgentDesk\\WebView2"),
                surface.UserDataFolder);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestRootVariable, null);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void Create_FailsClosedForInvalidTestModeConfiguration()
    {
        var validRoot = CreateTemporaryDirectory();
        var missingRoot = Path.Combine(
            Path.GetTempPath(),
            $"AgentDesk-WebView2-missing-{Guid.NewGuid():N}");
        var filePath = Path.Combine(validRoot, "not-a-directory.txt");
        File.WriteAllText(filePath, "not a directory");
        var outsideTemp = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            $"AgentDesk-WebView2-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideTemp);

        try
        {
            var cases = new (string? Mode, string? Root)[]
            {
                ("true", validRoot),
                ("1", null),
                ("1", "relative-test-root"),
                ("1", Path.GetTempPath()),
                ("1", missingRoot),
                ("1", filePath),
                ("1", outsideTemp),
            };

            foreach (var (mode, root) in cases)
            {
                Environment.SetEnvironmentVariable(TestModeVariable, mode);
                Environment.SetEnvironmentVariable(TestRootVariable, root);

                Assert.Throws<InvalidOperationException>(() => WebSurfacePolicy.Create(
                    WebSurfaceKind.Workbench,
                    "C:\\Users\\test\\AppData\\Local"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestModeVariable, null);
            Environment.SetEnvironmentVariable(TestRootVariable, null);
            Directory.Delete(validRoot, recursive: true);
            Directory.Delete(outsideTemp, recursive: true);
        }
    }

    [Fact]
    public void Create_FailsClosedForAReparsePointTestRoot()
    {
        var target = CreateTemporaryDirectory();
        var link = Path.Combine(
            Path.GetTempPath(),
            $"AgentDesk-WebView2-link-{Guid.NewGuid():N}");

        try
        {
            CreateDirectoryJunction(link, target);
            Environment.SetEnvironmentVariable(TestModeVariable, "1");
            Environment.SetEnvironmentVariable(TestRootVariable, link);

            Assert.Throws<InvalidOperationException>(() => WebSurfacePolicy.Create(
                WebSurfaceKind.Workbench,
                "C:\\Users\\test\\AppData\\Local"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestModeVariable, null);
            Environment.SetEnvironmentVariable(TestRootVariable, null);
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void MainWindow_DeclaresAnIndependentInspectorWebView()
    {
        var inspectorWebView = typeof(MainWindow).GetField(
            "InspectorWebView",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(inspectorWebView);
        Assert.Equal("WebView2", inspectorWebView.FieldType.Name);
    }

    [Fact]
    public void MainWindow_ErrorPanelDeclaresTheReloadControlAndHandler()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(FindMainWindowXaml());
        var reloadButton = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "Button" &&
                (string?)element.Attribute(xaml + "Name") == "ReloadButton");

        Assert.Equal("ReloadButton_Click", (string?)reloadButton.Attribute("Click"));
        Assert.Contains(reloadButton.Ancestors(), element =>
            (string?)element.Attribute(xaml + "Name") == "ErrorPanel");

        var reloadHandler = typeof(MainWindow).GetMethod(
            "ReloadButton_Click",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(reloadHandler);
        Assert.Equal(typeof(void), reloadHandler.ReturnType);
        Assert.Equal(
            [typeof(object), typeof(RoutedEventArgs)],
            reloadHandler.GetParameters().Select(parameter => parameter.ParameterType));

        var initializer = typeof(MainWindow).GetMethod(
            "InitializeWorkbenchAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(initializer);
        Assert.Equal(typeof(Task), initializer.ReturnType);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TestModeVariable, _previousTestMode);
        Environment.SetEnvironmentVariable(TestRootVariable, _previousTestRoot);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"AgentDesk-WebView2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindMainWindowXaml()
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
                "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Unable to locate MainWindow.xaml for the source contract test.");
    }

    private static void CreateDirectoryJunction(string link, string target)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start mklink.");
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"mklink failed: {process.StandardError.ReadToEnd()}");
    }
}
