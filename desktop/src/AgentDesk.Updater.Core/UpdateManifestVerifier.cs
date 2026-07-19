using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentDesk.Updater.Core;

public static class UpdateManifestVerifier
{
    public const int MaximumManifestBytes = 64 * 1024;
    public const long MaximumPackageBytes = 512L * 1024 * 1024;
    private const int MaximumAssets = 2;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static UpdateManifest Verify(
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> detachedSignature,
        ReadOnlySpan<byte> publicKeySubjectPublicKeyInfo,
        UpdateOriginPolicy originPolicy,
        string expectedProduct = "AgentDesk")
    {
        ArgumentNullException.ThrowIfNull(originPolicy);
        if (expectedProduct is not ("AgentDesk" or "AgentDesk.Updater"))
        {
            throw new ArgumentException(
                "The expected update product is unsupported.",
                nameof(expectedProduct));
        }
        if (manifestBytes.Length is 0 or > MaximumManifestBytes ||
            detachedSignature.Length is < 8 or > 128 ||
            publicKeySubjectPublicKeyInfo.Length is < 32 or > 1024)
        {
            throw new UpdateSecurityException("The signed update metadata has an invalid size.");
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeySubjectPublicKeyInfo, out var bytesRead);
            var parameters = key.ExportParameters(includePrivateParameters: false);
            if (bytesRead != publicKeySubjectPublicKeyInfo.Length ||
                key.KeySize != 256 ||
                parameters.Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
                !key.VerifyData(
                    manifestBytes,
                    detachedSignature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
            {
                throw new UpdateSecurityException("The update manifest signature is invalid.");
            }
        }
        catch (UpdateSecurityException)
        {
            throw;
        }
        catch (CryptographicException exception)
        {
            throw new UpdateSecurityException("The update manifest signature is invalid.", exception);
        }

        return ParseManifest(manifestBytes, originPolicy, expectedProduct);
    }

    private static UpdateManifest ParseManifest(
        ReadOnlySpan<byte> manifestBytes,
        UpdateOriginPolicy originPolicy,
        string expectedProduct)
    {
        try
        {
            _ = StrictUtf8.GetString(manifestBytes);
            using var document = JsonDocument.Parse(
                manifestBytes.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            var root = document.RootElement;
            EnsureObjectProperties(root, ["schemaVersion", "product", "version", "assets"]);

            var schemaVersion = root.GetProperty("schemaVersion").GetInt32();
            var product = root.GetProperty("product").GetString();
            var versionText = root.GetProperty("version").GetString();
            if (schemaVersion != 1 || product != expectedProduct || versionText is null)
            {
                throw new UpdateSecurityException("The update manifest identity is invalid.");
            }

            var version = SemanticVersion.Parse(versionText);
            var assetsElement = root.GetProperty("assets");
            if (assetsElement.ValueKind != JsonValueKind.Array ||
                assetsElement.GetArrayLength() is 0 or > MaximumAssets)
            {
                throw new UpdateSecurityException("The update manifest asset list is invalid.");
            }

            var assets = new List<UpdateAsset>(assetsElement.GetArrayLength());
            var architectures = new HashSet<UpdateArchitecture>();
            foreach (var element in assetsElement.EnumerateArray())
            {
                EnsureObjectProperties(element, ["architecture", "url", "sha256", "size", "entryPoint"]);
                var architecture = ParseArchitecture(element.GetProperty("architecture").GetString());
                if (!architectures.Add(architecture))
                {
                    throw new UpdateSecurityException("The update manifest contains duplicate architecture assets.");
                }

                var uriText = element.GetProperty("url").GetString();
                if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
                {
                    throw new UpdateSecurityException("The update asset URI is invalid.");
                }

                originPolicy.EnsureAllowedInitialUri(uri);
                var sha256 = element.GetProperty("sha256").GetString();
                var size = element.GetProperty("size").GetInt64();
                var entryPoint = element.GetProperty("entryPoint").GetString();
                if (!IsLowercaseSha256(sha256) ||
                    size is <= 0 or > MaximumPackageBytes ||
                    !IsSafeEntryPoint(entryPoint))
                {
                    throw new UpdateSecurityException("The update asset metadata is invalid.");
                }

                assets.Add(new UpdateAsset(architecture, uri, sha256!, size, entryPoint!));
            }

            return new UpdateManifest(schemaVersion, product, version, assets);
        }
        catch (UpdateSecurityException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or
            DecoderFallbackException or
            FormatException or
            InvalidOperationException or
            OverflowException)
        {
            throw new UpdateSecurityException("The update manifest is malformed.", exception);
        }
    }

    private static void EnsureObjectProperties(JsonElement element, IReadOnlyCollection<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new UpdateSecurityException("The update manifest structure is invalid.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new UpdateSecurityException("The update manifest contains unknown or duplicate fields.");
            }
        }

        if (seen.Count != expected.Count)
        {
            throw new UpdateSecurityException("The update manifest is missing required fields.");
        }
    }

    private static UpdateArchitecture ParseArchitecture(string? value) => value switch
    {
        "x64" => UpdateArchitecture.X64,
        "arm64" => UpdateArchitecture.Arm64,
        _ => throw new UpdateSecurityException("The update architecture is invalid."),
    };

    private static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsSafeEntryPoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
            !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.IsPathRooted(value) || value.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = value.Replace('\\', '/').Split('/');
        return segments.All(segment =>
            segment.Length is > 0 and <= 255 &&
            segment is not "." and not ".." &&
            !segment.Any(character => char.IsControl(character)));
    }
}
