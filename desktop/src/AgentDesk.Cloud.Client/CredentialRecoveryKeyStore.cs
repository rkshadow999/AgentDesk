using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgentDesk.Core.Security;

namespace AgentDesk.Cloud.Client;

public sealed class CredentialRecoveryKeyStore : IRecoveryKeyStore
{
    private const int KeySizeInBytes = 32;

    private readonly ICredentialStore _credentialStore;
    private readonly object _gate = new();

    public CredentialRecoveryKeyStore(ICredentialStore credentialStore)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        _credentialStore = credentialStore;
    }

    public void Save(RecoveryKeyReference reference, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ValidateKey(key);
        var encodedKey = Convert.ToBase64String(key);
        try
        {
            lock (_gate)
            {
                _credentialStore.Save(BuildCredentialName(reference), encodedKey);
            }
        }
        catch (Exception)
        {
            throw new RecoveryKeyStoreException();
        }
    }

    public byte[]? Read(RecoveryKeyReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        string? encodedKey;
        try
        {
            lock (_gate)
            {
                encodedKey = _credentialStore.Read(BuildCredentialName(reference));
            }
        }
        catch (Exception)
        {
            throw new RecoveryKeyStoreException();
        }

        if (encodedKey is null)
        {
            return null;
        }

        try
        {
            var key = Convert.FromBase64String(encodedKey);
            if (key.Length != KeySizeInBytes)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new RecoveryKeyStoreException();
            }
            return key;
        }
        catch (FormatException)
        {
            throw new RecoveryKeyStoreException();
        }
    }

    public byte[] GetOrCreate(RecoveryKeyReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        lock (_gate)
        {
            var existing = Read(reference);
            if (existing is not null)
            {
                return existing;
            }

            var key = RandomNumberGenerator.GetBytes(KeySizeInBytes);
            try
            {
                Save(reference, key);
                return key;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
        }
    }

    public bool Delete(RecoveryKeyReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        try
        {
            lock (_gate)
            {
                return _credentialStore.Delete(BuildCredentialName(reference));
            }
        }
        catch (Exception)
        {
            throw new RecoveryKeyStoreException();
        }
    }

    public RecoveryKeyPairingPackage ExportPairingPackage(
        RecoveryKeyReference reference,
        ReadOnlySpan<char> passphrase)
    {
        ArgumentNullException.ThrowIfNull(reference);
        RecoveryKeyPairingPackageCodec.ValidatePassphrase(passphrase);
        var key = GetOrCreate(reference);
        try
        {
            return RecoveryKeyPairingPackageCodec.Protect(key, passphrase, reference);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void ImportPairingPackage(
        RecoveryKeyReference reference,
        RecoveryKeyPairingPackage package,
        ReadOnlySpan<char> passphrase)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(package);
        var key = RecoveryKeyPairingPackageCodec.Unprotect(
            package,
            passphrase,
            reference);
        try
        {
            Save(reference, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySizeInBytes)
        {
            throw new ArgumentException("A recovery key must contain exactly 32 bytes.", nameof(key));
        }
    }

    private static string BuildCredentialName(RecoveryKeyReference reference)
    {
        var team = Encoding.UTF8.GetBytes(reference.TeamId);
        var device = Encoding.UTF8.GetBytes(reference.DeviceId);
        var material = new byte[sizeof(int) * 2 + team.Length + device.Length];
        var destination = material.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(destination, team.Length);
        team.CopyTo(destination[sizeof(int)..]);
        var offset = sizeof(int) + team.Length;
        BinaryPrimitives.WriteInt32BigEndian(destination[offset..], device.Length);
        device.CopyTo(destination[(offset + sizeof(int))..]);
        var digest = SHA256.HashData(material);
        CryptographicOperations.ZeroMemory(material);
        return $"cloud/recovery/{Convert.ToHexStringLower(digest)}";
    }
}
