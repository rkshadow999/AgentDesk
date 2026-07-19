using AgentDesk.Updater.Core;

namespace AgentDesk.Updater.Core.Tests;

public sealed class UpdatePolicyTests
{
    [Fact]
    public void SelectReturnsTheExactArchitectureAssetForANewerStableVersion()
    {
        var manifest = CreateManifest("2.0.0", UpdateArchitecture.X64, UpdateArchitecture.Arm64);

        var selected = UpdateSelector.Select(
            manifest,
            SemanticVersion.Parse("1.0.0"),
            highestSeen: SemanticVersion.Parse("1.5.0"),
            UpdateArchitecture.Arm64,
            allowPrerelease: false);

        Assert.NotNull(selected);
        Assert.Equal(UpdateArchitecture.Arm64, selected.Architecture);
    }

    [Fact]
    public void SelectReturnsNullWhenTheManifestIsNotNewerThanTheInstalledVersion()
    {
        Assert.Null(UpdateSelector.Select(
            CreateManifest("1.0.0", UpdateArchitecture.X64),
            SemanticVersion.Parse("1.0.0"),
            highestSeen: null,
            UpdateArchitecture.X64,
            allowPrerelease: false));
    }

    [Fact]
    public void SelectRejectsPrereleaseByDefaultAndAllowsItOnlyWhenExplicitlyEnabled()
    {
        var manifest = CreateManifest("2.0.0-beta.1", UpdateArchitecture.X64);

        Assert.Null(UpdateSelector.Select(
            manifest,
            SemanticVersion.Parse("1.0.0"),
            highestSeen: null,
            UpdateArchitecture.X64,
            allowPrerelease: false));
        Assert.NotNull(UpdateSelector.Select(
            manifest,
            SemanticVersion.Parse("1.0.0"),
            highestSeen: null,
            UpdateArchitecture.X64,
            allowPrerelease: true));
    }

    [Theory]
    [InlineData("2.0.0-beta.2")]
    [InlineData("2.0.0")]
    public void SelectAllowsPrereleaseInstallationsToAdvanceWithinTheirChannel(
        string availableVersion)
    {
        var selected = UpdateSelector.Select(
            CreateManifest(availableVersion, UpdateArchitecture.X64),
            SemanticVersion.Parse("2.0.0-alpha.1"),
            highestSeen: null,
            UpdateArchitecture.X64,
            allowPrerelease: true);

        Assert.NotNull(selected);
    }

    [Theory]
    [InlineData("2.0.0-alpha.0")]
    [InlineData("2.0.0-beta.0")]
    public void SelectAllowsCiInstallationsToAdvanceAcrossReleaseChannels(
        string availableVersion)
    {
        var selected = UpdateSelector.Select(
            CreateManifest(availableVersion, UpdateArchitecture.X64),
            SemanticVersion.Parse("2.0.0-ci.9999"),
            highestSeen: SemanticVersion.Parse("2.0.0-ci.9999"),
            UpdateArchitecture.X64,
            allowPrerelease: true);

        Assert.NotNull(selected);
    }

    [Fact]
    public void SelectFailsClosedWhenManifestWouldRollbackTheHighestSeenVersion()
    {
        var manifest = CreateManifest("2.0.0", UpdateArchitecture.X64);

        Assert.Throws<UpdateSecurityException>(() => UpdateSelector.Select(
            manifest,
            SemanticVersion.Parse("1.0.0"),
            highestSeen: SemanticVersion.Parse("3.0.0"),
            UpdateArchitecture.X64,
            allowPrerelease: false));
    }

    [Fact]
    public void SelectFailsClosedWhenCiWouldRollbackAnAlphaHighestSeenVersion()
    {
        Assert.Throws<UpdateSecurityException>(() => UpdateSelector.Select(
            CreateManifest("2.0.0-ci.9999", UpdateArchitecture.X64),
            SemanticVersion.Parse("1.0.0"),
            highestSeen: SemanticVersion.Parse("2.0.0-alpha.0"),
            UpdateArchitecture.X64,
            allowPrerelease: true));
    }

    [Fact]
    public void SelectFailsClosedWhenTheRequiredArchitectureIsMissing()
    {
        Assert.Throws<UpdateSecurityException>(() => UpdateSelector.Select(
            CreateManifest("2.0.0", UpdateArchitecture.X64),
            SemanticVersion.Parse("1.0.0"),
            highestSeen: null,
            UpdateArchitecture.Arm64,
            allowPrerelease: false));
    }

    private static UpdateManifest CreateManifest(
        string version,
        params UpdateArchitecture[] architectures) => new(
            1,
            "AgentDesk",
            SemanticVersion.Parse(version),
            architectures.Select(architecture => new UpdateAsset(
                architecture,
                new Uri($"https://github.com/example/AgentDesk-{architecture}.zip"),
                new string('0', 64),
                100,
                "AgentDesk.exe")).ToArray());
}
