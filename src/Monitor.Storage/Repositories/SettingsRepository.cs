using Microsoft.Extensions.Logging;
using Monitor.Contracts.Dtos;
using Monitor.Storage.Abstractions;

namespace Monitor.Storage.Repositories;

public sealed class SettingsRepository(
    IDbConnectionFactory dbConnectionFactory,
    ILogger<SettingsRepository> logger)
{
    public async Task<AppSettingsDto?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
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
            NetworkSampleIntervalMs = reader.GetInt32(2),
            AggregateIntervalSeconds = reader.GetInt32(3),
            HistoryRetentionDays = reader.GetInt32(4),
            TopNDefault = reader.GetInt32(5)
        };
    }

    public async Task SaveAsync(AppSettingsDto settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
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
                                  $networkSampleIntervalMs,
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
        command.Parameters.AddWithValue("$networkSampleIntervalMs", settings.NetworkSampleIntervalMs);
        command.Parameters.AddWithValue("$aggregateIntervalSeconds", settings.AggregateIntervalSeconds);
        command.Parameters.AddWithValue("$historyRetentionDays", settings.HistoryRetentionDays);
        command.Parameters.AddWithValue("$topNDefault", settings.TopNDefault);
        command.Parameters.AddWithValue("$createdAt", now);
        command.Parameters.AddWithValue("$updatedAt", now);

        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Settings persisted to SQLite.");
    }
}
