namespace AgentDesk.Cloud.Client;

public interface IRecoveryKeyStore
{
    void Save(RecoveryKeyReference reference, ReadOnlySpan<byte> key);

    byte[]? Read(RecoveryKeyReference reference);

    byte[] GetOrCreate(RecoveryKeyReference reference);

    bool Delete(RecoveryKeyReference reference);
}
