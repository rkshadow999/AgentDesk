using System.Security.Cryptography;
using System.Text;
using AgentDesk.Cloud.Client;

namespace AgentDesk.Cloud.Client.Tests;

public sealed class AesGcmEnvelopeCodecTests
{
    private static readonly EnvelopeBinding Binding =
        new("team-1", "device-1", "session-1", 7);

    [Fact]
    public void EncryptAndDecrypt_RoundTripsWithRandomTwelveByteNonce()
    {
        var codec = new AesGcmEnvelopeCodec(maximumPlaintextBytes: 1024);
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("只在本机出现的会话正文");

        var first = codec.Encrypt(plaintext, key, Binding);
        var second = codec.Encrypt(plaintext, key, Binding);
        var decrypted = codec.Decrypt(first, key, Binding);

        Assert.Equal("AES-256-GCM", first.Algorithm);
        Assert.Equal(12, Convert.FromBase64String(first.Nonce).Length);
        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
        Assert.Equal(plaintext, decrypted);
        Assert.DoesNotContain(
            Convert.ToBase64String(plaintext),
            first.Ciphertext,
            StringComparison.Ordinal);
    }

    public static TheoryData<EnvelopeBinding> WrongBindings => new()
    {
        new EnvelopeBinding("team-2", "device-1", "session-1", 7),
        new EnvelopeBinding("team-1", "device-2", "session-1", 7),
        new EnvelopeBinding("team-1", "device-1", "session-2", 7),
        new EnvelopeBinding("team-1", "device-1", "session-1", 8),
    };

    [Theory]
    [MemberData(nameof(WrongBindings))]
    public void Decrypt_RejectsDifferentAssociatedData(EnvelopeBinding wrongBinding)
    {
        var codec = new AesGcmEnvelopeCodec();
        var key = RandomNumberGenerator.GetBytes(32);
        var envelope = codec.Encrypt("secret"u8, key, Binding);

        var exception = Assert.Throws<EnvelopeAuthenticationException>(
            () => codec.Decrypt(envelope, key, wrongBinding));

        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("team-1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decrypt_RejectsTamperedCiphertext()
    {
        var codec = new AesGcmEnvelopeCodec();
        var key = RandomNumberGenerator.GetBytes(32);
        var original = codec.Encrypt("secret"u8, key, Binding);
        var bytes = Convert.FromBase64String(original.Ciphertext);
        bytes[0] ^= 0x80;
        var tampered = new EncryptedEnvelope(
            original.Algorithm,
            original.Nonce,
            Convert.ToBase64String(bytes));

        Assert.Throws<EnvelopeAuthenticationException>(
            () => codec.Decrypt(tampered, key, Binding));
    }

    [Fact]
    public void Decrypt_RejectsWrongNonceLengthAndMalformedBase64()
    {
        var codec = new AesGcmEnvelopeCodec();
        var key = RandomNumberGenerator.GetBytes(32);

        Assert.Throws<ArgumentException>(
            () => codec.Decrypt(
                new EncryptedEnvelope(
                    "AES-256-GCM",
                    Convert.ToBase64String(new byte[11]),
                    Convert.ToBase64String(new byte[16])),
                key,
                Binding));
        Assert.Throws<ArgumentException>(
            () => codec.Decrypt(
                new EncryptedEnvelope("AES-256-GCM", "not-base64", "also-not-base64"),
                key,
                Binding));
    }

    [Fact]
    public void Codec_RejectsWrongKeySizeAndOversizedPayload()
    {
        var codec = new AesGcmEnvelopeCodec(maximumPlaintextBytes: 4);

        Assert.Throws<ArgumentException>(
            () => codec.Encrypt("data"u8, new byte[31], Binding));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => codec.Encrypt("large"u8, new byte[32], Binding));
    }

    [Fact]
    public void EncryptedEnvelope_ToStringDoesNotExposeCryptographicMaterial()
    {
        var envelope = new EncryptedEnvelope(
            "AES-256-GCM",
            "sensitive-nonce",
            "sensitive-ciphertext");

        var display = envelope.ToString();

        Assert.DoesNotContain("sensitive-nonce", display, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-ciphertext", display, StringComparison.Ordinal);
    }
}
