using Microsoft.Extensions.Options;

namespace AgentDesk.Cloud;

internal sealed class AutomationWorker(
    CloudStore cloudStore,
    CloudNotifier notifier,
    IOptions<CloudOptions> options,
    TimeProvider timeProvider,
    ILogger<AutomationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.AutomationPollingIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var queued = await cloudStore.RunDueAutomationsAsync(
                    timeProvider.GetUtcNow(),
                    stoppingToken).ConfigureAwait(false);
                foreach (var (teamId, jobId) in queued)
                {
                    await notifier.JobChangedAsync(
                        teamId,
                        jobId,
                        stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The automation scheduler iteration failed.");
            }

            await Task.Delay(interval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}
