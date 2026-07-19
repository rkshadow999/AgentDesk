namespace AgentDesk.Engine.Transport;

public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
