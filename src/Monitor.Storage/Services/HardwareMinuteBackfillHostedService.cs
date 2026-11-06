using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monitor.Storage.Repositories;

namespace Monitor.Storage.Services;

public sealed class HardwareMinuteBackfillHostedService(
    HardwareRepository repository, ILogger<HardwareMinuteBackfillHostedService> logger) : BackgroundService
{
    private const int MaxMinutesPerCycle = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var count = 0;
                try
                {
                    count = await repository.BackfillMinutesAsync(MaxMinutesPerCycle, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Hardware minute backfill failed; it will be retried.");
                }
                await Task.Delay(count == MaxMinutesPerCycle ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
