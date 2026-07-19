using System.Text.Json.Serialization;

namespace AgentDesk.Cloud.Client;

public sealed class RecoveryKeyPairingPackage
{
    private const int MaximumPackageBytes = 4 * 1024;

    private readonly byte[] _bytes;

    private RecoveryKeyPairingPackage(byte[] bytes)
    {
        _bytes = bytes;
    }

    [JsonIgnore]
    public int ByteLength => _bytes.Length;

    public static RecoveryKeyPairingPackage FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumPackageBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        return new RecoveryKeyPairingPackage(bytes.ToArray());
    }

    public byte[] ExportBytes() => _bytes.ToArray();

    public override string ToString() =>
        $"RecoveryKeyPairingPackage {{ ByteLength = {_bytes.Length} }}";

    internal ReadOnlySpan<byte> AsSpan() => _bytes;
}
