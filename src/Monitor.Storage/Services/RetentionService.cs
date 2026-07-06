using Microsoft.Extensions.Logging;
using Monitor.Contracts.Options;
using Monitor.Storage.Abstractions;

namespace Monitor.Storage.Services;

// 保留策略负责删除超过 HistoryRetentionDays 的历史数据，并在确实删除后回收 WAL 空间。
// 清理顺序需要保留引用完整性：先删事实表和 rollup，再删除已经没有任何流量引用的 app_registry 记录。
public sealed class RetentionService(
    IDbConnectionFactory dbConnectionFactory,
    IMonitorSettingsProvider settingsMonitor,
    ILogger<RetentionService> logger)
{
    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        var retentionDays = settingsMonitor.Current.HistoryRetentionDays;
        if (retentionDays <= 0)
        {
            logger.LogInformation("Retention cleanup skipped because HistoryRetentionDays is {RetentionDays}.", retentionDays);
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);

        // 所有删除放在同一个事务里，避免只清掉部分表后留下 rollup、raw bucket 和 app_registry 不一致的状态。
        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transactionHandle = await connection.BeginTransactionAsync(cancellationToken);
        var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transactionHandle;

        var deletedHardwareSamples = await ExecuteDeleteAsync(
            connection,
            transaction,
            """
            DELETE FROM hardware_samples
            WHERE sample_time < $cutoff;
            """,
            cutoff,
            cancellationToken);

        var deletedNetworkBuckets = await ExecuteDeleteAsync(
            connection,
            transaction,
            """
            DELETE FROM network_usage_agg
            WHERE bucket_start_time < $cutoff;
            """,
            cutoff,
            cancellationToken);

        var deletedNetworkRollupRows = await ExecuteDeleteAsync(
            connection,
            transaction,
            """
            DELETE FROM network_usage_rollup_12h
            WHERE window_start_time < $cutoff;
            """,
            cutoff,
            cancellationToken);

        var deletedNetworkRollupWindows = await ExecuteDeleteAsync(
            connection,
            transaction,
            """
            DELETE FROM network_usage_rollup_12h_windows
            WHERE window_start_time < $cutoff;
            """,
            cutoff,
            cancellationToken);

        // app_registry 只删除没有 raw bucket 和 rollup 引用的陈旧应用，避免历史查询丢失进程显示信息。
        var deletedAppRegistryEntries = await ExecuteDeleteAsync(
            connection,
            transaction,
            """
            DELETE FROM app_registry
            WHERE last_seen_at < $cutoff
              AND NOT EXISTS (
                  SELECT 1
                  FROM network_usage_agg n
                  WHERE n.app_id = app_registry.id
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM network_usage_rollup_12h r
                  WHERE r.app_id = app_registry.id
              );
            """,
            cutoff,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var totalDeleted = deletedHardwareSamples
                           + deletedNetworkBuckets
                           + deletedNetworkRollupRows
                           + deletedNetworkRollupWindows
                           + deletedAppRegistryEntries;
        if (totalDeleted > 0)
        {
            // SQLite/WAL 删除行后不会立即缩小文件；只有发生实际删除时才做 checkpoint，避免空跑增加 I/O。
            await ReclaimSpaceAsync(connection, cancellationToken);
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

    private static async Task<int> ExecuteDeleteAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string sql,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$cutoff", cutoff.ToUniversalTime().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReclaimSpaceAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // TRUNCATE checkpoint 会把 WAL 内容合并回主库并截断 WAL 文件；这里不执行 VACUUM，避免长时间锁库。
        await using var checkpointCommand = connection.CreateCommand();
        checkpointCommand.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await checkpointCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
