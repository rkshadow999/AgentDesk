using System.Security.Cryptography;
using AgentDesk.Cloud.Client;
using AgentDesk.Core.Security;

namespace AgentDesk.Cloud.Client.Tests;

public sealed class CredentialRecoveryKeyStoreTests
{
    private static readonly RecoveryKeyReference Reference = new("team-1", "device-1");

    [Fact]
    public void SaveAndRead_RoundTripsThroughCredentialStoreWithoutIdentifiersInTarget()
    {
        var credentials = new RecordingCredentialStore();
        var store = new CredentialRecoveryKeyStore(credentials);
        var key = RandomNumberGenerator.GetBytes(32);

        store.Save(Reference, key);
        var restored = store.Read(Reference);

        Assert.Equal(key, restored);
        Assert.StartsWith("cloud/recovery/", credentials.LastName, StringComparison.Ordinal);
        Assert.DoesNotContain("team-1", credentials.LastName, StringComparison.Ordinal);
        Assert.DoesNotContain("device-1", credentials.LastName, StringComparison.Ordinal);
        Assert.NotEqual(Convert.ToBase64String(key), credentials.LastName);
    }

    [Fact]
    public void GetOrCreate_GeneratesAndReusesA256BitKey()
    {
        var credentials = new RecordingCredentialStore();
        var store = new CredentialRecoveryKeyStore(credentials);

        var first = store.GetOrCreate(Reference);
        var second = store.GetOrCreate(Reference);

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
        Assert.Equal(1, credentials.SaveCount);
    }

    [Fact]
    public void Save_RejectsNon256BitKeyBeforeCredentialStoreIsCalled()
    {
        var credentials = new RecordingCredentialStore();
        var store = new CredentialRecoveryKeyStore(credentials);

        Assert.Throws<ArgumentException>(() => store.Save(Reference, new byte[31]));
        Assert.Equal(0, credentials.SaveCount);
    }

    [Fact]
    public void Read_MalformedCredentialFailsWithoutEchoingStoredMaterial()
    {
        const string malformed = "not-a-recovery-key";
        var credentials = new RecordingCredentialStore { ReadValue = malformed };
        var store = new CredentialRecoveryKeyStore(credentials);

        var exception = Assert.Throws<RecoveryKeyStoreException>(() => store.Read(Reference));

        Assert.DoesNotContain(malformed, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Save_UnderlyingFailureDoesNotEchoKeyOrCredentialMessage()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var encodedKey = Convert.ToBase64String(key);
        var credentials = new RecordingCredentialStore
        {
            SaveException = new InvalidOperationException(encodedKey),
        };
        var store = new CredentialRecoveryKeyStore(credentials);

        var exception = Assert.Throws<RecoveryKeyStoreException>(
            () => store.Save(Reference, key));

        Assert.DoesNotContain(encodedKey, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Delete_UsesTheSameOpaqueCredentialTarget()
    {
        var credentials = new RecordingCredentialStore { DeleteResult = true };
        var store = new CredentialRecoveryKeyStore(credentials);
        store.Save(Reference, new byte[32]);
        var savedName = credentials.LastName;

        var deleted = store.Delete(Reference);

        Assert.True(deleted);
        Assert.Equal(savedName, credentials.LastName);
    }

    [Fact]
    public void ExportPairingPackage_RejectsWhitespacePassphraseWithoutCreatingAKey()
    {
        var credentials = new RecordingCredentialStore();
        var store = new CredentialRecoveryKeyStore(credentials);

        Assert.Throws<ArgumentException>(() =>
            store.ExportPairingPackage(Reference, new string(' ', 16)));

        Assert.Equal(0, credentials.SaveCount);
    }

    private sealed class RecordingCredentialStore : ICredentialStore
    {
        private string? _value;

        public string? LastName { get; private set; }

        public int SaveCount { get; private set; }

        public string? ReadValue { get; init; }

        public Exception? SaveException { get; init; }

        public bool DeleteResult { get; init; }

        public void Save(string name, string secret)
        {
            LastName = name;
            SaveCount++;
            if (SaveException is not null)
            {
                throw SaveException;
            }
            _value = secret;
        }

        public string? Read(string name)
        {
            LastName = name;
            return ReadValue ?? _value;
        }

        public bool Delete(string name)
        {
            LastName = name;
            return DeleteResult;
        }
    }
}
