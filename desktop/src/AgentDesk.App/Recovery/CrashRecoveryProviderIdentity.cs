using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgentDesk.Core.Providers;

namespace AgentDesk.App.Recovery;

public static class CrashRecoveryProviderIdentity
{
    public const int HexLength = 64;

    public static string Create(ProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, profile.BaseUrl);
        Append(hash, BackendName(profile.Backend));
        Append(hash, profile.Model);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static bool FixedTimeEquals(string expected, string actual)
    {
        if (!IsValid(expected) || !IsValid(actual))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expected),
            Convert.FromHexString(actual));
    }

    public static bool IsValid(string identity) =>
        identity is { Length: HexLength } &&
        identity.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static string BackendName(ProviderBackend backend) => backend switch
    {
        ProviderBackend.ChatCompletions => "chat_completions",
        ProviderBackend.Responses => "responses",
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };
}
