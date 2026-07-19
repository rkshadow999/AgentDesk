using AgentDesk.Core.Engine;

namespace AgentDesk.ProviderSmoke.Tests;

public sealed class ProviderSmokeCancellationProbeTests
{
    [Fact]
    public async Task CancelPromptStartsImmediatelyWithoutWaitingForStreaming()
    {
        var promptCompleted = new TaskCompletionSource<PromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelCalled = false;

        var result = await global::ProviderSmoke.CancelPromptAsync(
            promptCompleted.Task,
            _ =>
            {
                cancelCalled = true;
                promptCompleted.TrySetResult(
                    new PromptResult(EngineStopReason.Cancelled, "cancelled"));
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(cancelCalled);
        Assert.Equal(EngineStopReason.Cancelled, result.StopReason);
    }

    [Theory]
    [InlineData(
        "The cancellation prompt completed before cancellation could be exercised (stop reason: EndTurn).",
        "cancellation_completed_early")]
    [InlineData(
        "The cancellation prompt did not finish after cancellation was requested.",
        "cancellation_completion_timeout")]
    [InlineData(
        "The real prompt did not acknowledge cancellation.",
        "cancellation_not_acknowledged")]
    [InlineData(
        "The cancellation prompt produced no streamed model event before timeout.",
        "cancellation_stream_timeout")]
    public void FailureCodeClassifiesCancellationWithoutExposingExceptionText(
        string message,
        string expected)
    {
        var actual = global::ProviderSmoke.FailureCode(
            "cancellation",
            new InvalidDataException(message));

        Assert.Equal(expected, actual);
        Assert.DoesNotContain(message, actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObserveCancellationWaitsForStreamingBeforeCancelling()
    {
        var streamStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var promptCompleted = new TaskCompletionSource<PromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelCalled = false;

        var probe = global::ProviderSmoke.ObserveCancellationAsync(
            promptCompleted.Task,
            streamStarted.Task,
            _ =>
            {
                Assert.True(streamStarted.Task.IsCompletedSuccessfully);
                cancelCalled = true;
                promptCompleted.TrySetResult(
                    new PromptResult(EngineStopReason.Cancelled, "cancelled"));
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        await Task.Yield();
        Assert.False(cancelCalled);
        streamStarted.TrySetResult();

        var result = await probe;

        Assert.True(cancelCalled);
        Assert.Equal(EngineStopReason.Cancelled, result.StopReason);
    }

    [Fact]
    public async Task ObserveCancellationRejectsPromptThatCompletesBeforeStreaming()
    {
        var promptCompleted = Task.FromResult(
            new PromptResult(EngineStopReason.EndTurn, "end_turn"));
        var streamStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelCalled = false;

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            global::ProviderSmoke.ObserveCancellationAsync(
                promptCompleted,
                streamStarted.Task,
                _ =>
                {
                    cancelCalled = true;
                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2),
                CancellationToken.None));

        Assert.Contains("completed before cancellation", exception.Message, StringComparison.Ordinal);
        Assert.False(cancelCalled);
    }

    [Fact]
    public async Task ObserveCancellationRejectsNonCancelledStopReason()
    {
        var promptCompleted = new TaskCompletionSource<PromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            global::ProviderSmoke.ObserveCancellationAsync(
                promptCompleted.Task,
                Task.CompletedTask,
                _ =>
                {
                    promptCompleted.TrySetResult(
                        new PromptResult(EngineStopReason.EndTurn, "end_turn"));
                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2),
                CancellationToken.None));

        Assert.Contains("did not acknowledge cancellation", exception.Message, StringComparison.Ordinal);
    }
}
