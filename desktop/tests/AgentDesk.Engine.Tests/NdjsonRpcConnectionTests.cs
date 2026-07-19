using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using AgentDesk.Core.Engine;
using AgentDesk.Engine.Transport;

namespace AgentDesk.Engine.Tests;

public sealed class NdjsonRpcConnectionTests
{
    [Fact]
    public async Task SendRequestAsync_WritesOneJsonLineAndMatchesTheResponse()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());

        var responseTask = connection.SendRequestAsync(
            "initialize",
            new { protocolVersion = "1" },
            timeout.Token);

        using var requestReader = new StreamReader(
            clientToServer.Reader.AsStream(),
            Encoding.UTF8,
            leaveOpen: true);
        var requestLine = await requestReader.ReadLineAsync(timeout.Token);

        Assert.NotNull(requestLine);
        using var request = JsonDocument.Parse(requestLine);
        Assert.Equal("2.0", request.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, request.RootElement.GetProperty("id").GetInt64());
        Assert.Equal("initialize", request.RootElement.GetProperty("method").GetString());
        Assert.Equal("1", request.RootElement.GetProperty("params").GetProperty("protocolVersion").GetString());

        await serverToClient.Writer.WriteAsync(
            Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"1\"}}\n"),
            timeout.Token);

        var result = await responseTask;

        Assert.Equal("1", result.GetProperty("protocolVersion").GetString());
    }

    [Theory]
    [InlineData("agentdesk/v1/credential", "_agentdesk/v1/credential")]
    [InlineData("x.ai/session/list", "_x.ai/session/list")]
    public async Task SendRequestAsync_PrefixesExtensionMethodsOnTheWire(
        string method,
        string expectedWireMethod)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());

        var responseTask = connection.SendRequestAsync(method, new { }, timeout.Token);
        using var requestReader = new StreamReader(
            clientToServer.Reader.AsStream(),
            Encoding.UTF8,
            leaveOpen: true);
        var requestLine = await requestReader.ReadLineAsync(timeout.Token);

        Assert.NotNull(requestLine);
        using var request = JsonDocument.Parse(requestLine);
        Assert.Equal(expectedWireMethod, request.RootElement.GetProperty("method").GetString());

        await serverToClient.Writer.WriteAsync(
            Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n"),
            timeout.Token);
        _ = await responseTask;
    }

    [Fact]
    public async Task SendRequestAsync_ThrowsJsonRpcExceptionForErrorResponse()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());

        var responseTask = connection.SendRequestAsync("session/load", new { }, timeout.Token);
        using var requestReader = new StreamReader(clientToServer.Reader.AsStream(), leaveOpen: true);
        _ = await requestReader.ReadLineAsync(timeout.Token);
        await serverToClient.Writer.WriteAsync(
            Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32001,\"message\":\"session missing\"}}\n"),
            timeout.Token);

        var exception = await Assert.ThrowsAsync<JsonRpcException>(() => responseTask);

        Assert.Equal(-32001, exception.Code);
        Assert.Equal("session missing", exception.Message);
    }

    [Fact]
    public async Task NotificationReceived_EmitsMethodAndClonedParameters()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());
        var received = new TaskCompletionSource<JsonRpcNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.NotificationReceived += (_, notification) => received.TrySetResult(notification);

        await serverToClient.Writer.WriteAsync(
            Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"abc\"}}\n"),
            timeout.Token);

        var notification = await received.Task.WaitAsync(timeout.Token);

        Assert.Equal("session/update", notification.Method);
        Assert.Equal("abc", notification.Parameters.GetProperty("sessionId").GetString());
    }

    [Theory]
    [InlineData("_agentdesk/v1/health", "agentdesk/v1/health")]
    [InlineData("_x.ai/session/update", "x.ai/session/update")]
    public async Task IncomingExtensionNotifications_RemoveTheWirePrefix(
        string wireMethod,
        string expectedMethod)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());
        var received = new TaskCompletionSource<JsonRpcNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.NotificationReceived += (_, notification) => received.TrySetResult(notification);

        await serverToClient.Writer.WriteAsync(
            Encoding.UTF8.GetBytes(
                $"{{\"jsonrpc\":\"2.0\",\"method\":\"{wireMethod}\",\"params\":{{}}}}\n"),
            timeout.Token);

        var notification = await received.Task.WaitAsync(timeout.Token);

        Assert.Equal(expectedMethod, notification.Method);
    }

    [Fact]
    public async Task SendNotificationAsync_WritesOneJsonLineWithoutARequestId()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());

        await connection.SendNotificationAsync(
            "session/cancel",
            new { sessionId = "session-1" },
            timeout.Token);

        using var requestReader = new StreamReader(
            clientToServer.Reader.AsStream(),
            Encoding.UTF8,
            leaveOpen: true);
        var requestLine = await requestReader.ReadLineAsync(timeout.Token);

        Assert.NotNull(requestLine);
        using var request = JsonDocument.Parse(requestLine);
        Assert.Equal("2.0", request.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("session/cancel", request.RootElement.GetProperty("method").GetString());
        Assert.Equal("session-1", request.RootElement.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.False(request.RootElement.TryGetProperty("id", out _));
    }

    [Theory]
    [InlineData("agentdesk/v1/health", "_agentdesk/v1/health")]
    [InlineData("x.ai/session/update", "_x.ai/session/update")]
    public async Task SendNotificationAsync_PrefixesExtensionMethodsOnTheWire(
        string method,
        string expectedWireMethod)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());

        await connection.SendNotificationAsync(method, new { }, timeout.Token);
        using var requestReader = new StreamReader(
            clientToServer.Reader.AsStream(),
            Encoding.UTF8,
            leaveOpen: true);
        var requestLine = await requestReader.ReadLineAsync(timeout.Token);

        Assert.NotNull(requestLine);
        using var request = JsonDocument.Parse(requestLine);
        Assert.Equal(expectedWireMethod, request.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task SendRequestAsync_AfterInputEndedFailsImmediatelyWithTheTerminalError()
    {
        await using var connection = new NdjsonRpcConnection(
            new MemoryStream(),
            new MemoryStream());

        var exception = await Assert.ThrowsAsync<EndOfStreamException>(
            () => connection.SendRequestAsync("initialize", new { })
                .WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal("The engine closed its JSON-RPC output.", exception.Message);
    }

    [Fact]
    public async Task InputEnded_RaisesFaultedExactlyOnce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());
        var faulted = new TaskCompletionSource<EngineFaultedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var faultCount = 0;
        connection.Faulted += (_, args) =>
        {
            Interlocked.Increment(ref faultCount);
            faulted.TrySetResult(args);
        };

        await serverToClient.Writer.CompleteAsync();

        var fault = await faulted.Task.WaitAsync(timeout.Token);
        Assert.IsType<EndOfStreamException>(fault.Exception);
        Assert.Equal(1, Volatile.Read(ref faultCount));
    }

    [Fact]
    public async Task Faulted_SubscriberFailureDoesNotPreventOtherSubscribers()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());
        var observed = new TaskCompletionSource<EngineFaultedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Faulted += (_, _) => throw new InvalidOperationException("subscriber failed");
        connection.Faulted += (_, args) => observed.TrySetResult(args);

        await serverToClient.Writer.CompleteAsync();

        var fault = await observed.Task.WaitAsync(timeout.Token);
        Assert.IsType<EndOfStreamException>(fault.Exception);
    }

    [Fact]
    public async Task MalformedJson_RaisesFaultedWithTheParserError()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());
        var faulted = new TaskCompletionSource<EngineFaultedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Faulted += (_, args) => faulted.TrySetResult(args);

        await serverToClient.Writer.WriteAsync(
            Encoding.UTF8.GetBytes("{\"jsonrpc\":}\n"),
            timeout.Token);

        var fault = await faulted.Task.WaitAsync(timeout.Token);
        Assert.IsAssignableFrom<JsonException>(fault.Exception);
    }

    [Fact]
    public async Task ContractSizedIncomingFrameAboveLegacyLimitIsAccepted()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());
        var responseTask = connection.SendRequestAsync("session/export", new { }, timeout.Token);
        using var requestReader = new StreamReader(
            clientToServer.Reader.AsStream(),
            Encoding.UTF8,
            leaveOpen: true);
        _ = await requestReader.ReadLineAsync(timeout.Token);
        var payload = new string('x', (4 * 1024 * 1024) + 1024);
        var response = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            result = new { payload },
        });

        await serverToClient.Writer.WriteAsync(
            Encoding.UTF8.GetBytes(response + "\n"),
            timeout.Token);

        var result = await responseTask;
        Assert.Equal(payload.Length, result.GetProperty("payload").GetString()!.Length);
    }

    [Fact]
    public async Task OversizedIncomingFrameTerminatesTheConnectionBeforeJsonParsing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        const int testMessageLimit = 1024;
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream(),
            testMessageLimit);
        var faulted = new TaskCompletionSource<EngineFaultedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Faulted += (_, args) => faulted.TrySetResult(args);
        var responseTask = connection.SendRequestAsync("initialize", new { }, timeout.Token);
        var oversizedFrame = new byte[testMessageLimit + 1];
        Array.Fill(oversizedFrame, (byte)'x');

        await serverToClient.Writer.WriteAsync(oversizedFrame, timeout.Token);
        await serverToClient.Writer.CompleteAsync();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => responseTask);
        Assert.Contains("maximum", exception.Message, StringComparison.OrdinalIgnoreCase);
        var fault = await faulted.Task.WaitAsync(timeout.Token);
        Assert.Same(exception, fault.Exception);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotRaiseFaulted()
    {
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());
        var faultCount = 0;
        connection.Faulted += (_, _) => Interlocked.Increment(ref faultCount);

        await connection.DisposeAsync();

        Assert.Equal(0, Volatile.Read(ref faultCount));
    }

    [Fact]
    public async Task ReverseRequest_WaitsForTheHandlerWithoutBlockingTheProtocolReader()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());
        var permissionResponse = new TaskCompletionSource<JsonRpcResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var permissionReceived = new TaskCompletionSource<JsonRpcRequestEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.RequestReceived += (_, request) =>
        {
            permissionReceived.TrySetResult(request);
            Assert.True(request.TryHandle(_ => permissionResponse.Task));
        };

        var initializeTask = connection.SendRequestAsync("initialize", new { }, timeout.Token);
        using var requestReader = new StreamReader(
            clientToServer.Reader.AsStream(),
            Encoding.UTF8,
            leaveOpen: true);
        _ = await requestReader.ReadLineAsync(timeout.Token);

        await serverToClient.Writer.WriteAsync(
            Encoding.UTF8.GetBytes(
                "{\"jsonrpc\":\"2.0\",\"id\":41,\"method\":\"session/request_permission\",\"params\":{\"sessionId\":\"s1\"}}\n" +
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":1}}\n"),
            timeout.Token);

        var request = await permissionReceived.Task.WaitAsync(timeout.Token);
        var initialize = await initializeTask.WaitAsync(timeout.Token);
        Assert.Equal("session/request_permission", request.Method);
        Assert.Equal(41, request.Id.GetInt64());
        Assert.Equal("s1", request.Parameters.GetProperty("sessionId").GetString());
        Assert.Equal(1, initialize.GetProperty("protocolVersion").GetInt32());
        Assert.False(permissionResponse.Task.IsCompleted);

        permissionResponse.SetResult(JsonRpcResponse.Success(new
        {
            outcome = new { outcome = "selected", optionId = "allow-once" },
        }));
        var responseLine = await requestReader.ReadLineAsync(timeout.Token);

        Assert.NotNull(responseLine);
        using var response = JsonDocument.Parse(responseLine);
        Assert.Equal(41, response.RootElement.GetProperty("id").GetInt64());
        Assert.Equal(
            "allow-once",
            response.RootElement.GetProperty("result")
                .GetProperty("outcome")
                .GetProperty("optionId")
                .GetString());
    }

    [Fact]
    public async Task ReverseRequest_HandlerCanReturnAJsonRpcError()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());
        connection.RequestReceived += (_, request) =>
            Assert.True(request.TryHandle(_ => Task.FromResult(
                JsonRpcResponse.Failure(-32602, "Invalid params"))));
        using var responseReader = new StreamReader(
            clientToServer.Reader.AsStream(),
            Encoding.UTF8,
            leaveOpen: true);

        await serverToClient.Writer.WriteAsync(
            Encoding.UTF8.GetBytes(
                "{\"jsonrpc\":\"2.0\",\"id\":\"permission-1\",\"method\":\"session/request_permission\",\"params\":{}}\n"),
            timeout.Token);
        var responseLine = await responseReader.ReadLineAsync(timeout.Token);

        Assert.NotNull(responseLine);
        using var response = JsonDocument.Parse(responseLine);
        Assert.Equal("permission-1", response.RootElement.GetProperty("id").GetString());
        Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(
            "Invalid params",
            response.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ReverseRequest_WithoutAHandlerFailsWithMethodNotFound()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());
        using var responseReader = new StreamReader(
            clientToServer.Reader.AsStream(),
            Encoding.UTF8,
            leaveOpen: true);

        await serverToClient.Writer.WriteAsync(
            Encoding.UTF8.GetBytes(
                "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"client/unknown\",\"params\":{}}\n"),
            timeout.Token);
        var responseLine = await responseReader.ReadLineAsync(timeout.Token);

        Assert.NotNull(responseLine);
        using var response = JsonDocument.Parse(responseLine);
        Assert.Equal(7, response.RootElement.GetProperty("id").GetInt64());
        Assert.Equal(-32601, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task DisposeAsync_CancelsAndWaitsForReverseRequestHandlers()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());
        var handlerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.RequestReceived += (_, request) =>
            Assert.True(request.TryHandle(async cancellationToken =>
            {
                handlerStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    handlerCancelled.TrySetResult();
                    throw;
                }

                return JsonRpcResponse.Success(new { });
            }));

        await serverToClient.Writer.WriteAsync(
            Encoding.UTF8.GetBytes(
                "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"session/request_permission\",\"params\":{}}\n"),
            timeout.Token);
        await handlerStarted.Task.WaitAsync(timeout.Token);

        await connection.DisposeAsync().AsTask().WaitAsync(timeout.Token);

        await handlerCancelled.Task.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ReverseRequest_FloodIsBoundedAndFailsClosedWhenCapacityIsExhausted()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());
        connection.RequestReceived += (_, request) =>
            Assert.True(request.TryHandle(async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return JsonRpcResponse.Success(new { });
            }));
        using var responseReader = new StreamReader(
            clientToServer.Reader.AsStream(),
            Encoding.UTF8,
            leaveOpen: true);

        var requests = new StringBuilder();
        for (var id = 1; id <= 65; id++)
        {
            requests.Append(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"session/request_permission\",\"params\":{{}}}}\n");
        }
        await serverToClient.Writer.WriteAsync(
            Encoding.UTF8.GetBytes(requests.ToString()),
            timeout.Token);

        var responseLine = await responseReader.ReadLineAsync(timeout.Token);

        Assert.NotNull(responseLine);
        using var response = JsonDocument.Parse(responseLine);
        Assert.Equal(65, response.RootElement.GetProperty("id").GetInt64());
        Assert.Equal(-32000, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ReverseRequest_ProjectsHandlersInNdjsonArrivalOrder()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        await using var connection = new NdjsonRpcConnection(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream());
        var firstHandlerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedIds = new List<long>();
        connection.RequestReceived += (_, request) =>
        {
            var id = request.Id.GetInt64();
            if (id == 1)
            {
                firstHandlerEntered.TrySetResult();
                Thread.Sleep(250);
            }

            lock (receivedIds)
            {
                receivedIds.Add(id);
                if (receivedIds.Count == 2)
                {
                    allReceived.TrySetResult();
                }
            }
            Assert.True(request.TryHandle(_ => Task.FromResult(
                JsonRpcResponse.Success(new { }))));
        };

        await serverToClient.Writer.WriteAsync(
            Encoding.UTF8.GetBytes(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"session/request_permission\",\"params\":{}}\n"),
            timeout.Token);
        await firstHandlerEntered.Task.WaitAsync(timeout.Token);
        await serverToClient.Writer.WriteAsync(
            Encoding.UTF8.GetBytes(
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/request_permission\",\"params\":{}}\n"),
            timeout.Token);
        await allReceived.Task.WaitAsync(timeout.Token);

        Assert.Equal([1L, 2L], receivedIds);
    }
}
