namespace AgentDesk.App;

public enum WebSurfaceKind
{
    Workbench,
    Inspector,
}

public sealed record WebSurfaceDefinition(
    string VirtualHostName,
    Uri Source,
    string UserDataFolder,
    string ProfileName);

public static class WebSurfacePolicy
{
    private const string TestModeEnvironmentVariable =
        "AGENTDESK_WEBVIEW2_TEST_MODE";
    private const string TestRootEnvironmentVariable =
        "AGENTDESK_WEBVIEW2_TEST_USER_DATA_ROOT";

    public static bool IsSurfaceProfile(
        WebSurfaceDefinition surface,
        string? profileName)
    {
        ArgumentNullException.ThrowIfNull(surface);

        return !string.IsNullOrWhiteSpace(profileName) &&
            profileName.Equals(
                surface.ProfileName,
                StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedSource(WebSurfaceDefinition surface, string? source)
    {
        ArgumentNullException.ThrowIfNull(surface);

        return Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            uri.Host.Equals(surface.VirtualHostName, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == 443 &&
            string.IsNullOrEmpty(uri.UserInfo);
    }

    public static WebSurfaceDefinition Create(
        WebSurfaceKind kind,
        string localAppDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataDirectory);

        var name = kind switch
        {
            WebSurfaceKind.Workbench => "workbench",
            WebSurfaceKind.Inspector => "inspector",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var virtualHostName = $"{name}.agentdesk.local";
        var userDataFolder = ResolveUserDataFolder(localAppDataDirectory);
        return new(
            virtualHostName,
            new Uri($"https://{virtualHostName}/index.html?surface={name}"),
            userDataFolder,
            name);
    }

    private static string ResolveUserDataFolder(string localAppDataDirectory)
    {
        var testMode = Environment.GetEnvironmentVariable(
            TestModeEnvironmentVariable);
        if (testMode is null)
        {
            return Path.GetFullPath(
                Path.Combine(localAppDataDirectory, "AgentDesk", "WebView2"));
        }

        if (!testMode.Equals("1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The WebView2 test mode configuration is invalid.");
        }

        var configuredRoot = Environment.GetEnvironmentVariable(
            TestRootEnvironmentVariable);
        return ValidateTestUserDataRoot(configuredRoot);
    }

    private static string ValidateTestUserDataRoot(string? configuredRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot) ||
            !Path.IsPathFullyQualified(configuredRoot))
        {
            throw new InvalidOperationException(
                "The WebView2 test user-data root is invalid.");
        }

        try
        {
            var testRoot = Path.GetFullPath(configuredRoot);
            var temporaryDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath()));
            var relativePath = Path.GetRelativePath(temporaryDirectory, testRoot);
            if (relativePath.Equals(".", StringComparison.Ordinal) ||
                relativePath.Equals("..", StringComparison.Ordinal) ||
                relativePath.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) ||
                Path.IsPathFullyQualified(relativePath) ||
                !Directory.Exists(testRoot))
            {
                throw new InvalidOperationException(
                    "The WebView2 test user-data root is invalid.");
            }

            // This is a configuration guard for ordinary reparse paths, not a
            // same-account TOCTOU boundary. The CDP harness owns isolation by
            // creating a unique temporary root and managing the child lifecycle.
            EnsurePathIsNotReparsePoint(testRoot, temporaryDirectory);
            return testRoot;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            throw new InvalidOperationException(
                "The WebView2 test user-data root is invalid.",
                exception);
        }
    }

    private static void EnsurePathIsNotReparsePoint(
        string testRoot,
        string temporaryDirectory)
    {
        var current = new DirectoryInfo(testRoot);
        while (true)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "The WebView2 test user-data root is invalid.");
            }

            if (Path.GetFullPath(current.FullName).Equals(
                temporaryDirectory,
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            current = current.Parent ?? throw new InvalidOperationException(
                "The WebView2 test user-data root is invalid.");
        }
    }
}
