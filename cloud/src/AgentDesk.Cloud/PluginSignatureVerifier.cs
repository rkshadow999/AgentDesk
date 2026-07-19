using System.Security.Cryptography;
using System.Text;

namespace AgentDesk.Cloud;

internal static class PluginSignatureVerifier
{
    public static bool Verify(
        string publicKeyPem,
        string pluginId,
        string version,
        PluginPublishRequest request)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            var signature = Convert.FromBase64String(request.Signature);
            var payload = Encoding.UTF8.GetBytes(
                $"{pluginId}\n{version}\n{request.Sha256.ToLowerInvariant()}\n{request.ManifestJson}");
            return key.VerifyData(payload, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception exception) when (
            exception is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    public static bool IsSupportedPublicKey(string publicKeyPem)
    {
        if (publicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.KeySize >= 256;
        }
        catch (Exception exception) when (
            exception is CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
