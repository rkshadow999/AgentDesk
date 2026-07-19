using System.Security.Cryptography;
using System.Text;
using AgentDesk.Core.Security;

namespace AgentDesk.Platform.Windows.Credentials;

public sealed class WindowsCredentialStore : ICredentialStore
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly ICredentialApi _api;
    private readonly string _applicationName;

    public WindowsCredentialStore()
        : this(new WindowsCredentialApi(), "AgentDesk")
    {
    }

    internal WindowsCredentialStore(ICredentialApi api, string applicationName)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        _api = api;
        _applicationName = applicationName.TrimEnd('/');
    }

    public void Save(string name, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var bytes = StrictUtf8.GetBytes(secret);
        try
        {
            _api.Write(GetTarget(name), "AgentDesk", bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string? Read(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var bytes = _api.Read(GetTarget(name));
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public bool Delete(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _api.Delete(GetTarget(name));
    }

    private string GetTarget(string name) => $"{_applicationName}/{name}";
}
