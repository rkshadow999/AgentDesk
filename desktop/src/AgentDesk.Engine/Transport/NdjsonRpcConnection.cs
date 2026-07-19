using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using AgentDesk.Core.Engine;

namespace AgentDesk.Engine.Transport;

public sealed class NdjsonRpcConnection : IAsyncDisposable
{
    private const int MaxConcurrentIncomingRequests = 64;
    internal const int MaxIncomingMessageBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = 64,
    };

    private readonly PipeReader _reader;
    private readonly StreamWriter _writer;
    private readonly int _maxIncomingMessageBytes;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<long, Task> _incomingRequests = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _readerTask;
    private long _nextRequestId;
    private long _nextIncomingRequestId;
    private int _activeIncomingRequestCount;
    private Exception? _terminalError;

    public event EventHandler<JsonRpcNotification>? NotificationReceived;

    public event EventHandler<JsonRpcRequestEventArgs>? RequestReceived;

    public event EventHandler<EngineFaultedEventArgs>? Faulted;

    public NdjsonRpcConnection(Stream input, Stream output)
        : this(input, output, MaxIncomingMessageBytes)
    {
    }

    internal NdjsonRpcConnection(Stream input, Stream output, int maxIncomingMessageBytes)
    {
        if (maxIncomingMessageBytes is < 1 or > MaxIncomingMessageBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIncomingMessageBytes));
        }

        _maxIncomingMessageBytes = maxIncomingMessageBytes;
        _reader = PipeReader.Create(input, new StreamPipeReaderOptions(leaveOpen: false));
        _writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: false)
        {
            AutoFlush = true,
        };
        _readerTask = ReadMessagesAsync(_shutdown.Token);
    }

    public async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateLock)
        {
            if (_terminalError is { } terminalError)
            {
                throw terminalError;
            }

            if (!_pending.TryAdd(requestId, completion))
            {
                throw new InvalidOperationException($"Duplicate JSON-RPC request id {requestId}.");
            }
        }

        try
        {
            var wireMethod = ToWireMethod(method);
            var request = new
            {
                jsonrpc = "2.0",
                id = requestId,
                method = wireMethod,
                @params = parameters,
            };
            var line = JsonSerializer.Serialize(request, SerializerOptions);

            await WriteLineAsync(line, cancellationToken).ConfigureAwait(false);

            using var registration = cancellationToken.Register(
                static state =>
                {
                    var tuple = ((NdjsonRpcConnection Connection, long RequestId))state!;
                    if (tuple.Connection._pending.TryRemove(tuple.RequestId, out var pending))
                    {
                        pending.TrySetCanceled();
                    }
                },
                (this, requestId));

            return await completion.Task.ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(requestId, out _);
            throw;
        }
    }

    public async Task SendNotificationAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var wireMethod = ToWireMethod(method);
        var notification = new
        {
            jsonrpc = "2.0",
            method = wireMethod,
            @params = parameters,
        };
        var line = JsonSerializer.Serialize(notification, SerializerOptions);

        await WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
    }

    private static string ToWireMethod(string method) =>
        method.StartsWith("agentdesk/", StringComparison.Ordinal) ||
        method.StartsWith("x.ai/", StringComparison.Ordinal)
            ? $"_{method}"
            : method;

    private static string FromWireMethod(string method) =>
        method.StartsWith("_agentdesk/", StringComparison.Ordinal) ||
        method.StartsWith("_x.ai/", StringComparison.Ordinal)
            ? method[1..]
            : method;

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadMessagesAsync(CancellationToken cancellationToken)
    {
        Exception? terminalError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var message = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    terminalError = new EndOfStreamException("The engine closed its JSON-RPC output.");
                    break;
                }

                var root = message.RootElement;
                if (root.TryGetProperty("method", out var methodElement))
                {
                    var method = methodElement.GetString();
                    if (!string.IsNullOrWhiteSpace(method))
                    {
                        method = FromWireMethod(method);
                        var parameters = root.TryGetProperty("params", out var paramsElement)
                            ? paramsElement.Clone()
                            : default;
                        if (root.TryGetProperty("id", out var incomingId))
                        {
                            var request = new JsonRpcRequestEventArgs(
                                incomingId.Clone(),
                                method,
                                parameters);
                            if (!TryStartIncomingRequest(request))
                            {
                                await WriteIncomingResponseAsync(
                                        request,
                                        JsonRpcResponse.Failure(
                                            -32000,
                                            "Too many pending client requests"),
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            RaiseNotification(new JsonRpcNotification(method, parameters));
                        }
                    }
                    continue;
                }

                if (!root.TryGetProperty("id", out var idElement) ||
                    !idElement.TryGetInt64(out var requestId) ||
                    !_pending.TryRemove(requestId, out var completion))
                {
                    continue;
                }

                if (root.TryGetProperty("result", out var result))
                {
                    completion.TrySetResult(result.Clone());
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var code = error.TryGetProperty("code", out var codeElement)
                        ? codeElement.GetInt32()
                        : -32603;
                    var messageText = error.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString() ?? "JSON-RPC error"
                        : "JSON-RPC error";
                    completion.TrySetException(new JsonRpcException(code, messageText));
                    continue;
                }

                completion.TrySetException(new InvalidDataException("The engine response did not contain a result."));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            terminalError = exception;
        }
        finally
        {
            var fault = terminalError;
            terminalError ??= new OperationCanceledException("The JSON-RPC connection stopped.");
            List<TaskCompletionSource<JsonElement>> pendingCompletions = [];
            lock (_stateLock)
            {
                _terminalError = terminalError;
                foreach (var requestId in _pending.Keys)
                {
                    if (_pending.TryRemove(requestId, out var completion))
                    {
                        pendingCompletions.Add(completion);
                    }
                }
            }

            foreach (var completion in pendingCompletions)
            {
                completion.TrySetException(terminalError);
            }

            if (fault is not null)
            {
                RaiseFaulted(new EngineFaultedEventArgs(fault));
            }
        }
    }

    private async Task<JsonDocument?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            var newline = buffer.PositionOf((byte)'\n');
            if (newline is { } newlinePosition)
            {
                var line = TrimTrailingCarriageReturn(buffer.Slice(0, newlinePosition));
                var consumed = buffer.GetPosition(1, newlinePosition);
                try
                {
                    EnsureMessageWithinLimit(line.Length);
                    return JsonDocument.Parse(line, DocumentOptions);
                }
                finally
                {
                    _reader.AdvanceTo(consumed);
                }
            }

            EnsureMessageWithinLimit(buffer.Length);
            if (result.IsCompleted)
            {
                try
                {
                    return buffer.IsEmpty
                        ? null
                        : JsonDocument.Parse(TrimTrailingCarriageReturn(buffer), DocumentOptions);
                }
                finally
                {
                    _reader.AdvanceTo(buffer.End);
                }
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static ReadOnlySequence<byte> TrimTrailingCarriageReturn(
        ReadOnlySequence<byte> line)
    {
        if (line.IsEmpty)
        {
            return line;
        }

        var lastByte = line.Slice(line.Length - 1, 1).FirstSpan[0];
        return lastByte == (byte)'\r' ? line.Slice(0, line.Length - 1) : line;
    }

    private void EnsureMessageWithinLimit(long length)
    {
        if (length > _maxIncomingMessageBytes)
        {
            throw new InvalidDataException(
                $"The engine JSON-RPC frame exceeded the maximum of {_maxIncomingMessageBytes} bytes.");
        }
    }

    private bool TryStartIncomingRequest(JsonRpcRequestEventArgs request)
    {
        if (Interlocked.Increment(ref _activeIncomingRequestCount) >
            MaxConcurrentIncomingRequests)
        {
            Interlocked.Decrement(ref _activeIncomingRequestCount);
            return false;
        }

        var trackingId = Interlocked.Increment(ref _nextIncomingRequestId);
        var task = HandleIncomingRequestAsync(request, _shutdown.Token);
        if (!_incomingRequests.TryAdd(trackingId, task))
        {
            Interlocked.Decrement(ref _activeIncomingRequestCount);
            throw new InvalidOperationException(
                $"Duplicate incoming JSON-RPC request tracking id {trackingId}.");
        }

        _ = task.ContinueWith(
            static (completedTask, state) =>
            {
                var tuple = ((NdjsonRpcConnection Connection, long TrackingId))state!;
                tuple.Connection._incomingRequests.TryRemove(tuple.TrackingId, out _);
                Interlocked.Decrement(ref tuple.Connection._activeIncomingRequestCount);
            },
            (this, trackingId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    private async Task HandleIncomingRequestAsync(
        JsonRpcRequestEventArgs request,
        CancellationToken cancellationToken)
    {
        JsonRpcResponse response;
        try
        {
            var subscriberFailed = false;
            var handlers = RequestReceived;
            if (handlers is not null)
            {
                foreach (EventHandler<JsonRpcRequestEventArgs> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(this, request);
                    }
                    catch (Exception)
                    {
                        subscriberFailed = true;
                    }
                }
            }

            var responseTask = request.InvokeHandlerAsync(cancellationToken);
            if (responseTask is null)
            {
                response = subscriberFailed
                    ? JsonRpcResponse.Failure(-32603, "Internal error")
                    : JsonRpcResponse.Failure(-32601, "Method not found");
            }
            else
            {
                response = await responseTask
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            response = JsonRpcResponse.Failure(-32603, "Internal error");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await WriteIncomingResponseAsync(request, response, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private Task WriteIncomingResponseAsync(
        JsonRpcRequestEventArgs request,
        JsonRpcResponse response,
        CancellationToken cancellationToken)
    {
        var line = response.Error is { } error
            ? JsonSerializer.Serialize(
                new
                {
                    jsonrpc = "2.0",
                    id = request.Id,
                    error = new { code = error.Code, message = error.Message },
                },
                SerializerOptions)
            : JsonSerializer.Serialize(
                new
                {
                    jsonrpc = "2.0",
                    id = request.Id,
                    result = response.Result,
                },
                SerializerOptions);
        return WriteLineAsync(line, cancellationToken);
    }

    private void RaiseNotification(JsonRpcNotification notification)
    {
        var handlers = NotificationReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<JsonRpcNotification> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, notification);
            }
            catch (Exception)
            {
                // A UI subscriber must not stop the shared protocol reader.
            }
        }
    }

    private void RaiseFaulted(EngineFaultedEventArgs args)
    {
        var handlers = Faulted;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<EngineFaultedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception)
            {
                // A subscriber must not interfere with terminal reader cleanup.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            await _readerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await Task.WhenAll(_incomingRequests.Values).ConfigureAwait(false);

        await _reader.CompleteAsync().ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _shutdown.Dispose();
    }
}
