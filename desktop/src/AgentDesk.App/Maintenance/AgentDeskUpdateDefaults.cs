using System.Security.Cryptography;
using System.Text;
using AgentDesk.Updater.Core;

namespace AgentDesk.App.Maintenance;

public static class AgentDeskUpdateDefaults
{
    private const string PublicKeyResourceName = "AgentDesk.UpdatePublicKey.SpkiBase64";
    private const string PublicKeySha256 =
        "a7350091fed6493ac0aa0d6222b4f2e0b80eb365c70fcf89d9040276e47b6e15";
    private const string StableFeedTag = "update-stable";
    private const string PrereleaseFeedTag = "update-prerelease";
    private const string ReleaseDownloadBase =
        "https://github.com/rkshadow999/AgentDesk/releases/download";

    public static AgentDeskUpdateOptions Create(
        SemanticVersion installedVersion,
        UpdateArchitecture architecture,
        string stateDirectory,
        string installationDirectory,
        IReadOnlyList<string> restartArguments,
        bool? allowPrerelease = null)
    {
        var resolvedAllowPrerelease = allowPrerelease ?? installedVersion.IsPrerelease;
        var feedTag = resolvedAllowPrerelease ? PrereleaseFeedTag : StableFeedTag;
        var publicKey = LoadPinnedPublicKey();
        try
        {
            return new AgentDeskUpdateOptions(
                FeedUri(feedTag, "AgentDesk-updater-manifest.json"),
                FeedUri(feedTag, "AgentDesk-updater-manifest.json.sig"),
                FeedUri(feedTag, "AgentDesk-update-manifest.json"),
                FeedUri(feedTag, "AgentDesk-update-manifest.json.sig"),
                publicKey,
                installedVersion,
                architecture,
                stateDirectory,
                installationDirectory,
                resolvedAllowPrerelease,
                restartArguments);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    private static Uri FeedUri(string feedTag, string fileName) =>
        new($"{ReleaseDownloadBase}/{feedTag}/{fileName}");

    public static byte[] LoadPinnedPublicKey()
    {
        using var stream = typeof(AgentDeskUpdateDefaults).Assembly
            .GetManifestResourceStream(PublicKeyResourceName) ??
            throw new UpdateSecurityException("The pinned AgentDesk update public key is missing.");
        if (stream.Length is <= 0 or > 4096)
        {
            throw new UpdateSecurityException("The pinned AgentDesk update public key is invalid.");
        }

        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: false);
        byte[] key;
        try
        {
            key = Convert.FromBase64String(reader.ReadToEnd().Trim());
        }
        catch (Exception exception) when (
            exception is FormatException or DecoderFallbackException)
        {
            throw new UpdateSecurityException(
                "The pinned AgentDesk update public key is invalid.",
                exception);
        }

        try
        {
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(key));
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out var bytesRead);
            if (!string.Equals(fingerprint, PublicKeySha256, StringComparison.Ordinal) ||
                bytesRead != key.Length ||
                ecdsa.KeySize != 256 ||
                ecdsa.ExportParameters(includePrivateParameters: false).Curve.Oid.Value !=
                    "1.2.840.10045.3.1.7")
            {
                throw new UpdateSecurityException(
                    "The pinned AgentDesk update public key is invalid.");
            }
            return key;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }
}
