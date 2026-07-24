using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Monitor.Contracts.Options;
using Monitor.Storage.Abstractions;

namespace Monitor.Storage.Services;

// 保留策略按小批次删除过期历史，避免在大数据库上用单个事务长期占用 SQLite 唯一写锁。
// 清理顺序保留引用完整性：先删事实表和 rollup，再删除已经没有任何流量引用的 app_registry 记录。
public sealed class RetentionService(
    IDbConnectionFactory dbConnectionFactory,
    IMonitorSettingsProvider settingsMonitor,
    ILogger<RetentionService> logger)
{
    private const int DeleteBatchSize = 5_000;
    private const long ProgressLogInterval = 100_000;
    private static readonly TimeSpan BatchYieldDelay = TimeSpan.FromMilliseconds(100);

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        var retentionDays = settingsMonitor.Current.HistoryRetentionDays;
        if (retentionDays <= 0)
        {
            logger.LogInformation("Retention cleanup skipped because HistoryRetentionDays is {RetentionDays}.", retentionDays);
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        logger.LogInformation(
            "Retention cleanup started. Cutoff={Cutoff}, BatchSize={BatchSize}.",
            cutoff,
            DeleteBatchSize);

        var deletedHardwareSamples = await DeleteInBatchesAsync(
            "hardware_samples",
            """
            DELETE FROM hardware_samples
            WHERE id IN (
                SELECT id
                FROM hardware_samples
                WHERE sample_time < $cutoff
                ORDER BY sample_time
                LIMIT $batchSize
            );
            """,
            cutoff,
            retentionDays,
            cancellationToken);

        var deletedNetworkBuckets = await DeleteInBatchesAsync(
            "network_usage_agg",
            """
            DELETE FROM network_usage_agg
            WHERE id IN (
                SELECT id
                FROM network_usage_agg
                WHERE bucket_start_time < $cutoff
                ORDER BY bucket_start_time
                LIMIT $batchSize
            );
            """,
            cutoff,
            retentionDays,
            cancellationToken);

        var deletedNetworkRollupRows = await DeleteInBatchesAsync(
            "network_usage_rollup_12h",
            """
            DELETE FROM network_usage_rollup_12h
            WHERE rowid IN (
                SELECT rowid
                FROM network_usage_rollup_12h
                WHERE window_start_time < $cutoff
                ORDER BY window_start_time
                LIMIT $batchSize
            );
            """,
            cutoff,
            retentionDays,
            cancellationToken);

        var deletedNetworkRollupWindows = await DeleteInBatchesAsync(
            "network_usage_rollup_12h_windows",
            """
            DELETE FROM network_usage_rollup_12h_windows
            WHERE window_start_time IN (
                SELECT window_start_time
                FROM network_usage_rollup_12h_windows
                WHERE window_start_time < $cutoff
                ORDER BY window_start_time
                LIMIT $batchSize
            );
            """,
            cutoff,
            retentionDays,
            cancellationToken);

        // app_registry 只删除没有 raw bucket 和 rollup 引用的陈旧应用，避免历史查询丢失进程显示信息。
        var deletedAppRegistryEntries = await DeleteInBatchesAsync(
            "app_registry",
            """
            DELETE FROM app_registry
            WHERE id IN (
                SELECT candidate.id
                FROM app_registry AS candidate
                WHERE candidate.last_seen_at < $cutoff
                  AND NOT EXISTS (
                      SELECT 1
                      FROM network_usage_agg AS network
                      WHERE network.app_id = candidate.id
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM network_usage_rollup_12h AS rollup
                      WHERE rollup.app_id = candidate.id
                  )
                ORDER BY candidate.last_seen_at
                LIMIT $batchSize
            );
            """,
            cutoff,
            retentionDays,
            cancellationToken);

        var totalDeleted = deletedHardwareSamples
                           + deletedNetworkBuckets
                           + deletedNetworkRollupRows
                           + deletedNetworkRollupWindows
                           + deletedAppRegistryEntries;
        if (totalDeleted > 0)
        {
            await TryPassiveCheckpointAsync(cancellationToken);
        }

        logger.LogInformation(
            "Retention cleanup finished. Cutoff={Cutoff}, hardwareDeleted={HardwareDeleted}, networkDeleted={NetworkDeleted}, networkRollupRowsDeleted={NetworkRollupRowsDeleted}, networkRollupWindowsDeleted={NetworkRollupWindowsDeleted}, appRegistryDeleted={AppRegistryDeleted}.",
            cutoff,
            deletedHardwareSamples,
            deletedNetworkBuckets,
            deletedNetworkRollupRows,
            deletedNetworkRollupWindows,
            deletedAppRegistryEntries);
    }

    private async Task<long> DeleteInBatchesAsync(
        string tableName,
        string sql,
        DateTimeOffset cutoff,
        int retentionDaysAtStart,
        CancellationToken cancellationToken)
    {
        long totalDeleted = 0;
        long nextProgressLog = ProgressLogInterval;

        while (ShouldContinueCleanup(retentionDaysAtStart))
        {
            var deleted = await SqliteBusyRetry.ExecuteAsync(
                async token =>
                {
                    await using var connection = dbConnectionFactory.CreateConnection();
                    await connection.OpenAsync(token);
                    await using var transactionHandle = await connection.BeginTransactionAsync(token);
                    var transaction = (SqliteTransaction)transactionHandle;

                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("$cutoff", cutoff.ToUniversalTime().ToString("O"));
                    command.Parameters.AddWithValue("$batchSize", DeleteBatchSize);
                    var affectedRows = await command.ExecuteNonQueryAsync(token);
                    await transaction.CommitAsync(token);
                    return affectedRows;
                },
                cancellationToken);

            totalDeleted += deleted;
            if (totalDeleted >= nextProgressLog)
            {
                logger.LogInformation(
                    "Retention cleanup progress. Table={TableName}, Deleted={Deleted}.",
                    tableName,
                    totalDeleted);
                nextProgressLog = totalDeleted + ProgressLogInterval;
            }

            if (deleted < DeleteBatchSize)
            {
                return totalDeleted;
            }

            // 每批提交后显式让出时间片，让硬件和网络实时写入有机会获得写锁。
            await Task.Delay(BatchYieldDelay, cancellationToken);
        }

        logger.LogInformation(
            "Retention cleanup paused because HistoryRetentionDays was increased or disabled. Table={TableName}, Deleted={Deleted}.",
            tableName,
            totalDeleted);
        return totalDeleted;
    }

    private bool ShouldContinueCleanup(int retentionDaysAtStart)
    {
        var currentRetentionDays = settingsMonitor.Current.HistoryRetentionDays;
        return currentRetentionDays > 0 && currentRetentionDays <= retentionDaysAtStart;
    }

    private async Task TryPassiveCheckpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = dbConnectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            // PASSIVE 不等待其他连接释放读写锁，也不截断 WAL；失败时交给 SQLite 自动 checkpoint。
            command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                logger.LogInformation(
                    "Passive WAL checkpoint finished. Busy={Busy}, LogFrames={LogFrames}, CheckpointedFrames={CheckpointedFrames}.",
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2));
            }
        }
        catch (SqliteException exception) when (SqliteBusyRetry.IsBusy(exception))
        {
            logger.LogDebug(exception, "Passive WAL checkpoint skipped because SQLite is busy.");
        }
    }
}
