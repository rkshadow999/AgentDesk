namespace AgentDesk.App.Tests;

public sealed class AppLaunchOptionsTests
{
    [Fact]
    public void Parse_NormalizesSupportedPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "AgentDesk Tests", "web");
        var workspace = Path.Combine(Path.GetTempPath(), "AgentDesk Tests", "workspace");

        var options = AppLaunchOptions.Parse(
            ["--web-root", root, "--workspace", workspace],
            allowExternalWebRoot: true);

        Assert.Equal(Path.GetFullPath(root), options.WebRoot);
        Assert.Equal(Path.GetFullPath(workspace), options.WorkspacePath);
    }

    [Fact]
    public void Parse_RejectsExternalWebRootUnlessExplicitlyAllowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "AgentDesk Tests", "web");

        var error = Assert.Throws<ArgumentException>(
            () => AppLaunchOptions.Parse(["--web-root", root]));

        Assert.Contains("--web-root", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsMissingOptionValueWithChineseMessage()
    {
        var error = Assert.Throws<ArgumentException>(
            () => AppLaunchOptions.Parse(["--workspace"]));

        Assert.Contains("缺少路径", error.Message, StringComparison.Ordinal);
    }
}
