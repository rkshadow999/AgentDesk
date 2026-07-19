using System.Buffers;
using System.Net;
using System.Security.Cryptography;

namespace AgentDesk.Updater.Core;

public sealed class SecureUpdateDownloader : IDisposable
{
    private const int MaximumRedirects = 5;
    private readonly HttpClient _client;
    private readonly UpdateOriginPolicy _originPolicy;

    public SecureUpdateDownloader(UpdateOriginPolicy originPolicy)
        : this(CreateProductionHandler(), originPolicy)
    {
    }

    internal SecureUpdateDownloader(
        HttpMessageHandler handler,
        UpdateOriginPolicy originPolicy)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _originPolicy = originPolicy ?? throw new ArgumentNullException(nameof(originPolicy));
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("AgentDesk-Updater/1");
    }

    internal UpdateOriginPolicy OriginPolicy => _originPolicy;

    public async Task<byte[]> DownloadBytesAsync(
        Uri uri,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        if (maximumBytes is <= 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        using var response = await SendAsync(uri, maximumBytes, cancellationToken).ConfigureAwait(false);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream((int)Math.Min(maximumBytes, 64 * 1024));
        await CopyBoundedAsync(input, output, maximumBytes, hash: null, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    public async Task DownloadFileAsync(
        Uri uri,
        string destinationPath,
        long expectedSize,
        string expectedSha256Hex,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (expectedSize is <= 0 or > UpdateManifestVerifier.MaximumPackageBytes ||
            expectedSha256Hex is not { Length: 64 } ||
            !expectedSha256Hex.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("The expected package integrity metadata is invalid.");
        }

        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullDestinationPath)!;
        Directory.CreateDirectory(directory);
        UpdatePathSafety.EnsureNoReparsePoints(directory);
        if (File.Exists(fullDestinationPath))
        {
            throw new IOException("The update package destination already exists.");
        }

        var temporaryPath = $"{fullDestinationPath}.partial-{Guid.NewGuid():N}";
        try
        {
            using var response = await SendAsync(uri, expectedSize, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != expectedSize)
            {
                throw new UpdateSecurityException("The update package size does not match its manifest.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var copied = await CopyBoundedAsync(
                input,
                output,
                expectedSize,
                hash,
                cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);

            var actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (copied != expectedSize ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(expectedSha256Hex)))
            {
                throw new UpdateSecurityException("The update package failed its integrity check.");
            }

            output.Close();
            File.Move(temporaryPath, fullDestinationPath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public void Dispose() => _client.Dispose();

    private async Task<HttpResponseMessage> SendAsync(
        Uri initialUri,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        _originPolicy.EnsureAllowedInitialUri(initialUri);
        var currentUri = initialUri;
        for (var redirects = 0; redirects <= MaximumRedirects; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirects == MaximumRedirects)
                {
                    throw new UpdateSecurityException("The update server returned an invalid redirect chain.");
                }

                currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                _originPolicy.EnsureAllowedRedirectUri(currentUri);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException(
                    "The update server returned an unsuccessful response.",
                    inner: null,
                    statusCode);
            }

            if (response.Content.Headers.ContentEncoding.Count > 0 ||
                response.Content.Headers.ContentLength is { } length && length > maximumBytes)
            {
                response.Dispose();
                throw new UpdateSecurityException("The update response exceeds its permitted size.");
            }

            return response;
        }

        throw new UpdateSecurityException("The update server returned an invalid redirect chain.");
    }

    private static async Task<long> CopyBoundedAsync(
        Stream input,
        Stream output,
        long maximumBytes,
        IncrementalHash? hash,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return total;
                }

                total = checked(total + read);
                if (total > maximumBytes)
                {
                    throw new UpdateSecurityException("The update response exceeds its permitted size.");
                }

                hash?.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.Moved or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static SocketsHttpHandler CreateProductionHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        MaxConnectionsPerServer = 2,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        UseCookies = false,
    };
}
