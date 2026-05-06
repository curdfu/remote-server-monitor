using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monitor.Storage.Repositories;

namespace Monitor.Storage.Services;

public sealed class NetworkTrafficRollupHostedService(
    NetworkTrafficRepository networkTrafficRepository,
    ILogger<NetworkTrafficRollupHostedService> logger) : BackgroundService
{
    private const int MaxWindowsPerCycle = 4;
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CatchUpDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FailureDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        logger.LogInformation(
            "Network traffic rollup background service started. MaxWindowsPerCycle={MaxWindowsPerCycle}, CatchUpDelay={CatchUpDelay}, IdleDelay={IdleDelay}.",
            MaxWindowsPerCycle,
            CatchUpDelay,
            IdleDelay);

        while (!stoppingToken.IsCancellationRequested)
        {
            var nextDelay = IdleDelay;
            try
            {
                var processedWindows = await networkTrafficRepository.RollupCompletedWindowsAsync(
                    MaxWindowsPerCycle,
                    stoppingToken);

                nextDelay = processedWindows >= MaxWindowsPerCycle ? CatchUpDelay : IdleDelay;
                if (processedWindows > 0)
                {
                    logger.LogInformation(
                        "Network traffic rollup cycle finished. ProcessedWindows={ProcessedWindows}, NextDelay={NextDelay}.",
                        processedWindows,
                        nextDelay);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Network traffic rollup cycle failed. The service will retry on the next scheduled cycle.");
                nextDelay = FailureDelay;
            }

            try
            {
                await Task.Delay(nextDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
