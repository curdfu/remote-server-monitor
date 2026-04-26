using Microsoft.Extensions.Logging;
using Monitor.Contracts.Options;
using Monitor.Storage.Abstractions;

namespace Monitor.Storage.Services;

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
        await using var checkpointCommand = connection.CreateCommand();
        checkpointCommand.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await checkpointCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
