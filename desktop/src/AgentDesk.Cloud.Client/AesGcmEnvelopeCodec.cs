using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AgentDesk.Cloud.Client;

public sealed class AesGcmEnvelopeCodec
{
    private const int KeySizeInBytes = 32;
    private const int NonceSizeInBytes = 12;
    private const int TagSizeInBytes = 16;
    private const int MaximumPermittedPlaintextBytes = 64 * 1024 * 1024;

    private static readonly byte[] AssociatedDataPrefix =
        "AgentDesk.Cloud.Envelope/v1"u8.ToArray();
    private static readonly byte[] HandoffAssociatedDataPrefix =
        "AgentDesk.Cloud.Handoff/v1"u8.ToArray();

    private readonly int _maximumPlaintextBytes;

    public AesGcmEnvelopeCodec(
        int maximumPlaintextBytes =
            CloudConnectionOptions.DefaultMaximumEnvelopeBytes - TagSizeInBytes)
    {
        if (maximumPlaintextBytes is < 0 or > MaximumPermittedPlaintextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPlaintextBytes));
        }

        _maximumPlaintextBytes = maximumPlaintextBytes;
    }

    public EncryptedEnvelope Encrypt(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> key,
        EnvelopeBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return EncryptCore(plaintext, key, BuildAssociatedData(binding));
    }

    public EncryptedEnvelope Encrypt(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> key,
        HandoffEnvelopeBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return EncryptCore(plaintext, key, BuildAssociatedData(binding));
    }

    private EncryptedEnvelope EncryptCore(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> associatedData)
    {
        ValidateKey(key);
        if (plaintext.Length > _maximumPlaintextBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(plaintext),
                "The plaintext exceeds the configured envelope limit.");
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSizeInBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeInBytes];
        using (var aes = new AesGcm(key, TagSizeInBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }

        var ciphertextAndTag = new byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(ciphertextAndTag, 0);
        tag.CopyTo(ciphertextAndTag, ciphertext.Length);

        return new EncryptedEnvelope(
            EncryptedEnvelope.Aes256GcmAlgorithm,
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertextAndTag));
    }

    public byte[] Decrypt(
        EncryptedEnvelope envelope,
        ReadOnlySpan<byte> key,
        EnvelopeBinding binding)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(binding);
        return DecryptCore(envelope, key, BuildAssociatedData(binding));
    }

    public byte[] Decrypt(
        EncryptedEnvelope envelope,
        ReadOnlySpan<byte> key,
        HandoffEnvelopeBinding binding)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(binding);
        return DecryptCore(envelope, key, BuildAssociatedData(binding));
    }

    private byte[] DecryptCore(
        EncryptedEnvelope envelope,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> associatedData)
    {
        ValidateKey(key);

        var nonce = DecodeBase64(envelope.Nonce, NonceSizeInBytes, nameof(envelope));
        if (nonce.Length != NonceSizeInBytes)
        {
            throw new ArgumentException("An AES-256-GCM nonce must contain exactly 12 bytes.", nameof(envelope));
        }

        var maximumCiphertextBytes = checked(_maximumPlaintextBytes + TagSizeInBytes);
        var ciphertextAndTag = DecodeBase64(
            envelope.Ciphertext,
            maximumCiphertextBytes,
            nameof(envelope));
        if (ciphertextAndTag.Length < TagSizeInBytes)
        {
            throw new ArgumentException("The encrypted envelope is missing its authentication tag.", nameof(envelope));
        }

        var plaintextLength = ciphertextAndTag.Length - TagSizeInBytes;
        var plaintext = new byte[plaintextLength];
        try
        {
            using var aes = new AesGcm(key, TagSizeInBytes);
            aes.Decrypt(
                nonce,
                ciphertextAndTag.AsSpan(0, plaintextLength),
                ciphertextAndTag.AsSpan(plaintextLength, TagSizeInBytes),
                plaintext,
                associatedData);
            return plaintext;
        }
        catch (CryptographicException exception)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new EnvelopeAuthenticationException(exception);
        }
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySizeInBytes)
        {
            throw new ArgumentException("AES-256-GCM requires a 32-byte key.", nameof(key));
        }
    }

    private static byte[] DecodeBase64(string value, int maximumBytes, string parameterName)
    {
        var maximumEncodedLength = checked(((maximumBytes + 2) / 3) * 4);
        if (value.Length > maximumEncodedLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The encoded envelope exceeds the configured limit.");
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length > maximumBytes)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "The decoded envelope exceeds the configured limit.");
            }

            return bytes;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The encrypted envelope is not valid Base64.", parameterName, exception);
        }
    }

    private static byte[] BuildAssociatedData(EnvelopeBinding binding)
    {
        var team = Encoding.UTF8.GetBytes(binding.TeamId);
        var device = Encoding.UTF8.GetBytes(binding.DeviceId);
        var session = Encoding.UTF8.GetBytes(binding.SessionId);
        var length = checked(
            AssociatedDataPrefix.Length +
            sizeof(int) * 4 +
            team.Length +
            device.Length +
            session.Length);
        var result = new byte[length];
        var destination = result.AsSpan();
        AssociatedDataPrefix.CopyTo(destination);
        var offset = AssociatedDataPrefix.Length;
        offset = WriteLengthPrefixed(team, destination, offset);
        offset = WriteLengthPrefixed(device, destination, offset);
        offset = WriteLengthPrefixed(session, destination, offset);
        BinaryPrimitives.WriteInt32BigEndian(destination[offset..], binding.Revision);
        return result;
    }

    private static byte[] BuildAssociatedData(HandoffEnvelopeBinding binding)
    {
        var team = Encoding.UTF8.GetBytes(binding.TeamId);
        var source = Encoding.UTF8.GetBytes(binding.SourceDeviceId);
        var target = Encoding.UTF8.GetBytes(binding.TargetDeviceId);
        var session = Encoding.UTF8.GetBytes(binding.SessionId);
        var handoff = Encoding.UTF8.GetBytes(binding.HandoffId);
        var length = checked(
            HandoffAssociatedDataPrefix.Length +
            sizeof(int) * 6 +
            team.Length +
            source.Length +
            target.Length +
            session.Length +
            handoff.Length);
        var result = new byte[length];
        var destination = result.AsSpan();
        HandoffAssociatedDataPrefix.CopyTo(destination);
        var offset = HandoffAssociatedDataPrefix.Length;
        offset = WriteLengthPrefixed(team, destination, offset);
        offset = WriteLengthPrefixed(source, destination, offset);
        offset = WriteLengthPrefixed(target, destination, offset);
        offset = WriteLengthPrefixed(session, destination, offset);
        offset = WriteLengthPrefixed(handoff, destination, offset);
        BinaryPrimitives.WriteInt32BigEndian(destination[offset..], binding.Revision);
        return result;
    }

    private static int WriteLengthPrefixed(byte[] value, Span<byte> destination, int offset)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination[offset..], value.Length);
        offset += sizeof(int);
        value.CopyTo(destination[offset..]);
        return offset + value.Length;
    }
}
