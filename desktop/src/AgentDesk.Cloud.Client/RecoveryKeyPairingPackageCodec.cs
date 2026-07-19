using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AgentDesk.Cloud.Client;

internal static class RecoveryKeyPairingPackageCodec
{
    private const int KeyBytes = 32;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int IterationBytes = sizeof(int);
    private const int Iterations = 600_000;
    private const int MinimumPassphraseCharacters = 16;
    private const int MaximumPassphraseCharacters = 1024;

    private static readonly byte[] Magic = "ADRKPKG1"u8.ToArray();
    private static readonly byte[] ContextPrefix =
        "AgentDesk.RecoveryKeyPairing/v1"u8.ToArray();

    private static int HeaderBytes => Magic.Length + IterationBytes + SaltBytes + NonceBytes;

    private static int PackageBytes => HeaderBytes + KeyBytes + TagBytes;

    public static RecoveryKeyPairingPackage Protect(
        ReadOnlySpan<byte> recoveryKey,
        ReadOnlySpan<char> passphrase,
        RecoveryKeyReference reference)
    {
        ValidateRecoveryKey(recoveryKey);
        ValidatePassphrase(passphrase);
        ArgumentNullException.ThrowIfNull(reference);

        var packageBytes = new byte[PackageBytes];
        var header = packageBytes.AsSpan(0, HeaderBytes);
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32BigEndian(header[Magic.Length..], Iterations);
        var salt = header.Slice(Magic.Length + IterationBytes, SaltBytes);
        RandomNumberGenerator.Fill(salt);
        var nonce = header.Slice(
            Magic.Length + IterationBytes + SaltBytes,
            NonceBytes);
        RandomNumberGenerator.Fill(nonce);

        var derivedKey = new byte[KeyBytes];
        var passwordBytes = EncodePassphrase(passphrase);
        var associatedData = BuildAssociatedData(header, reference);
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                derivedKey,
                Iterations,
                HashAlgorithmName.SHA256);
            using var aes = new AesGcm(derivedKey, TagBytes);
            aes.Encrypt(
                nonce,
                recoveryKey,
                packageBytes.AsSpan(HeaderBytes, KeyBytes),
                packageBytes.AsSpan(HeaderBytes + KeyBytes, TagBytes),
                associatedData);
            return RecoveryKeyPairingPackage.FromBytes(packageBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(associatedData);
            CryptographicOperations.ZeroMemory(packageBytes);
        }
    }

    public static byte[] Unprotect(
        RecoveryKeyPairingPackage package,
        ReadOnlySpan<char> passphrase,
        RecoveryKeyReference reference)
    {
        ArgumentNullException.ThrowIfNull(package);
        ValidatePassphrase(passphrase);
        ArgumentNullException.ThrowIfNull(reference);

        var packageBytes = package.ExportBytes();
        var recoveryKey = new byte[KeyBytes];
        byte[]? passwordBytes = null;
        byte[]? derivedKey = null;
        byte[]? associatedData = null;
        try
        {
            if (packageBytes.Length != PackageBytes)
            {
                throw new RecoveryKeyPairingException();
            }

            var header = packageBytes.AsSpan(0, HeaderBytes);
            if (!header[..Magic.Length].SequenceEqual(Magic) ||
                BinaryPrimitives.ReadInt32BigEndian(header[Magic.Length..]) != Iterations)
            {
                throw new RecoveryKeyPairingException();
            }

            var salt = header.Slice(Magic.Length + IterationBytes, SaltBytes);
            var nonce = header.Slice(
                Magic.Length + IterationBytes + SaltBytes,
                NonceBytes);
            passwordBytes = EncodePassphrase(passphrase);
            derivedKey = new byte[KeyBytes];
            associatedData = BuildAssociatedData(header, reference);
            Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                derivedKey,
                Iterations,
                HashAlgorithmName.SHA256);
            using var aes = new AesGcm(derivedKey, TagBytes);
            aes.Decrypt(
                nonce,
                packageBytes.AsSpan(HeaderBytes, KeyBytes),
                packageBytes.AsSpan(HeaderBytes + KeyBytes, TagBytes),
                recoveryKey,
                associatedData);
            return recoveryKey;
        }
        catch (RecoveryKeyPairingException)
        {
            CryptographicOperations.ZeroMemory(recoveryKey);
            throw;
        }
        catch (Exception exception) when (
            exception is CryptographicException or ArgumentException)
        {
            CryptographicOperations.ZeroMemory(recoveryKey);
            throw new RecoveryKeyPairingException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(packageBytes);
            if (passwordBytes is not null)
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
            if (derivedKey is not null)
            {
                CryptographicOperations.ZeroMemory(derivedKey);
            }
            if (associatedData is not null)
            {
                CryptographicOperations.ZeroMemory(associatedData);
            }
        }
    }

    private static byte[] BuildAssociatedData(
        ReadOnlySpan<byte> header,
        RecoveryKeyReference reference)
    {
        var referenceBytes = Encoding.UTF8.GetBytes(
            $"{reference.TeamId}\0{reference.DeviceId}");
        try
        {
            var referenceDigest = SHA256.HashData(referenceBytes);
            var associatedData = new byte[
                ContextPrefix.Length + header.Length + referenceDigest.Length];
            ContextPrefix.CopyTo(associatedData, 0);
            header.CopyTo(associatedData.AsSpan(ContextPrefix.Length));
            referenceDigest.CopyTo(
                associatedData,
                ContextPrefix.Length + header.Length);
            CryptographicOperations.ZeroMemory(referenceDigest);
            return associatedData;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(referenceBytes);
        }
    }

    private static byte[] EncodePassphrase(ReadOnlySpan<char> passphrase)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(passphrase)];
        Encoding.UTF8.GetBytes(passphrase, bytes);
        return bytes;
    }

    private static void ValidateRecoveryKey(ReadOnlySpan<byte> recoveryKey)
    {
        if (recoveryKey.Length != KeyBytes)
        {
            throw new ArgumentException(
                "A recovery key must contain exactly 32 bytes.",
                nameof(recoveryKey));
        }
    }

    internal static void ValidatePassphrase(ReadOnlySpan<char> passphrase)
    {
        if (passphrase.Length is < MinimumPassphraseCharacters or > MaximumPassphraseCharacters ||
            passphrase.Trim().IsEmpty)
        {
            throw new ArgumentException(
                "The pairing passphrase must contain between 16 and 1024 characters.",
                nameof(passphrase));
        }
    }
}
