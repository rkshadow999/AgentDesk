using System.Net;
using System.Security.Cryptography;
using AgentDesk.Updater.Core;

namespace AgentDesk.Updater.Core.Tests;

public sealed class SecureUpdateDownloaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"AgentDesk.Downloader.Tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task DownloadBytesFollowsOnlyTrustedHttpsRedirects()
    {
        var handler = new SequenceHttpHandler(
            Redirect("https://release-assets.githubusercontent.com/object?token=opaque"),
            Bytes("manifest"));
        var downloader = new SecureUpdateDownloader(handler, UpdateOriginPolicy.GitHub);

        var bytes = await downloader.DownloadBytesAsync(
            new Uri("https://github.com/example/manifest.json"),
            maximumBytes: 64);

        Assert.Equal("manifest", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/object")]
    [InlineData("https://evil.example/object")]
    public async Task DownloadBytesRejectsUnsafeRedirects(string location)
    {
        var downloader = new SecureUpdateDownloader(
            new SequenceHttpHandler(Redirect(location)),
            UpdateOriginPolicy.GitHub);

        await Assert.ThrowsAsync<UpdateSecurityException>(() => downloader.DownloadBytesAsync(
            new Uri("https://github.com/example/manifest.json"),
            maximumBytes: 64));
    }

    [Fact]
    public async Task DownloadBytesRejectsDeclaredAndStreamedOversizeResponses()
    {
        var declared = Bytes("small");
        declared.Content.Headers.ContentLength = 65;
        var declaredDownloader = new SecureUpdateDownloader(
            new SequenceHttpHandler(declared),
            UpdateOriginPolicy.GitHub);
        await Assert.ThrowsAsync<UpdateSecurityException>(() => declaredDownloader.DownloadBytesAsync(
            new Uri("https://github.com/example/manifest.json"),
            maximumBytes: 64));

        var streamedDownloader = new SecureUpdateDownloader(
            new SequenceHttpHandler(Bytes(new byte[65], includeLength: false)),
            UpdateOriginPolicy.GitHub);
        await Assert.ThrowsAsync<UpdateSecurityException>(() => streamedDownloader.DownloadBytesAsync(
            new Uri("https://github.com/example/manifest.json"),
            maximumBytes: 64));
    }

    [Fact]
    public async Task DownloadFileAtomicallyPublishesOnlyAnExactSizeAndHashMatch()
    {
        var payload = "verified package"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var destination = Path.Combine(_directory, "package.zip");
        var downloader = new SecureUpdateDownloader(
            new SequenceHttpHandler(Bytes(payload)),
            UpdateOriginPolicy.GitHub);

        await downloader.DownloadFileAsync(
            new Uri("https://github.com/example/package.zip"),
            destination,
            payload.Length,
            hash);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.GetFiles(_directory, "*.partial-*"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadFileDeletesPartialDataOnHashOrSizeMismatch(bool wrongHash)
    {
        var payload = "untrusted package"u8.ToArray();
        var destination = Path.Combine(_directory, "package.zip");
        var hash = wrongHash
            ? new string('0', 64)
            : Convert.ToHexStringLower(SHA256.HashData(payload));
        var expectedSize = wrongHash ? payload.Length : payload.Length + 1;
        var downloader = new SecureUpdateDownloader(
            new SequenceHttpHandler(Bytes(payload)),
            UpdateOriginPolicy.GitHub);

        await Assert.ThrowsAsync<UpdateSecurityException>(() => downloader.DownloadFileAsync(
            new Uri("https://github.com/example/package.zip"),
            destination,
            expectedSize,
            hash));

        Assert.False(File.Exists(destination));
        Assert.False(Directory.Exists(_directory) && Directory.EnumerateFiles(_directory).Any());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(location) },
    };

    private static HttpResponseMessage Bytes(string value) => Bytes(
        System.Text.Encoding.UTF8.GetBytes(value));

    private static HttpResponseMessage Bytes(byte[] value, bool includeLength = true)
    {
        var content = new ByteArrayContent(value);
        if (!includeLength)
        {
            content.Headers.ContentLength = null;
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class SequenceHttpHandler(params HttpResponseMessage[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No fake response was configured.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }
}
