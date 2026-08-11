using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitor.Contracts.Options;
using Monitor.Storage.Abstractions;

namespace Monitor.Storage.Services;

// 数据库初始化只负责确保本地 SQLite 结构可用，不做历史迁移和数据修复。
// 表结构围绕两类数据设计：硬件采样保存原始时间序列，网络流量保存细粒度 bucket 并额外维护 12 小时 rollup 以支撑长时间查询。
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

    public async Task OptimizeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await EnablePragmasAsync(connection, cancellationToken);
        await CreateDeferredIndexesAsync(connection, cancellationToken);

        logger.LogInformation("SQLite database optimization indexes ensured. Path: {DatabasePath}", dbConnectionFactory.DatabasePath);
    }

    private static async Task EnablePragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // WAL 降低读写互斥，NORMAL synchronous 在监控场景下平衡写入性能和崩溃恢复成本。
        // foreign_keys 必须显式开启，SQLite 默认不会强制外键约束。
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
        // network_usage_agg 保存查询可回放的细粒度事实数据；rollup_12h 保存完整 12 小时窗口的预聚合结果。
        // app_registry 把高频流量记录中的进程元数据抽离出来，避免每个 bucket 重复保存路径和显示名。
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

                        CREATE TABLE IF NOT EXISTS ignored_network_apps (
                            app_key TEXT PRIMARY KEY,
                            process_name TEXT NOT NULL,
                            display_name TEXT,
                            executable_path TEXT,
                            created_at TEXT NOT NULL,
                            updated_at TEXT NOT NULL
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

                        CREATE TABLE IF NOT EXISTS network_usage_rollup_12h (
                            window_start_time TEXT NOT NULL,
                            window_duration_seconds INTEGER NOT NULL,
                            app_id INTEGER NOT NULL,
                            direction TEXT NOT NULL CHECK(direction IN ('inbound', 'outbound')),
                            scope_type TEXT NOT NULL CHECK(scope_type IN ('wan', 'lan', 'loopback', 'other')),
                            bytes INTEGER NOT NULL,
                            packets INTEGER NOT NULL,
                            source_bucket_count INTEGER NOT NULL,
                            updated_at TEXT NOT NULL,
                            PRIMARY KEY(window_start_time, app_id, direction, scope_type),
                            FOREIGN KEY(app_id) REFERENCES app_registry(id)
                        );

                        CREATE TABLE IF NOT EXISTS network_usage_rollup_12h_windows (
                            window_start_time TEXT PRIMARY KEY,
                            window_end_time TEXT NOT NULL,
                            source_bucket_count INTEGER NOT NULL,
                            updated_at TEXT NOT NULL
                        );

                        CREATE INDEX IF NOT EXISTS idx_network_usage_rollup_12h_scope_direction_window
                        ON network_usage_rollup_12h(scope_type, direction, window_start_time, app_id, bytes);

                        CREATE INDEX IF NOT EXISTS idx_network_usage_rollup_12h_app_window
                        ON network_usage_rollup_12h(app_id, window_start_time);
                        """;

        await using var command = connection.CreateCommand();
        command.CommandText = schemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CreateDeferredIndexesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // 延迟索引用于优化历史查询的组合筛选；放在 OptimizeAsync 中创建，避免首次初始化路径过重。
        var indexSql = """
                       CREATE INDEX IF NOT EXISTS idx_network_usage_agg_time_app_scope_direction_bytes
                       ON network_usage_agg(bucket_start_time, app_id, scope_type, direction, bytes);

                       CREATE INDEX IF NOT EXISTS idx_network_usage_agg_scope_direction_time_app_bytes
                       ON network_usage_agg(scope_type, direction, bucket_start_time, app_id, bytes);
                       """;

        await using var command = connection.CreateCommand();
        command.CommandText = indexSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedSettingsAsync(
        SqliteConnection connection,
        MonitorSettings settings,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");

        // 设置表只有 id=1 一行。首次启动写入默认值，后续用户保存的 SQLite 配置不能被 appsettings 覆盖。
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
                                  $networkProcessingIntervalMs,
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
        command.Parameters.AddWithValue("$networkProcessingIntervalMs", settings.NetworkProcessingIntervalMs);
        command.Parameters.AddWithValue("$aggregateIntervalSeconds", settings.AggregateIntervalSeconds);
        command.Parameters.AddWithValue("$historyRetentionDays", settings.HistoryRetentionDays);
        command.Parameters.AddWithValue("$topNDefault", settings.TopNDefault);
        command.Parameters.AddWithValue("$createdAt", now);
        command.Parameters.AddWithValue("$updatedAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
