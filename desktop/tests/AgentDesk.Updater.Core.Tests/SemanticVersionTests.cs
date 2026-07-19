using AgentDesk.Updater.Core;

namespace AgentDesk.Updater.Core.Tests;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("0.0.0")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3-alpha.1")]
    [InlineData("1.2.3-alpha.1+build.42")]
    public void ParseAcceptsStrictSemver2(string value)
    {
        Assert.Equal(value, SemanticVersion.Parse(value).ToString());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+bad space")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3.4")]
    public void ParseRejectsNonCanonicalOrInvalidVersions(string value)
    {
        Assert.Throws<FormatException>(() => SemanticVersion.Parse(value));
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.9.9", "2.0.0")]
    public void ComparisonFollowsSemverPrecedence(string lower, string higher)
    {
        Assert.True(SemanticVersion.Parse(lower) < SemanticVersion.Parse(higher));
    }

    [Theory]
    [InlineData("1.0.0-ci.9999", "1.0.0-alpha.0")]
    [InlineData("1.0.0-alpha.9999", "1.0.0-beta.0")]
    [InlineData("1.0.0-beta.9999", "1.0.0-preview.0")]
    [InlineData("1.0.0-preview.9999", "1.0.0-rc.0")]
    [InlineData("1.0.0-rc.9999", "1.0.0")]
    public void AgentDeskReleaseComparisonFollowsPackageChannelOrder(
        string lower,
        string higher)
    {
        Assert.True(
            SemanticVersion.Parse(lower).CompareAgentDeskReleaseTo(
                SemanticVersion.Parse(higher)) < 0);
    }

    [Fact]
    public void StandardAndAgentDeskReleaseComparisonRemainExplicitlyDistinct()
    {
        var ci = SemanticVersion.Parse("1.0.0-ci.9999");
        var alpha = SemanticVersion.Parse("1.0.0-alpha.0");

        Assert.True(alpha < ci);
        Assert.True(ci.CompareAgentDeskReleaseTo(alpha) < 0);
    }

    [Fact]
    public void AgentDeskReleaseComparisonIsTransitiveForOtherPrereleases()
    {
        var ci = SemanticVersion.Parse("1.0.0-ci.9999");
        var alpha = SemanticVersion.Parse("1.0.0-alpha.0");
        var other = SemanticVersion.Parse("1.0.0-bravo.0");
        var stable = SemanticVersion.Parse("1.0.0");

        Assert.True(ci.CompareAgentDeskReleaseTo(alpha) < 0);
        Assert.True(alpha.CompareAgentDeskReleaseTo(other) < 0);
        Assert.True(ci.CompareAgentDeskReleaseTo(other) < 0);
        Assert.True(other.CompareAgentDeskReleaseTo(stable) < 0);
    }

    [Fact]
    public void BuildMetadataDoesNotAffectPrecedence()
    {
        Assert.Equal(
            SemanticVersion.Parse("1.2.3+first"),
            SemanticVersion.Parse("1.2.3+second"));
    }
}
