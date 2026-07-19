using AgentDesk.App.Maintenance;
using AgentDesk.Updater.Core;
using System.Security.Cryptography;

namespace AgentDesk.App.Tests;

public sealed class AgentDeskUpdateCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"agentdesk-update-coordinator-{Guid.NewGuid():N}");

    [Fact]
    public async Task CheckAsync_StagesOnlyTheExplicitUpdaterProduct()
    {
        var fixture = CreateFixture();

        var availability = await fixture.Coordinator.CheckAsync();

        Assert.NotNull(availability);
        Assert.Equal(SemanticVersion.Parse("2.0.0"), availability.Version);
        Assert.NotNull(fixture.Stager.LastRequest);
        Assert.Equal("AgentDesk.Updater", fixture.Stager.LastRequest.ExpectedProduct);
        Assert.Equal(fixture.Options.UpdaterManifestUri, fixture.Stager.LastRequest.ManifestUri);
        Assert.Equal(fixture.Options.UpdaterSignatureUri, fixture.Stager.LastRequest.SignatureUri);
        Assert.Equal(fixture.Options.InstalledVersion, fixture.Stager.LastRequest.InstalledVersion);
    }

    [Fact]
    public async Task CheckAsync_ReturnsNullWhenTheStagerFindsNoUpdate()
    {
        var fixture = CreateFixture(noUpdate: true);

        Assert.Null(await fixture.Coordinator.CheckAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.LaunchAsync(parentProcessId: 42));
    }

    [Fact]
    public async Task LaunchAsync_WritesThePinnedKeyAndUsesArgumentListWithoutSecrets()
    {
        var fixture = CreateFixture();
        _ = await fixture.Coordinator.CheckAsync();

        await fixture.Coordinator.LaunchAsync(parentProcessId: 42);

        var start = Assert.Single(fixture.Launcher.Starts);
        Assert.Equal(
            Path.Combine(fixture.Staged.PayloadDirectory, "AgentDesk.Updater.exe"),
            start.ExecutablePath);
        Assert.Equal(fixture.Staged.PayloadDirectory, start.WorkingDirectory);
        Assert.Equal("apply", start.Arguments[0]);
        AssertArgument(start.Arguments, "--manifest-uri", fixture.Options.ApplicationManifestUri.AbsoluteUri);
        AssertArgument(start.Arguments, "--signature-uri", fixture.Options.ApplicationSignatureUri.AbsoluteUri);
        AssertArgument(start.Arguments, "--current-version", "1.0.0");
        AssertArgument(start.Arguments, "--architecture", "x64");
        AssertArgument(start.Arguments, "--parent-process-id", "42");
        Assert.Contains("--allow-prerelease", start.Arguments);
        AssertArgument(start.Arguments, "--restart-argument", "--workspace");
        Assert.DoesNotContain(
            Convert.ToBase64String(fixture.Options.PublicKeySubjectPublicKeyInfo.Span),
            start.Arguments);

        var keyPath = ValueAfter(start.Arguments, "--public-key-file");
        Assert.Equal(fixture.Options.PublicKeySubjectPublicKeyInfo.ToArray(),
            await File.ReadAllBytesAsync(keyPath));
        Assert.StartsWith(
            Path.GetFullPath(fixture.Options.StateDirectory) + Path.DirectorySeparatorChar,
            Path.GetFullPath(keyPath),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchAsync_IsSingleUseAndRejectsAnInvalidParentProcessId()
    {
        var fixture = CreateFixture();
        _ = await fixture.Coordinator.CheckAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fixture.Coordinator.LaunchAsync(parentProcessId: 0));
        await fixture.Coordinator.LaunchAsync(parentProcessId: 42);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.LaunchAsync(parentProcessId: 42));
    }

    [Fact]
    public void OptionsRejectUntrustedOriginsAndUnsafeInstallationLayouts()
    {
        Directory.CreateDirectory(_directory);
        var state = Path.Combine(_directory, "install", "state");
        var install = Path.Combine(_directory, "install");

        Assert.Throws<UpdateSecurityException>(() => CreateOptions(
            updaterManifestUri: new Uri("http://github.com/manifest.json")));
        Assert.Throws<UpdateSecurityException>(() => CreateOptions(
            stateDirectory: state,
            installationDirectory: install));
    }

    [Fact]
    public void DefaultsLoadTheRepositoryPinnedP256PublicKey()
    {
        var key = AgentDeskUpdateDefaults.LoadPinnedPublicKey();
        try
        {
            Assert.Equal(
                "a7350091fed6493ac0aa0d6222b4f2e0b80eb365c70fcf89d9040276e47b6e15",
                Convert.ToHexStringLower(SHA256.HashData(key)));
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out var bytesRead);
            Assert.Equal(key.Length, bytesRead);
            Assert.Equal(256, ecdsa.KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [Fact]
    public void DefaultsUseTheStableFeedForAStableInstallation()
    {
        var options = AgentDeskUpdateDefaults.Create(
            SemanticVersion.Parse("1.2.3"),
            UpdateArchitecture.Arm64,
            Path.Combine(_directory, "state"),
            Path.Combine(_directory, "install"),
            ["--workspace", "C:\\repo"]);

        Assert.Equal(
            "https://github.com/rkshadow999/AgentDesk/releases/download/update-stable/AgentDesk-updater-manifest.json",
            options.UpdaterManifestUri.AbsoluteUri);
        Assert.Equal(
            "https://github.com/rkshadow999/AgentDesk/releases/download/update-stable/AgentDesk-updater-manifest.json.sig",
            options.UpdaterSignatureUri.AbsoluteUri);
        Assert.Equal(
            "https://github.com/rkshadow999/AgentDesk/releases/download/update-stable/AgentDesk-update-manifest.json",
            options.ApplicationManifestUri.AbsoluteUri);
        Assert.Equal(
            "https://github.com/rkshadow999/AgentDesk/releases/download/update-stable/AgentDesk-update-manifest.json.sig",
            options.ApplicationSignatureUri.AbsoluteUri);
        Assert.Equal(UpdateArchitecture.Arm64, options.Architecture);
        Assert.Equal(SemanticVersion.Parse("1.2.3"), options.InstalledVersion);
        Assert.False(options.AllowPrerelease);
        Assert.Equal(new[] { "--workspace", "C:\\repo" }, options.RestartArguments);
    }

    [Fact]
    public void DefaultsUseThePrereleaseFeedForAPrereleaseInstallation()
    {
        var options = AgentDeskUpdateDefaults.Create(
            SemanticVersion.Parse("1.2.3-alpha.1"),
            UpdateArchitecture.X64,
            Path.Combine(_directory, "state"),
            Path.Combine(_directory, "install"),
            []);

        Assert.Equal(
            "https://github.com/rkshadow999/AgentDesk/releases/download/update-prerelease/AgentDesk-updater-manifest.json",
            options.UpdaterManifestUri.AbsoluteUri);
        Assert.Equal(
            "https://github.com/rkshadow999/AgentDesk/releases/download/update-prerelease/AgentDesk-updater-manifest.json.sig",
            options.UpdaterSignatureUri.AbsoluteUri);
        Assert.Equal(
            "https://github.com/rkshadow999/AgentDesk/releases/download/update-prerelease/AgentDesk-update-manifest.json",
            options.ApplicationManifestUri.AbsoluteUri);
        Assert.Equal(
            "https://github.com/rkshadow999/AgentDesk/releases/download/update-prerelease/AgentDesk-update-manifest.json.sig",
            options.ApplicationSignatureUri.AbsoluteUri);
        Assert.True(options.AllowPrerelease);
    }

    private Fixture CreateFixture(StagedUpdate? staged = null, bool noUpdate = false)
    {
        Directory.CreateDirectory(_directory);
        var options = CreateOptions();
        Directory.CreateDirectory(options.StateDirectory);
        Directory.CreateDirectory(options.InstallationDirectory);
        var payload = Path.Combine(_directory, "staged", "payload");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "AgentDesk.Updater.exe"), "fixture");
        var asset = new UpdateAsset(
            UpdateArchitecture.X64,
            new Uri("https://github.com/rkshadow999/AgentDesk/releases/download/v2.0.0/AgentDesk-updater.exe"),
            new string('a', 64),
            7,
            "AgentDesk.Updater.exe");
        var defaultStaged = new StagedUpdate(
            new UpdateManifest(1, "AgentDesk.Updater", SemanticVersion.Parse("2.0.0"), [asset]),
            asset,
            Path.Combine(_directory, "staged"),
            Path.Combine(_directory, "staged", "package.zip"),
            payload);
        var selected = noUpdate ? null : staged ?? defaultStaged;
        var stager = new FakeStager(selected);
        var launcher = new FakeLauncher();
        return new Fixture(
            options,
            defaultStaged,
            stager,
            launcher,
            new AgentDeskUpdateCoordinator(options, stager, launcher));
    }

    private AgentDeskUpdateOptions CreateOptions(
        Uri? updaterManifestUri = null,
        string? stateDirectory = null,
        string? installationDirectory = null) => new(
        updaterManifestUri ?? new Uri(
            "https://github.com/rkshadow999/AgentDesk/releases/latest/download/AgentDesk-updater-manifest.json"),
        new Uri(
            "https://github.com/rkshadow999/AgentDesk/releases/latest/download/AgentDesk-updater-manifest.json.sig"),
        new Uri(
            "https://github.com/rkshadow999/AgentDesk/releases/latest/download/AgentDesk-update-manifest.json"),
        new Uri(
            "https://github.com/rkshadow999/AgentDesk/releases/latest/download/AgentDesk-update-manifest.json.sig"),
        new byte[]
        {
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        },
        SemanticVersion.Parse("1.0.0"),
        UpdateArchitecture.X64,
        stateDirectory ?? Path.Combine(_directory, "state"),
        installationDirectory ?? Path.Combine(_directory, "install"),
        allowPrerelease: true,
        restartArguments: ["--workspace", "C:\\repo"]);

    private static void AssertArgument(
        IReadOnlyList<string> arguments,
        string option,
        string expected) => Assert.Equal(expected, ValueAfter(arguments, option));

    private static string ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.IndexOf(option);
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed record Fixture(
        AgentDeskUpdateOptions Options,
        StagedUpdate Staged,
        FakeStager Stager,
        FakeLauncher Launcher,
        AgentDeskUpdateCoordinator Coordinator);

    private sealed class FakeStager(StagedUpdate? result) : IAgentDeskUpdateStager
    {
        public UpdateCheckRequest? LastRequest { get; private set; }

        public Task<StagedUpdate?> CheckAndStageAsync(
            UpdateCheckRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeLauncher : IAgentDeskUpdateProcessLauncher
    {
        public List<AgentDeskUpdateProcessStart> Starts { get; } = [];

        public void Start(AgentDeskUpdateProcessStart start) => Starts.Add(start);
    }
}

internal static class ReadOnlyListTestExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> items, T value)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(items[index], value))
            {
                return index;
            }
        }
        return -1;
    }
}
