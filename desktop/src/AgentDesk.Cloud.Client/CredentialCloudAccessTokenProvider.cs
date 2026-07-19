using AgentDesk.Core.Security;

namespace AgentDesk.Cloud.Client;

public sealed class CredentialCloudAccessTokenProvider : ICloudAccessTokenVault
{
    private const int MaximumAccessTokenCharacters = 8 * 1024;

    private readonly ICredentialStore _credentialStore;
    private readonly string _credentialName;

    public CredentialCloudAccessTokenProvider(
        ICredentialStore credentialStore,
        CloudConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.IsLocalOnly || profile.AccessTokenCredentialName is null)
        {
            throw new ArgumentException(
                "A remote cloud profile is required for access-token storage.",
                nameof(profile));
        }

        _credentialStore = credentialStore;
        _credentialName = profile.AccessTokenCredentialName;
    }

    public void SaveAccessToken(string accessToken)
    {
        ValidateAccessToken(accessToken);
        try
        {
            _credentialStore.Save(_credentialName, accessToken);
        }
        catch (Exception)
        {
            throw new CloudAccessTokenStoreException();
        }
    }

    public bool DeleteAccessToken()
    {
        try
        {
            return _credentialStore.Delete(_credentialName);
        }
        catch (Exception)
        {
            throw new CloudAccessTokenStoreException();
        }
    }

    public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? accessToken;
        try
        {
            accessToken = _credentialStore.Read(_credentialName);
        }
        catch (Exception)
        {
            throw new CloudAccessTokenStoreException();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidAccessToken(accessToken))
        {
            throw new CloudAccessTokenStoreException();
        }
        return ValueTask.FromResult(accessToken!);
    }

    public override string ToString() => "CredentialCloudAccessTokenProvider";

    private static void ValidateAccessToken(string value)
    {
        if (!IsValidAccessToken(value))
        {
            throw new ArgumentException("The cloud access token is invalid.", nameof(value));
        }
    }

    private static bool IsValidAccessToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumAccessTokenCharacters &&
        !value.Any(char.IsWhiteSpace);
}
