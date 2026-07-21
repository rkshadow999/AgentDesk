using System.Security.Cryptography;
using System.Text;
using AgentDesk.Updater.Core;

namespace AgentDesk.App.Maintenance;

public static class AgentDeskUpdateDefaults
{
    private const string PublicKeyResourceName = "AgentDesk.UpdatePublicKey.SpkiBase64";
    // Community self-hosted feed key (update.rkshadow.com). Generated 2026-07-21.
    private const string PublicKeySha256 =
        "c9b3ccf2dd92519a17720056dc43c1f3bb55f4652a1d99e68f99160657611e37";
    private const string SelfHostedFeedBase = "https://update.rkshadow.com/feed";

    public static AgentDeskUpdateOptions Create(
        SemanticVersion installedVersion,
        UpdateArchitecture architecture,
        string stateDirectory,
        string installationDirectory,
        IReadOnlyList<string> restartArguments,
        bool? allowPrerelease = null)
    {
        // Self-hosted feed serves one channel; pre-release packages still receive feed updates.
        var resolvedAllowPrerelease = allowPrerelease ?? true;
        var publicKey = LoadPinnedPublicKey();
        try
        {
            return new AgentDeskUpdateOptions(
                FeedUri("AgentDesk-updater-manifest.json"),
                FeedUri("AgentDesk-updater-manifest.json.sig"),
                FeedUri("AgentDesk-update-manifest.json"),
                FeedUri("AgentDesk-update-manifest.json.sig"),
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

    private static Uri FeedUri(string fileName) =>
        new($"{SelfHostedFeedBase}/{fileName}");

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
