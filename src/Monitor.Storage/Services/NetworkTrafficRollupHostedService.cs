using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monitor.Storage.Repositories;

namespace Monitor.Storage.Services;

// Rollup 服务把已经完整结束的原始网络 bucket 聚合成 12 小时窗口，加速长时间范围的应用排行和汇总查询。
// 它不处理当前正在写入的窗口，避免实时数据和历史 rollup 之间出现重复或缺口。
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
            // 启动后延迟一小段时间，让数据库初始化、采集器和首批 raw bucket 先稳定下来。
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

                // 如果本轮达到处理上限，说明仍有历史窗口待追赶；缩短下一轮间隔直到追平。
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
                // 失败后使用单独的退避间隔，避免故障时每 5 秒持续打数据库。
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
