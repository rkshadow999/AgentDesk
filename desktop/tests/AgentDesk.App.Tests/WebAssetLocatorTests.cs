namespace AgentDesk.App.Tests;

public sealed class WebAssetLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AgentDesk.App.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void FindIndexPath_PrefersExplicitWebRoot()
    {
        var appBase = CreateFile("app", "web", "index.html");
        var explicitIndex = CreateFile("override", "index.html");

        var result = WebAssetLocator.FindIndexPath(
            Path.GetDirectoryName(Path.GetDirectoryName(appBase)!)!,
            Path.GetDirectoryName(explicitIndex));

        Assert.Equal(Path.GetFullPath(explicitIndex), result);
    }

    [Fact]
    public void FindIndexPath_FindsCopiedOutputAssets()
    {
        var index = CreateFile("app", "web", "index.html");

        var result = WebAssetLocator.FindIndexPath(
            Path.GetDirectoryName(Path.GetDirectoryName(index)!)!);

        Assert.Equal(Path.GetFullPath(index), result);
    }

    [Fact]
    public void FindIndexPath_DoesNotSearchParentDirectoriesByDefault()
    {
        _ = CreateFile("repo", "desktop", "web", "dist", "index.html");
        var appBase = Path.Combine(
            _root, "repo", "desktop", "src", "AgentDesk.App", "bin", "x64", "Debug");
        Directory.CreateDirectory(appBase);

        var result = WebAssetLocator.FindIndexPath(appBase);

        Assert.Null(result);
    }

    [Fact]
    public void FindIndexPath_FindsDesktopSourceAssetsWhenDevelopmentFallbacksAreEnabled()
    {
        var index = CreateFile("repo", "desktop", "web", "dist", "index.html");
        var appBase = Path.Combine(
            _root, "repo", "desktop", "src", "AgentDesk.App", "bin", "x64", "Debug");
        Directory.CreateDirectory(appBase);

        var result = WebAssetLocator.FindIndexPath(
            appBase,
            allowDevelopmentFallbacks: true);

        Assert.Equal(Path.GetFullPath(index), result);
    }

    [Fact]
    public void FindIndexPath_ReturnsNullAndProvidesChineseDiagnosticWhenMissing()
    {
        Directory.CreateDirectory(_root);

        var result = WebAssetLocator.FindIndexPath(_root);

        Assert.Null(result);
        Assert.Contains("desktop/web/dist/index.html", WebAssetLocator.MissingAssetsMessage);
        Assert.Contains("未找到", WebAssetLocator.MissingAssetsMessage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateFile(params string[] segments)
    {
        var path = segments.Aggregate(_root, Path.Combine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<!doctype html>");
        return path;
    }
}
