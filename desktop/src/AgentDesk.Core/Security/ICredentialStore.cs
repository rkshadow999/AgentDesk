namespace AgentDesk.Core.Security;

public interface ICredentialStore
{
    void Save(string name, string secret);

    string? Read(string name);

    bool Delete(string name);
}
