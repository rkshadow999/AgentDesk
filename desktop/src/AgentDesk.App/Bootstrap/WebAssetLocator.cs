namespace AgentDesk.App;

public static class WebAssetLocator
{
    public const string MissingAssetsMessage =
        "未找到桌面界面资源。请先在 desktop/web 运行 npm run build，确认 desktop/web/dist/index.html 已生成。";

    public static string? FindIndexPath(
        string appBaseDirectory,
        string? explicitWebRoot = null,
        bool allowDevelopmentFallbacks = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);

        if (!string.IsNullOrWhiteSpace(explicitWebRoot))
        {
            return ExistingIndex(Path.GetFullPath(explicitWebRoot));
        }

        var appBase = Path.GetFullPath(appBaseDirectory);
        var packagedCandidates = new[]
        {
            Path.Combine(appBase, "web"),
            Path.Combine(appBase, "web", "dist"),
        };
        foreach (var candidate in packagedCandidates)
        {
            var indexPath = ExistingIndex(candidate);
            if (indexPath is not null)
            {
                return indexPath;
            }
        }

        if (!allowDevelopmentFallbacks)
        {
            return null;
        }

        var current = new DirectoryInfo(appBase);
        while (current is not null)
        {
            var indexPath = ExistingIndex(
                Path.Combine(current.FullName, "desktop", "web", "dist"));
            if (indexPath is not null)
            {
                return indexPath;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? ExistingIndex(string directory)
    {
        var indexPath = Path.Combine(directory, "index.html");
        return File.Exists(indexPath) ? Path.GetFullPath(indexPath) : null;
    }
}
