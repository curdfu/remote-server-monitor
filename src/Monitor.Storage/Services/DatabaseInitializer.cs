using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitor.Contracts.Options;
using Monitor.Storage.Abstractions;

namespace Monitor.Storage.Services;

public sealed class DatabaseInitializer(
    IDbConnectionFactory dbConnectionFactory,
    IOptionsMonitor<MonitorSettings> settingsMonitor,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await EnablePragmasAsync(connection, cancellationToken);
        await CreateSchemaAsync(connection, cancellationToken);
        await SeedSettingsAsync(connection, settingsMonitor.CurrentValue, cancellationToken);

        logger.LogInformation("SQLite database initialized. Path: {DatabasePath}", dbConnectionFactory.DatabasePath);
    }

    private static async Task EnablePragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var pragmaSql = """
                        PRAGMA journal_mode = WAL;
                        PRAGMA synchronous = NORMAL;
                        PRAGMA foreign_keys = ON;
                        """;

        await using var command = connection.CreateCommand();
        command.CommandText = pragmaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var schemaSql = """
                        CREATE TABLE IF NOT EXISTS settings (
                            id INTEGER PRIMARY KEY CHECK (id = 1),
                            http_port INTEGER NOT NULL,
                            hardware_sample_interval_ms INTEGER NOT NULL,
                            network_sample_interval_ms INTEGER NOT NULL,
                            aggregate_interval_seconds INTEGER NOT NULL,
                            history_retention_days INTEGER NOT NULL,
                            top_n_default INTEGER NOT NULL,
                            created_at TEXT NOT NULL,
                            updated_at TEXT NOT NULL
                        );

                        CREATE TABLE IF NOT EXISTS hardware_samples (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            sample_time TEXT NOT NULL,
                            cpu_usage_percent REAL,
                            cpu_temperature_c REAL,
                            cpu_frequency_mhz REAL,
                            memory_total_mb REAL,
                            memory_used_mb REAL,
                            memory_usage_percent REAL,
                            disk_temperature_c REAL,
                            uptime_seconds INTEGER NOT NULL
                        );

                        CREATE INDEX IF NOT EXISTS idx_hardware_samples_sample_time
                        ON hardware_samples(sample_time);

                        CREATE TABLE IF NOT EXISTS app_registry (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            app_key TEXT NOT NULL UNIQUE,
                            process_name TEXT NOT NULL,
                            display_name TEXT,
                            executable_path TEXT,
                            first_seen_at TEXT NOT NULL,
                            last_seen_at TEXT NOT NULL
                        );

                        CREATE TABLE IF NOT EXISTS network_usage_agg (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            bucket_start_time TEXT NOT NULL,
                            bucket_granularity_seconds INTEGER NOT NULL,
                            app_id INTEGER NOT NULL,
                            direction TEXT NOT NULL CHECK(direction IN ('inbound', 'outbound')),
                            scope_type TEXT NOT NULL CHECK(scope_type IN ('wan', 'lan', 'loopback', 'other')),
                            bytes INTEGER NOT NULL,
                            packets INTEGER,
                            FOREIGN KEY(app_id) REFERENCES app_registry(id)
                        );

                        CREATE INDEX IF NOT EXISTS idx_network_usage_agg_bucket_time
                        ON network_usage_agg(bucket_start_time);

                        CREATE INDEX IF NOT EXISTS idx_network_usage_agg_app_bucket
                        ON network_usage_agg(app_id, bucket_start_time);

                        CREATE INDEX IF NOT EXISTS idx_network_usage_agg_scope_direction
                        ON network_usage_agg(scope_type, direction, bucket_start_time);
                        """;

        await using var command = connection.CreateCommand();
        command.CommandText = schemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedSettingsAsync(
        SqliteConnection connection,
        MonitorSettings settings,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");

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
                              ON CONFLICT(id) DO NOTHING;
                              """;

        command.Parameters.AddWithValue("$httpPort", settings.HttpPort);
        command.Parameters.AddWithValue("$hardwareSampleIntervalMs", settings.HardwareSampleIntervalMs);
        command.Parameters.AddWithValue("$networkSampleIntervalMs", settings.NetworkSampleIntervalMs);
        command.Parameters.AddWithValue("$aggregateIntervalSeconds", settings.AggregateIntervalSeconds);
        command.Parameters.AddWithValue("$historyRetentionDays", settings.HistoryRetentionDays);
        command.Parameters.AddWithValue("$topNDefault", settings.TopNDefault);
        command.Parameters.AddWithValue("$createdAt", now);
        command.Parameters.AddWithValue("$updatedAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
