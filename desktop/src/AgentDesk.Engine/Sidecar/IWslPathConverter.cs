namespace AgentDesk.Engine.Sidecar;

public interface IWslPathConverter
{
    Task<string> ConvertAsync(
        string windowsPath,
        string distributionName,
        CancellationToken cancellationToken);
}
