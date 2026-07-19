namespace AgentDesk.Cloud.Client;

public interface ICloudAccessTokenVault : ICloudAccessTokenProvider
{
    void SaveAccessToken(string accessToken);

    bool DeleteAccessToken();
}
