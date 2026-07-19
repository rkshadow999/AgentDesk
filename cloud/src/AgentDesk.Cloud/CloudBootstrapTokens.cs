using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace AgentDesk.Cloud;

internal sealed class CloudBootstrapTokens
{
    public const int MinimumTokenCharacters = 32;
    private readonly byte[][] _tokenHashes;

    public CloudBootstrapTokens(IOptions<CloudOptions> options)
    {
        var configuredTokens = new List<string> { options.Value.BootstrapToken };
        if (!string.IsNullOrEmpty(options.Value.PreviousBootstrapToken))
        {
            configuredTokens.Add(options.Value.PreviousBootstrapToken);
        }

        _tokenHashes = configuredTokens
            .Select(HashToken)
            .ToArray();
    }

    public bool Matches(string suppliedToken)
    {
        var suppliedHash = HashToken(suppliedToken);
        try
        {
            var matched = false;
            foreach (var configuredHash in _tokenHashes)
            {
                matched |= CryptographicOperations.FixedTimeEquals(configuredHash, suppliedHash);
            }
            return matched;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(suppliedHash);
        }
    }

    public static bool IsValidConfiguredToken(string? token) =>
        !string.IsNullOrWhiteSpace(token) && token.Length >= MinimumTokenCharacters;

    public static bool IsValidOptionalToken(string? token) =>
        string.IsNullOrEmpty(token) || IsValidConfiguredToken(token);

    public static bool ConfiguredTokensDiffer(CloudOptions options)
    {
        if (string.IsNullOrEmpty(options.PreviousBootstrapToken))
        {
            return true;
        }

        var currentHash = HashToken(options.BootstrapToken);
        var previousHash = HashToken(options.PreviousBootstrapToken);
        try
        {
            return !CryptographicOperations.FixedTimeEquals(currentHash, previousHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentHash);
            CryptographicOperations.ZeroMemory(previousHash);
        }
    }

    private static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
