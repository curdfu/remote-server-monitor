using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Monitor.Network.Abstractions;
using Monitor.Network.Enums;
using Monitor.Network.Models;
using Monitor.Storage.Abstractions;

namespace Monitor.Storage.Repositories;

public sealed class NetworkTrafficRepository(
    IDbConnectionFactory dbConnectionFactory,
    IAppRegistry appRegistry,
    ILogger<NetworkTrafficRepository> logger)
{
    private const int AppIdLookupBatchSize = 200;

    public enum TrafficScopeFilter
    {
        All,
        Wan,
        Lan
    }

    public enum TrafficDirectionFilter
    {
        Total,
        Upload,
        Download
    }

    public async Task SaveAsync(IReadOnlyList<TrafficBucket> buckets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        if (buckets.Count == 0)
        {
            return;
        }

        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transactionHandle = await connection.BeginTransactionAsync(cancellationToken);
        var transaction = (SqliteTransaction)transactionHandle;

        var appIds = await UpsertAppRegistryAndGetIdsAsync(connection, transaction, buckets, cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT INTO network_usage_agg (
                                  bucket_start_time,
                                  bucket_granularity_seconds,
                                  app_id,
                                  direction,
                                  scope_type,
                                  bytes,
                                  packets
                              )
                              VALUES (
                                  $bucketStartTime,
                                  $bucketGranularitySeconds,
                                  $appId,
                                  $direction,
                                  $scopeType,
                                  $bytes,
                                  $packets
                              );
                              """;

        var bucketStartTimeParameter = command.Parameters.Add("$bucketStartTime", SqliteType.Text);
        var bucketGranularityParameter = command.Parameters.Add("$bucketGranularitySeconds", SqliteType.Integer);
        var appIdParameter = command.Parameters.Add("$appId", SqliteType.Integer);
        var directionParameter = command.Parameters.Add("$direction", SqliteType.Text);
        var scopeTypeParameter = command.Parameters.Add("$scopeType", SqliteType.Text);
        var bytesParameter = command.Parameters.Add("$bytes", SqliteType.Integer);
        var packetsParameter = command.Parameters.Add("$packets", SqliteType.Integer);

        foreach (var bucket in buckets)
        {
            bucketStartTimeParameter.Value = bucket.BucketStartTime.ToString("O");
            bucketGranularityParameter.Value = bucket.BucketGranularitySeconds;
            appIdParameter.Value = appIds[bucket.AppKey];
            directionParameter.Value = MapDirection(bucket.Direction);
            scopeTypeParameter.Value = MapScopeType(bucket.ScopeType);
            bytesParameter.Value = bucket.Bytes;
            packetsParameter.Value = bucket.Packets;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogDebug("Inserted {Count} network traffic buckets into SQLite.", buckets.Count);
    }

    public async Task<IReadOnlyList<TrafficBucket>> QueryBucketsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        string? appKey = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT n.bucket_start_time,
                                     n.bucket_granularity_seconds,
                                     a.app_key,
                                     n.direction,
                                     n.scope_type,
                                     n.bytes,
                                     n.packets
                              FROM network_usage_agg n
                              JOIN app_registry a ON a.id = n.app_id
                              WHERE n.bucket_start_time >= $from
                                AND n.bucket_start_time < $to
                                AND ($appKey IS NULL OR a.app_key = $appKey)
                              ORDER BY n.bucket_start_time ASC, a.app_key ASC;
                              """;

        command.Parameters.AddWithValue("$from", from.ToString("O"));
        command.Parameters.AddWithValue("$to", to.ToString("O"));
        command.Parameters.AddWithValue("$appKey", string.IsNullOrWhiteSpace(appKey) ? DBNull.Value : appKey);

        var results = new List<TrafficBucket>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TrafficBucket
            {
                BucketStartTime = reader.GetDateTimeOffset(0),
                BucketGranularitySeconds = reader.GetInt32(1),
                AppKey = reader.GetString(2),
                Direction = ParseDirection(reader.GetString(3)),
                ScopeType = ParseScopeType(reader.GetString(4)),
                Bytes = reader.GetInt64(5),
                Packets = reader.IsDBNull(6) ? 0 : reader.GetInt64(6)
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<AppTrafficPeriodSummary>> QueryAppSummariesAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TrafficScopeFilter scopeFilter = TrafficScopeFilter.All,
        TrafficDirectionFilter directionFilter = TrafficDirectionFilter.Total,
        int? topN = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var orderByExpression = GetOrderByExpression(scopeFilter, directionFilter);

        var sql = """
                  SELECT a.app_key,
                         a.process_name,
                         a.display_name,
                         SUM(CASE WHEN n.direction = 'outbound' THEN n.bytes ELSE 0 END) AS total_upload_bytes,
                         SUM(CASE WHEN n.direction = 'inbound' THEN n.bytes ELSE 0 END) AS total_download_bytes,
                         SUM(CASE WHEN n.direction = 'outbound' AND n.scope_type = 'wan' THEN n.bytes ELSE 0 END) AS wan_upload_bytes,
                         SUM(CASE WHEN n.direction = 'inbound' AND n.scope_type = 'wan' THEN n.bytes ELSE 0 END) AS wan_download_bytes,
                         SUM(CASE WHEN n.direction = 'outbound' AND n.scope_type = 'lan' THEN n.bytes ELSE 0 END) AS lan_upload_bytes,
                         SUM(CASE WHEN n.direction = 'inbound' AND n.scope_type = 'lan' THEN n.bytes ELSE 0 END) AS lan_download_bytes,
                         SUM(CASE WHEN n.direction = 'outbound' AND n.scope_type = 'loopback' THEN n.bytes ELSE 0 END) AS loopback_upload_bytes,
                         SUM(CASE WHEN n.direction = 'inbound' AND n.scope_type = 'loopback' THEN n.bytes ELSE 0 END) AS loopback_download_bytes,
                         SUM(CASE WHEN n.direction = 'outbound' AND n.scope_type = 'other' THEN n.bytes ELSE 0 END) AS other_upload_bytes,
                         SUM(CASE WHEN n.direction = 'inbound' AND n.scope_type = 'other' THEN n.bytes ELSE 0 END) AS other_download_bytes
                  FROM network_usage_agg n
                  JOIN app_registry a ON a.id = n.app_id
                  WHERE n.bucket_start_time >= $from
                    AND n.bucket_start_time < $to
                  GROUP BY a.app_key, a.process_name, a.display_name
                  ORDER BY
                  """;

        sql += Environment.NewLine + orderByExpression + " DESC, a.process_name ASC";

        if (topN is > 0)
        {
            sql += Environment.NewLine + "LIMIT $topN";
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql + ";";
        command.Parameters.AddWithValue("$from", from.ToString("O"));
        command.Parameters.AddWithValue("$to", to.ToString("O"));

        if (topN is > 0)
        {
            command.Parameters.AddWithValue("$topN", topN.Value);
        }

        var results = new List<AppTrafficPeriodSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AppTrafficPeriodSummary
            {
                AppKey = reader.GetString(0),
                ProcessName = reader.GetString(1),
                DisplayName = reader.IsDBNull(2) ? null : reader.GetString(2),
                TotalUploadBytes = reader.GetInt64(3),
                TotalDownloadBytes = reader.GetInt64(4),
                WanUploadBytes = reader.GetInt64(5),
                WanDownloadBytes = reader.GetInt64(6),
                LanUploadBytes = reader.GetInt64(7),
                LanDownloadBytes = reader.GetInt64(8),
                LoopbackUploadBytes = reader.GetInt64(9),
                LoopbackDownloadBytes = reader.GetInt64(10),
                OtherUploadBytes = reader.GetInt64(11),
                OtherDownloadBytes = reader.GetInt64(12)
            });
        }

        return results;
    }

    public async Task<AppTrafficPeriodSummary> QueryTotalsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TrafficScopeFilter scopeFilter = TrafficScopeFilter.All,
        TrafficDirectionFilter directionFilter = TrafficDirectionFilter.Total,
        CancellationToken cancellationToken = default)
    {
        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COALESCE(SUM(CASE
                                                      WHEN $directionFilter <> 'download' AND n.direction = 'outbound'
                                                      THEN n.bytes
                                                      ELSE 0
                                                  END), 0) AS total_upload_bytes,
                                     COALESCE(SUM(CASE
                                                      WHEN $directionFilter <> 'upload' AND n.direction = 'inbound'
                                                      THEN n.bytes
                                                      ELSE 0
                                                  END), 0) AS total_download_bytes,
                                     COALESCE(SUM(CASE
                                                      WHEN $directionFilter <> 'download'
                                                           AND n.direction = 'outbound'
                                                           AND n.scope_type = 'wan'
                                                      THEN n.bytes
                                                      ELSE 0
                                                  END), 0) AS wan_upload_bytes,
                                     COALESCE(SUM(CASE
                                                      WHEN $directionFilter <> 'upload'
                                                           AND n.direction = 'inbound'
                                                           AND n.scope_type = 'wan'
                                                      THEN n.bytes
                                                      ELSE 0
                                                  END), 0) AS wan_download_bytes,
                                     COALESCE(SUM(CASE
                                                      WHEN $directionFilter <> 'download'
                                                           AND n.direction = 'outbound'
                                                           AND n.scope_type = 'lan'
                                                      THEN n.bytes
                                                      ELSE 0
                                                  END), 0) AS lan_upload_bytes,
                                     COALESCE(SUM(CASE
                                                      WHEN $directionFilter <> 'upload'
                                                           AND n.direction = 'inbound'
                                                           AND n.scope_type = 'lan'
                                                      THEN n.bytes
                                                      ELSE 0
                                                  END), 0) AS lan_download_bytes,
                                     COALESCE(SUM(CASE
                                                      WHEN $directionFilter <> 'download'
                                                           AND n.direction = 'outbound'
                                                           AND n.scope_type = 'loopback'
                                                      THEN n.bytes
                                                      ELSE 0
                                                  END), 0) AS loopback_upload_bytes,
                                     COALESCE(SUM(CASE
                                                      WHEN $directionFilter <> 'upload'
                                                           AND n.direction = 'inbound'
                                                           AND n.scope_type = 'loopback'
                                                      THEN n.bytes
                                                      ELSE 0
                                                  END), 0) AS loopback_download_bytes,
                                     COALESCE(SUM(CASE
                                                      WHEN $directionFilter <> 'download'
                                                           AND n.direction = 'outbound'
                                                           AND n.scope_type = 'other'
                                                      THEN n.bytes
                                                      ELSE 0
                                                  END), 0) AS other_upload_bytes,
                                     COALESCE(SUM(CASE
                                                      WHEN $directionFilter <> 'upload'
                                                           AND n.direction = 'inbound'
                                                           AND n.scope_type = 'other'
                                                      THEN n.bytes
                                                      ELSE 0
                                                  END), 0) AS other_download_bytes
                              FROM network_usage_agg n
                              WHERE n.bucket_start_time >= $from
                                AND n.bucket_start_time < $to
                                AND ($scopeType IS NULL OR n.scope_type = $scopeType);
                              """;

        command.Parameters.AddWithValue("$from", from.ToString("O"));
        command.Parameters.AddWithValue("$to", to.ToString("O"));
        command.Parameters.AddWithValue("$scopeType", MapScopeFilterToDbValue(scopeFilter));
        command.Parameters.AddWithValue("$directionFilter", MapDirectionFilter(directionFilter));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new AppTrafficPeriodSummary();
        }

        return new AppTrafficPeriodSummary
        {
            TotalUploadBytes = reader.GetInt64(0),
            TotalDownloadBytes = reader.GetInt64(1),
            WanUploadBytes = reader.GetInt64(2),
            WanDownloadBytes = reader.GetInt64(3),
            LanUploadBytes = reader.GetInt64(4),
            LanDownloadBytes = reader.GetInt64(5),
            LoopbackUploadBytes = reader.GetInt64(6),
            LoopbackDownloadBytes = reader.GetInt64(7),
            OtherUploadBytes = reader.GetInt64(8),
            OtherDownloadBytes = reader.GetInt64(9)
        };
    }

    public async Task UpsertAppRegistryAsync(
        IReadOnlyCollection<AppRegistryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transactionHandle = await connection.BeginTransactionAsync(cancellationToken);
        var transaction = (SqliteTransaction)transactionHandle;

        await using var command = CreateUpsertAppRegistryCommand(connection, transaction);
        foreach (var entry in entries)
        {
            await ExecuteUpsertAppRegistryEntryAsync(command, entry, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogDebug("Upserted {Count} app registry entries into SQLite.", entries.Count);
    }

    private async Task<Dictionary<string, long>> UpsertAppRegistryAndGetIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<TrafficBucket> buckets,
        CancellationToken cancellationToken)
    {
        var appKeys = new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var bucket in buckets)
        {
            if (string.IsNullOrWhiteSpace(bucket.AppKey) || !seenKeys.Add(bucket.AppKey))
            {
                continue;
            }

            appKeys.Add(bucket.AppKey);
        }

        if (appKeys.Count == 0)
        {
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        await using var upsertCommand = CreateUpsertAppRegistryCommand(connection, transaction);
        foreach (var appKey in appKeys)
        {
            var entry = appRegistry.GetByAppKey(appKey) ?? CreateFallbackEntry(appKey);
            await ExecuteUpsertAppRegistryEntryAsync(upsertCommand, entry, cancellationToken);
        }

        return await GetAppIdsByKeysAsync(connection, transaction, appKeys, cancellationToken);
    }

    private static AppRegistryEntry CreateFallbackEntry(string appKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new AppRegistryEntry
        {
            AppKey = appKey,
            ProcessName = appKey,
            FirstSeenAt = now,
            LastSeenAt = now
        };
    }

    private static SqliteCommand CreateUpsertAppRegistryCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT INTO app_registry (
                                  app_key,
                                  process_name,
                                  display_name,
                                  executable_path,
                                  first_seen_at,
                                  last_seen_at
                              )
                              VALUES (
                                  $appKey,
                                  $processName,
                                  $displayName,
                                  $executablePath,
                                  $firstSeenAt,
                                  $lastSeenAt
                              )
                              ON CONFLICT(app_key) DO UPDATE SET
                                  process_name = excluded.process_name,
                                  display_name = COALESCE(excluded.display_name, app_registry.display_name),
                                  executable_path = COALESCE(excluded.executable_path, app_registry.executable_path),
                                  last_seen_at = excluded.last_seen_at;
                              """;

        command.Parameters.Add("$appKey", SqliteType.Text);
        command.Parameters.Add("$processName", SqliteType.Text);
        command.Parameters.Add("$displayName", SqliteType.Text);
        command.Parameters.Add("$executablePath", SqliteType.Text);
        command.Parameters.Add("$firstSeenAt", SqliteType.Text);
        command.Parameters.Add("$lastSeenAt", SqliteType.Text);
        return command;
    }

    private static async Task ExecuteUpsertAppRegistryEntryAsync(
        SqliteCommand command,
        AppRegistryEntry entry,
        CancellationToken cancellationToken)
    {
        command.Parameters["$appKey"].Value = entry.AppKey;
        command.Parameters["$processName"].Value = entry.ProcessName;
        command.Parameters["$displayName"].Value = ToDbValue(entry.DisplayName);
        command.Parameters["$executablePath"].Value = ToDbValue(entry.ExecutablePath);
        command.Parameters["$firstSeenAt"].Value = entry.FirstSeenAt.ToString("O");
        command.Parameters["$lastSeenAt"].Value = entry.LastSeenAt.ToString("O");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, long>> GetAppIdsByKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> appKeys,
        CancellationToken cancellationToken)
    {
        var appIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        for (var offset = 0; offset < appKeys.Count; offset += AppIdLookupBatchSize)
        {
            var count = Math.Min(AppIdLookupBatchSize, appKeys.Count - offset);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;

            var sql = new StringBuilder();
            sql.Append("SELECT id, app_key FROM app_registry WHERE app_key IN (");
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    sql.Append(", ");
                }

                var parameterName = $"$appKey{index}";
                sql.Append(parameterName);
                command.Parameters.AddWithValue(parameterName, appKeys[offset + index]);
            }

            sql.Append(");");
            command.CommandText = sql.ToString();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                appIds[reader.GetString(1)] = reader.GetInt64(0);
            }
        }

        foreach (var appKey in appKeys)
        {
            if (!appIds.ContainsKey(appKey))
            {
                throw new InvalidOperationException($"App registry id not found for app key '{appKey}'.");
            }
        }

        return appIds;
    }

    private static string GetOrderByExpression(TrafficScopeFilter scopeFilter, TrafficDirectionFilter directionFilter)
    {
        return (scopeFilter, directionFilter) switch
        {
            (TrafficScopeFilter.Wan, TrafficDirectionFilter.Upload) =>
                "SUM(CASE WHEN n.direction = 'outbound' AND n.scope_type = 'wan' THEN n.bytes ELSE 0 END)",
            (TrafficScopeFilter.Wan, TrafficDirectionFilter.Download) =>
                "SUM(CASE WHEN n.direction = 'inbound' AND n.scope_type = 'wan' THEN n.bytes ELSE 0 END)",
            (TrafficScopeFilter.Wan, TrafficDirectionFilter.Total) =>
                "SUM(CASE WHEN n.scope_type = 'wan' THEN n.bytes ELSE 0 END)",
            (TrafficScopeFilter.Lan, TrafficDirectionFilter.Upload) =>
                "SUM(CASE WHEN n.direction = 'outbound' AND n.scope_type = 'lan' THEN n.bytes ELSE 0 END)",
            (TrafficScopeFilter.Lan, TrafficDirectionFilter.Download) =>
                "SUM(CASE WHEN n.direction = 'inbound' AND n.scope_type = 'lan' THEN n.bytes ELSE 0 END)",
            (TrafficScopeFilter.Lan, TrafficDirectionFilter.Total) =>
                "SUM(CASE WHEN n.scope_type = 'lan' THEN n.bytes ELSE 0 END)",
            (TrafficScopeFilter.All, TrafficDirectionFilter.Upload) =>
                "SUM(CASE WHEN n.direction = 'outbound' THEN n.bytes ELSE 0 END)",
            (TrafficScopeFilter.All, TrafficDirectionFilter.Download) =>
                "SUM(CASE WHEN n.direction = 'inbound' THEN n.bytes ELSE 0 END)",
            _ =>
                "SUM(n.bytes)"
        };
    }

    private static object MapScopeFilterToDbValue(TrafficScopeFilter scopeFilter) => scopeFilter switch
    {
        TrafficScopeFilter.Wan => "wan",
        TrafficScopeFilter.Lan => "lan",
        _ => DBNull.Value
    };

    private static string MapDirectionFilter(TrafficDirectionFilter directionFilter) => directionFilter switch
    {
        TrafficDirectionFilter.Upload => "upload",
        TrafficDirectionFilter.Download => "download",
        _ => "total"
    };

    private static object ToDbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static string MapDirection(TrafficDirection direction) => direction switch
    {
        TrafficDirection.Inbound => "inbound",
        TrafficDirection.Outbound => "outbound",
        _ => "outbound"
    };

    private static TrafficDirection ParseDirection(string value) => value.ToLowerInvariant() switch
    {
        "inbound" => TrafficDirection.Inbound,
        "outbound" => TrafficDirection.Outbound,
        _ => TrafficDirection.Outbound
    };

    private static string MapScopeType(AddressScopeType scopeType) => scopeType switch
    {
        AddressScopeType.Wan => "wan",
        AddressScopeType.Lan => "lan",
        AddressScopeType.Loopback => "loopback",
        _ => "other"
    };

    private static AddressScopeType ParseScopeType(string value) => value.ToLowerInvariant() switch
    {
        "wan" => AddressScopeType.Wan,
        "lan" => AddressScopeType.Lan,
        "loopback" => AddressScopeType.Loopback,
        _ => AddressScopeType.Other
    };
}
