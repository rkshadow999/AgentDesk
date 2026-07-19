namespace AgentDesk.Platform.Windows.Credentials;

internal interface ICredentialApi
{
    void Write(string target, string userName, byte[] secret);

    byte[]? Read(string target);

    bool Delete(string target);
}
