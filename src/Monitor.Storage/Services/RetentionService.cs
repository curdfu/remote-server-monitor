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

        var deletedHardwareSamples = await DeleteHardwareInBatchesAsync(cutoff, retentionDays, cancellationToken);
        if (!ShouldContinueCleanup(retentionDays)) return;

        var deletedNetworkBuckets = await DeleteNetworkInBatchesAsync(cutoff, retentionDays, cancellationToken);
        if (!ShouldContinueCleanup(retentionDays)) return;

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

        var totalDeleted = deletedHardwareSamples + deletedNetworkBuckets + deletedAppRegistryEntries;
        if (totalDeleted > 0)
        {
            await TryPassiveCheckpointAsync(cancellationToken);
        }

        logger.LogInformation(
            "Retention cleanup finished. Cutoff={Cutoff}, hardwareDeleted={HardwareDeleted}, networkDeleted={NetworkDeleted}, appRegistryDeleted={AppRegistryDeleted}.",
            cutoff, deletedHardwareSamples, deletedNetworkBuckets, deletedAppRegistryEntries);
    }

    private async Task<long> DeleteHardwareInBatchesAsync(DateTimeOffset cutoff, int retentionDays, CancellationToken cancellationToken)
    {
        long total = 0;
        while (ShouldContinueCleanup(retentionDays))
        {
            var deleted = await SqliteBusyRetry.ExecuteAsync(async token =>
            {
                await using var connection = await dbConnectionFactory.OpenConnectionAsync(token);
                await using var transaction = connection.BeginTransaction();
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE TEMP TABLE IF NOT EXISTS retention_minutes (minute_start TEXT PRIMARY KEY);
                    DELETE FROM retention_minutes;
                    INSERT OR IGNORE INTO retention_minutes
                    SELECT substr(sample_time, 1, 16) || ':00.0000000+00:00' FROM hardware_samples
                    WHERE id IN (SELECT id FROM hardware_samples WHERE sample_time < $cutoff ORDER BY sample_time LIMIT $batchSize);
                    DELETE FROM hardware_minute_candidates WHERE minute_start IN (SELECT minute_start FROM retention_minutes);
                    DELETE FROM hardware_minute_windows WHERE minute_start IN (SELECT minute_start FROM retention_minutes);
                    DELETE FROM hardware_minute_anchor_windows WHERE minute_start IN (SELECT minute_start FROM retention_minutes);
                    DELETE FROM hardware_samples WHERE id IN (
                        SELECT id FROM hardware_samples WHERE sample_time < $cutoff ORDER BY sample_time LIMIT $batchSize
                    );
                    """;
                command.Parameters.AddWithValue("$cutoff", cutoff.ToUniversalTime().ToString("O"));
                command.Parameters.AddWithValue("$batchSize", DeleteBatchSize);
                await command.ExecuteNonQueryAsync(token);
                await using var count = connection.CreateCommand();
                count.Transaction = transaction;
                count.CommandText = "SELECT changes();";
                var rows = Convert.ToInt32(await count.ExecuteScalarAsync(token));
                await transaction.CommitAsync(token);
                return rows;
            }, cancellationToken);
            total += deleted;
            if (deleted < DeleteBatchSize) break;
            await Task.Delay(BatchYieldDelay, cancellationToken);
        }
        return total;
    }

    private async Task<long> DeleteNetworkInBatchesAsync(DateTimeOffset cutoff, int retentionDays, CancellationToken cancellationToken)
    {
        long total = 0;
        while (ShouldContinueCleanup(retentionDays))
        {
            var deleted = await SqliteBusyRetry.ExecuteAsync(async token =>
            {
                await using var connection = await dbConnectionFactory.OpenConnectionAsync(token);
                await using var transaction = connection.BeginTransaction();
                // Invalidate whole affected windows and advance the permanent completeness floor
                // in the same transaction as the first deletion from each window.
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE TEMP TABLE IF NOT EXISTS retention_windows (window_start TEXT PRIMARY KEY);
                    DELETE FROM retention_windows;
                    INSERT OR IGNORE INTO retention_windows
                    SELECT strftime('%Y-%m-%dT', bucket_start_time) ||
                           CASE WHEN CAST(substr(bucket_start_time, 12, 2) AS INTEGER) < 12 THEN '00' ELSE '12' END ||
                           ':00:00.0000000+00:00'
                    FROM network_usage_agg WHERE id IN (
                        SELECT id FROM network_usage_agg WHERE bucket_start_time < $cutoff ORDER BY bucket_start_time LIMIT $batchSize
                    );
                    INSERT INTO storage_progress(name, value) VALUES ('network_retention_cutoff', $cutoff)
                    ON CONFLICT(name) DO UPDATE SET value = MAX(value, excluded.value);
                    DELETE FROM network_usage_rollup_12h WHERE window_start_time IN (SELECT window_start FROM retention_windows);
                    DELETE FROM network_usage_rollup_12h_windows WHERE window_start_time IN (SELECT window_start FROM retention_windows);
                    DELETE FROM network_usage_agg WHERE id IN (
                        SELECT id FROM network_usage_agg WHERE bucket_start_time < $cutoff ORDER BY bucket_start_time LIMIT $batchSize
                    );
                    -- Windows entirely before the cut-off may have lost their raw rows in
                    -- an older release. Retire rollup and marker together; keep the cut
                    -- window if no raw was deleted, since its retained portion is unknowable.
                    DELETE FROM network_usage_rollup_12h WHERE window_start_time IN (
                        SELECT window_start_time FROM network_usage_rollup_12h_windows
                        WHERE window_end_time <= $cutoff ORDER BY window_start_time LIMIT $batchSize
                    );
                    DELETE FROM network_usage_rollup_12h_windows WHERE window_start_time IN (
                        SELECT window_start_time FROM network_usage_rollup_12h_windows
                        WHERE window_end_time <= $cutoff ORDER BY window_start_time LIMIT $batchSize
                    );
                    DELETE FROM network_usage_rollup_12h WHERE rowid IN (
                        SELECT r.rowid FROM network_usage_rollup_12h r
                        WHERE r.window_start_time < $cutoff
                          AND NOT EXISTS (SELECT 1 FROM network_usage_rollup_12h_windows w WHERE w.window_start_time = r.window_start_time)
                        ORDER BY r.window_start_time LIMIT $batchSize
                    );
                    """;
                command.Parameters.AddWithValue("$cutoff", cutoff.ToUniversalTime().ToString("O"));
                command.Parameters.AddWithValue("$batchSize", DeleteBatchSize);
                await command.ExecuteNonQueryAsync(token);
                await using var countCommand = connection.CreateCommand();
                countCommand.Transaction = transaction;
                countCommand.CommandText = """
                    SELECT changes() + (SELECT COUNT(*) FROM retention_windows)
                        + EXISTS(SELECT 1 FROM network_usage_agg WHERE bucket_start_time < $cutoff)
                        + EXISTS(SELECT 1 FROM network_usage_rollup_12h_windows WHERE window_end_time <= $cutoff)
                        + EXISTS(SELECT 1 FROM network_usage_rollup_12h r WHERE r.window_start_time < $cutoff
                            AND NOT EXISTS (SELECT 1 FROM network_usage_rollup_12h_windows w WHERE w.window_start_time = r.window_start_time));
                    """;
                countCommand.Parameters.AddWithValue("$cutoff", cutoff.ToUniversalTime().ToString("O"));
                var count = Convert.ToInt32(await countCommand.ExecuteScalarAsync(token));
                await transaction.CommitAsync(token);
                return count;
            }, cancellationToken);
            total += deleted;
            if (deleted == 0) break;
            await Task.Delay(BatchYieldDelay, cancellationToken);
        }
        return total;
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
                    await using var connection = await dbConnectionFactory.OpenConnectionAsync(token);
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

            // 每批提交后显式让出时间片，让硬件与网络聚合写入有机会获得写锁。
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
            await using var connection = await dbConnectionFactory.OpenConnectionAsync(cancellationToken);
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
