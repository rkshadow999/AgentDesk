namespace AgentDesk.Updater.Core;

public sealed record UpdateCheckRequest(
    Uri ManifestUri,
    Uri SignatureUri,
    ReadOnlyMemory<byte> PublicKeySubjectPublicKeyInfo,
    SemanticVersion InstalledVersion,
    UpdateArchitecture Architecture,
    bool AllowPrerelease,
    string StateDirectory,
    string ExpectedProduct = "AgentDesk");

public sealed record StagedUpdate(
    UpdateManifest Manifest,
    UpdateAsset Asset,
    string TransactionDirectory,
    string PackagePath,
    string PayloadDirectory);

public sealed class PortableUpdateService : IDisposable
{
    private const int MaximumSignatureBytes = 128;
    private readonly SecureUpdateDownloader _downloader;
    private readonly SafeZipExtractor _extractor;
    private readonly bool _ownsDownloader;

    public PortableUpdateService(UpdateOriginPolicy originPolicy)
        : this(new SecureUpdateDownloader(originPolicy), new SafeZipExtractor())
    {
        _ownsDownloader = true;
    }

    internal PortableUpdateService(
        SecureUpdateDownloader downloader,
        SafeZipExtractor extractor)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    }

    public async Task<StagedUpdate?> CheckAndStageAsync(
        UpdateCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ManifestUri);
        ArgumentNullException.ThrowIfNull(request.SignatureUri);
        var publicKey = request.PublicKeySubjectPublicKeyInfo.Span.ToArray();
        var stateDirectory = UpdatePathSafety.FullPath(request.StateDirectory);
        Directory.CreateDirectory(stateDirectory);
        UpdatePathSafety.EnsureNoReparsePoints(stateDirectory);

        var manifestBytes = await _downloader.DownloadBytesAsync(
            request.ManifestUri,
            UpdateManifestVerifier.MaximumManifestBytes,
            cancellationToken).ConfigureAwait(false);
        var signatureBytes = await _downloader.DownloadBytesAsync(
            request.SignatureUri,
            MaximumSignatureBytes,
            cancellationToken).ConfigureAwait(false);
        var manifest = UpdateManifestVerifier.Verify(
            manifestBytes,
            signatureBytes,
            publicKey,
            _downloader.OriginPolicy,
            request.ExpectedProduct);

        using var highestSeenStore = new HighestSeenVersionStore(
            Path.Combine(stateDirectory, "highest-seen.json"));
        var highestSeen = await highestSeenStore.GetAsync(cancellationToken).ConfigureAwait(false);
        var asset = UpdateSelector.Select(
            manifest,
            request.InstalledVersion,
            highestSeen,
            request.Architecture,
            request.AllowPrerelease);
        if (asset is null)
        {
            return null;
        }

        await highestSeenStore.RecordAsync(manifest.Version, cancellationToken).ConfigureAwait(false);

        var stagingRoot = Path.Combine(stateDirectory, "staging");
        Directory.CreateDirectory(stagingRoot);
        UpdatePathSafety.EnsureNoReparsePoints(stagingRoot);
        var transactionDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transactionDirectory);
        var packagePath = Path.Combine(transactionDirectory, "package.zip");
        var payloadDirectory = Path.Combine(transactionDirectory, "payload");
        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(transactionDirectory, "manifest.json"),
                manifestBytes,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(transactionDirectory, "manifest.sig"),
                signatureBytes,
                cancellationToken).ConfigureAwait(false);
            await _downloader.DownloadFileAsync(
                asset.Uri,
                packagePath,
                asset.Size,
                asset.Sha256Hex,
                cancellationToken).ConfigureAwait(false);
            await _extractor.ExtractAsync(
                packagePath,
                payloadDirectory,
                cancellationToken).ConfigureAwait(false);

            var entryPoint = Path.Combine(
                payloadDirectory,
                asset.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
            UpdatePathSafety.EnsureContained(payloadDirectory, entryPoint, "update entry point");
            if (!File.Exists(entryPoint) ||
                (File.GetAttributes(entryPoint) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UpdateSecurityException("The staged update is missing its trusted entry point.");
            }

            return new StagedUpdate(
                manifest,
                asset,
                transactionDirectory,
                packagePath,
                payloadDirectory);
        }
        catch
        {
            CleanupTransaction(stateDirectory, transactionDirectory);
            throw;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    public void Dispose()
    {
        if (_ownsDownloader)
        {
            _downloader.Dispose();
        }
    }

    private static void CleanupTransaction(string stateDirectory, string transactionDirectory)
    {
        UpdatePathSafety.EnsureContained(stateDirectory, transactionDirectory, "staging transaction");
        if (Directory.Exists(transactionDirectory))
        {
            Directory.Delete(transactionDirectory, recursive: true);
        }
    }
}
