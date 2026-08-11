using Microsoft.Extensions.Logging;
using Monitor.Contracts.Dtos;
using Monitor.Contracts.Options;
using Monitor.Storage.Abstractions;
using Monitor.Storage.Services;

namespace Monitor.Storage.Repositories;

// SettingsRepository 负责运行时设置的 SQLite 持久化；启动前读取使用 PersistedSettingsLoader，运行后读写走这里。
// settings 表固定 id=1，保存时使用 upsert，保证首次初始化和后续更新共用同一条记录。
public sealed class SettingsRepository(
    IDbConnectionFactory dbConnectionFactory,
    ILogger<SettingsRepository> logger)
{
    public async Task<AppSettingsDto?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        // 只读取当前前端可编辑且会影响运行时行为的设置字段。
        command.CommandText = """
                              SELECT http_port,
                                     hardware_sample_interval_ms,
                                     network_sample_interval_ms,
                                     aggregate_interval_seconds,
                                     history_retention_days,
                                     top_n_default
                              FROM settings
                              WHERE id = 1
                              LIMIT 1;
                              """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AppSettingsDto
        {
            HttpPort = reader.GetInt32(0),
            HardwareSampleIntervalMs = reader.GetInt32(1),
            NetworkProcessingIntervalMs = reader.GetInt32(2),
            AggregateIntervalSeconds = reader.GetInt32(3),
            HistoryRetentionDays = reader.GetInt32(4),
            TopNDefault = reader.GetInt32(5)
        };
    }

    public Task SaveAsync(AppSettingsDto settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return SqliteBusyRetry.ExecuteAsync(
            token => SaveCoreAsync(settings, token),
            cancellationToken);
    }

    private async Task SaveCoreAsync(AppSettingsDto settings, CancellationToken cancellationToken)
    {
        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        // network_sample_interval_ms 是历史兼容字段，现在用于网络事件追平和持久化检查节奏。
        // 它与历史 bucket 粒度分别保存，避免把处理频率和统计粒度混为一谈。
        command.CommandText = """
                              INSERT INTO settings (
                                  id,
                                  http_port,
                                  hardware_sample_interval_ms,
                                  network_sample_interval_ms,
                                  aggregate_interval_seconds,
                                  history_retention_days,
                                  top_n_default,
                                  created_at,
                                  updated_at
                              )
                              VALUES (
                                  1,
                                  $httpPort,
                                  $hardwareSampleIntervalMs,
                                  $networkProcessingIntervalMs,
                                  $aggregateIntervalSeconds,
                                  $historyRetentionDays,
                                  $topNDefault,
                                  $createdAt,
                                  $updatedAt
                              )
                              ON CONFLICT(id) DO UPDATE SET
                                  http_port = excluded.http_port,
                                  hardware_sample_interval_ms = excluded.hardware_sample_interval_ms,
                                  network_sample_interval_ms = excluded.network_sample_interval_ms,
                                  aggregate_interval_seconds = excluded.aggregate_interval_seconds,
                                  history_retention_days = excluded.history_retention_days,
                                  top_n_default = excluded.top_n_default,
                                  updated_at = excluded.updated_at;
                              """;

        var now = DateTimeOffset.UtcNow.ToString("O");
        command.Parameters.AddWithValue("$httpPort", settings.HttpPort);
        command.Parameters.AddWithValue("$hardwareSampleIntervalMs", settings.HardwareSampleIntervalMs);
        command.Parameters.AddWithValue("$networkProcessingIntervalMs", settings.NetworkProcessingIntervalMs);
        command.Parameters.AddWithValue("$aggregateIntervalSeconds", settings.AggregateIntervalSeconds);
        command.Parameters.AddWithValue("$historyRetentionDays", settings.HistoryRetentionDays);
        command.Parameters.AddWithValue("$topNDefault", settings.TopNDefault);
        command.Parameters.AddWithValue("$createdAt", now);
        command.Parameters.AddWithValue("$updatedAt", now);

        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation(
            "Settings persisted to SQLite. HttpPort={HttpPort}, HardwareIntervalMs={HardwareIntervalMs}, NetworkProcessingIntervalMs={NetworkProcessingIntervalMs}, AggregateIntervalSeconds={AggregateIntervalSeconds}, HistoryRetentionDays={HistoryRetentionDays}, TopNDefault={TopNDefault}",
            settings.HttpPort,
            settings.HardwareSampleIntervalMs,
            settings.NetworkProcessingIntervalMs,
            settings.AggregateIntervalSeconds,
            settings.HistoryRetentionDays,
            settings.TopNDefault);
    }
}
