using System.Text.Json;

namespace AgentDesk.Engine.Transport;

public sealed class JsonRpcRequestEventArgs(
    JsonElement id,
    string method,
    JsonElement parameters) : EventArgs
{
    private Func<CancellationToken, Task<JsonRpcResponse>>? _handler;

    public JsonElement Id { get; } = id;

    public string Method { get; } = method;

    public JsonElement Parameters { get; } = parameters;

    public bool TryHandle(Func<CancellationToken, Task<JsonRpcResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Interlocked.CompareExchange(ref _handler, handler, null) is null;
    }

    internal Task<JsonRpcResponse>? InvokeHandlerAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref _handler)?.Invoke(cancellationToken);
}

public sealed class JsonRpcResponse
{
    private JsonRpcResponse(object? result, JsonRpcResponseError? error)
    {
        Result = result;
        Error = error;
    }

    internal object? Result { get; }

    internal JsonRpcResponseError? Error { get; }

    public static JsonRpcResponse Success(object? result) => new(result, error: null);

    public static JsonRpcResponse Failure(int code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new(result: null, new JsonRpcResponseError(code, message));
    }
}

internal sealed record JsonRpcResponseError(int Code, string Message);
