namespace AgentDesk.Cloud.Client;

public sealed class CloudAccessTokenStoreException : Exception
{
    internal CloudAccessTokenStoreException()
        : base("The cloud access token could not be accessed securely.")
    {
    }
}
