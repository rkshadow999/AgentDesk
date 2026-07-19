namespace AgentDesk.Cloud.Client;

public interface ICloudAccessTokenProvider
{
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}
