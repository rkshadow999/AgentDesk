using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgentDesk.Updater.Core;

namespace AgentDesk.Updater.Core.Tests;

public sealed class PortableUpdateServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"AgentDesk.Service.Tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task CheckAndStageVerifiesSelectsDownloadsAndExtractsBeforeReturning()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePackage(("AgentDesk.exe", "new application"));
        var manifest = CreateManifest(package, "2.0.0");
        var handler = new SequenceHttpHandler(
            Response(manifest),
            Response(ManifestVerifierTests.Sign(key, manifest)),
            Response(package));
        using var downloader = new SecureUpdateDownloader(handler, UpdateOriginPolicy.GitHub);
        var service = new PortableUpdateService(downloader, new SafeZipExtractor());
        var state = Path.Combine(_directory, "state");

        var staged = await service.CheckAndStageAsync(new UpdateCheckRequest(
            new Uri("https://github.com/example/update-manifest.json"),
            new Uri("https://github.com/example/update-manifest.json.sig"),
            key.ExportSubjectPublicKeyInfo(),
            SemanticVersion.Parse("1.0.0"),
            UpdateArchitecture.X64,
            AllowPrerelease: false,
            state));

        Assert.NotNull(staged);
        Assert.Equal("new application", await File.ReadAllTextAsync(
            Path.Combine(staged.PayloadDirectory, "AgentDesk.exe")));
        Assert.Equal(SemanticVersion.Parse("2.0.0"),
            await new HighestSeenVersionStore(Path.Combine(state, "highest-seen.json")).GetAsync());
    }

    [Fact]
    public async Task CheckAndStageReturnsNullWithoutDownloadingWhenNoUpdateIsAvailable()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePackage(("AgentDesk.exe", "same application"));
        var manifest = CreateManifest(package, "1.0.0");
        var handler = new SequenceHttpHandler(
            Response(manifest),
            Response(ManifestVerifierTests.Sign(key, manifest)));
        using var downloader = new SecureUpdateDownloader(handler, UpdateOriginPolicy.GitHub);
        var service = new PortableUpdateService(downloader, new SafeZipExtractor());

        var staged = await service.CheckAndStageAsync(new UpdateCheckRequest(
            new Uri("https://github.com/example/update-manifest.json"),
            new Uri("https://github.com/example/update-manifest.json.sig"),
            key.ExportSubjectPublicKeyInfo(),
            SemanticVersion.Parse("1.0.0"),
            UpdateArchitecture.X64,
            AllowPrerelease: false,
            Path.Combine(_directory, "state")));

        Assert.Null(staged);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task CheckAndStageRemovesTransactionDataWhenPackageIntegrityFails()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signedPackage = CreatePackage(("AgentDesk.exe", "signed"));
        var servedPackage = CreatePackage(("AgentDesk.exe", "tampered"));
        var manifest = CreateManifest(signedPackage, "2.0.0");
        var handler = new SequenceHttpHandler(
            Response(manifest),
            Response(ManifestVerifierTests.Sign(key, manifest)),
            Response(servedPackage));
        using var downloader = new SecureUpdateDownloader(handler, UpdateOriginPolicy.GitHub);
        var state = Path.Combine(_directory, "state");

        await Assert.ThrowsAsync<UpdateSecurityException>(() =>
            new PortableUpdateService(downloader, new SafeZipExtractor()).CheckAndStageAsync(
                new UpdateCheckRequest(
                    new Uri("https://github.com/example/update-manifest.json"),
                    new Uri("https://github.com/example/update-manifest.json.sig"),
                    key.ExportSubjectPublicKeyInfo(),
                    SemanticVersion.Parse("1.0.0"),
                    UpdateArchitecture.X64,
                    AllowPrerelease: false,
                    state)));

        var staging = Path.Combine(state, "staging");
        Assert.False(Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any());
    }

    [Fact]
    public async Task CheckAndStageUsesTheConfiguredTrustedHttpsOriginForManifestAssets()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePackage(("AgentDesk.exe", "enterprise release"));
        var manifest = CreateManifest(package, "2.0.0", "updates.example.com");
        var policy = new UpdateOriginPolicy(["updates.example.com"]);
        var handler = new SequenceHttpHandler(
            Response(manifest),
            Response(ManifestVerifierTests.Sign(key, manifest)),
            Response(package));
        using var downloader = new SecureUpdateDownloader(handler, policy);

        var staged = await new PortableUpdateService(downloader, new SafeZipExtractor())
            .CheckAndStageAsync(new UpdateCheckRequest(
                new Uri("https://updates.example.com/manifest.json"),
                new Uri("https://updates.example.com/manifest.json.sig"),
                key.ExportSubjectPublicKeyInfo(),
                SemanticVersion.Parse("1.0.0"),
                UpdateArchitecture.X64,
                AllowPrerelease: false,
                Path.Combine(_directory, "enterprise-state")));

        Assert.NotNull(staged);
    }

    [Fact]
    public async Task CheckAndStageUsesTheExplicitExpectedProduct()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePackage(("AgentDesk.Updater.exe", "trusted updater"));
        var manifest = CreateManifest(
            package,
            "2.0.0",
            product: "AgentDesk.Updater",
            entryPoint: "AgentDesk.Updater.exe");
        var handler = new SequenceHttpHandler(
            Response(manifest),
            Response(ManifestVerifierTests.Sign(key, manifest)),
            Response(package));
        using var downloader = new SecureUpdateDownloader(handler, UpdateOriginPolicy.GitHub);

        var staged = await new PortableUpdateService(downloader, new SafeZipExtractor())
            .CheckAndStageAsync(new UpdateCheckRequest(
                new Uri("https://github.com/example/updater-manifest.json"),
                new Uri("https://github.com/example/updater-manifest.json.sig"),
                key.ExportSubjectPublicKeyInfo(),
                SemanticVersion.Parse("1.0.0"),
                UpdateArchitecture.X64,
                AllowPrerelease: false,
                Path.Combine(_directory, "updater-state"),
                ExpectedProduct: "AgentDesk.Updater"));

        Assert.NotNull(staged);
        Assert.Equal("AgentDesk.Updater", staged.Manifest.Product);
    }

    private static byte[] CreatePackage(params (string Name, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
                writer.Write(content);
            }
        }

        return output.ToArray();
    }

    private static byte[] CreateManifest(
        byte[] package,
        string version,
        string host = "github.com",
        string product = "AgentDesk",
        string entryPoint = "AgentDesk.exe")
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(package));
        return Encoding.UTF8.GetBytes($$"""
            {
              "schemaVersion":1,
              "product":"{{product}}",
              "version":"{{version}}",
              "assets":[{
                "architecture":"x64",
                "url":"https://{{host}}/example/AgentDesk.zip",
                "sha256":"{{hash}}",
                "size":{{package.Length}},
                "entryPoint":"{{entryPoint}}"
              }]
            }
            """);
    }

    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class SequenceHttpHandler(params HttpResponseMessage[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
