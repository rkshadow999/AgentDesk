using AgentDesk.Updater.Core;

namespace AgentDesk.App.Tests;

public sealed class AgentDeskApplicationVersionTests
{
    [Theory]
    [InlineData("0.2.0-alpha.1", "0.2.0.0", "0.2.0-alpha.1")]
    [InlineData("0.2.0-rc.2+build.7", "0.2.0.0", "0.2.0-rc.2+build.7")]
    public void MainWindowVersionParserPreservesTheInformationalSemanticVersion(
        string informationalVersion,
        string assemblyVersion,
        string expected)
    {
        var resolved = MainWindow.ResolveSemanticVersion(
            informationalVersion,
            new Version(assemblyVersion));

        Assert.Equal(expected, resolved.ToString());
        Assert.True(resolved.IsPrerelease);
    }

    [Fact]
    public void MainWindowVersionParserFallsBackToTheAssemblyVersionWhenMetadataIsMissing()
    {
        var resolved = MainWindow.ResolveSemanticVersion(
            informationalVersion: null,
            new Version(1, 2, 3, 4));

        Assert.Equal(SemanticVersion.Parse("1.2.3"), resolved);
    }
}
