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
    private const int SqlParameterBatchSize = 400;
    public const int RollupWindowDurationSeconds = 12 * 60 * 60;
    private static readonly TimeSpan RollupWindowDuration = TimeSpan.FromSeconds(RollupWindowDurationSeconds);

    public enum TrafficScopeFilter
    {
        All,
        Wan,
        Lan,
        Loopback
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
        var currentRollupWindowStart = AlignDownToRollupWindow(DateTimeOffset.UtcNow);

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
            var bucketStartTime = bucket.BucketStartTime.ToUniversalTime();
            bucketStartTimeParameter.Value = ToDbTime(bucketStartTime);
            bucketGranularityParameter.Value = bucket.BucketGranularitySeconds;
            appIdParameter.Value = appIds[bucket.AppKey];
            directionParameter.Value = MapDirection(bucket.Direction);
            scopeTypeParameter.Value = MapScopeType(bucket.ScopeType);
            bytesParameter.Value = bucket.Bytes;
            packetsParameter.Value = bucket.Packets;
            await command.ExecuteNonQueryAsync(cancellationToken);

            var bucketRollupWindowStart = AlignDownToRollupWindow(bucketStartTime);
            if (bucketRollupWindowStart < currentRollupWindowStart)
            {
                await ApplyCompletedRollupBucketDeltaAsync(
                    connection,
                    transaction,
                    bucketRollupWindowStart,
                    appIds[bucket.AppKey],
                    MapDirection(bucket.Direction),
                    MapScopeType(bucket.ScopeType),
                    bucket.Bytes,
                    bucket.Packets,
                    cancellationToken);
            }
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

        command.Parameters.AddWithValue("$from", ToDbTime(from));
        command.Parameters.AddWithValue("$to", ToDbTime(to));
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

        await using var command = connection.CreateCommand();
        var trafficSourceSql = await BuildTrafficSourceSqlAsync(connection, command, from, to, cancellationToken);

        if (topN is > 0)
        {
            command.CommandText = BuildTopAppSummariesSql(trafficSourceSql, scopeFilter, directionFilter);
            command.Parameters.AddWithValue("$topN", topN.Value);
        }
        else
        {
            command.CommandText = BuildAllAppSummariesSql(trafficSourceSql, scopeFilter, directionFilter);
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
                ExecutablePath = reader.IsDBNull(3) ? null : reader.GetString(3),
                TotalUploadBytes = reader.GetInt64(4),
                TotalDownloadBytes = reader.GetInt64(5),
                WanUploadBytes = reader.GetInt64(6),
                WanDownloadBytes = reader.GetInt64(7),
                LanUploadBytes = reader.GetInt64(8),
                LanDownloadBytes = reader.GetInt64(9),
                LoopbackUploadBytes = reader.GetInt64(10),
                LoopbackDownloadBytes = reader.GetInt64(11),
                OtherUploadBytes = reader.GetInt64(12),
                OtherDownloadBytes = reader.GetInt64(13)
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
        var trafficSourceSql = await BuildTrafficSourceSqlAsync(
            connection,
            command,
            from,
            to,
            cancellationToken,
            BuildTrafficSourceFilters(scopeFilter, directionFilter));
        var sqlBuilder = new StringBuilder("""
                                           WITH traffic AS (
                                           """);
        sqlBuilder.AppendLine(trafficSourceSql);
        sqlBuilder.AppendLine("""
                                           )
                                           SELECT COALESCE(SUM(CASE
                                                                   WHEN t.direction = 'outbound'
                                                                   THEN t.bytes
                                                                   ELSE 0
                                                               END), 0) AS total_upload_bytes,
                                                  COALESCE(SUM(CASE
                                                                   WHEN t.direction = 'inbound'
                                                                   THEN t.bytes
                                                                   ELSE 0
                                                               END), 0) AS total_download_bytes,
                                                  COALESCE(SUM(CASE
                                                                   WHEN t.direction = 'outbound'
                                                                        AND t.scope_type = 'wan'
                                                                   THEN t.bytes
                                                                   ELSE 0
                                                               END), 0) AS wan_upload_bytes,
                                                  COALESCE(SUM(CASE
                                                                   WHEN t.direction = 'inbound'
                                                                        AND t.scope_type = 'wan'
                                                                   THEN t.bytes
                                                                   ELSE 0
                                                               END), 0) AS wan_download_bytes,
                                                  COALESCE(SUM(CASE
                                                                   WHEN t.direction = 'outbound'
                                                                        AND t.scope_type = 'lan'
                                                                   THEN t.bytes
                                                                   ELSE 0
                                                               END), 0) AS lan_upload_bytes,
                                                  COALESCE(SUM(CASE
                                                                   WHEN t.direction = 'inbound'
                                                                        AND t.scope_type = 'lan'
                                                                   THEN t.bytes
                                                                   ELSE 0
                                                               END), 0) AS lan_download_bytes,
                                                  COALESCE(SUM(CASE
                                                                   WHEN t.direction = 'outbound'
                                                                        AND t.scope_type = 'loopback'
                                                                   THEN t.bytes
                                                                   ELSE 0
                                                               END), 0) AS loopback_upload_bytes,
                                                  COALESCE(SUM(CASE
                                                                   WHEN t.direction = 'inbound'
                                                                        AND t.scope_type = 'loopback'
                                                                   THEN t.bytes
                                                                   ELSE 0
                                                               END), 0) AS loopback_download_bytes,
                                                  COALESCE(SUM(CASE
                                                                   WHEN t.direction = 'outbound'
                                                                        AND t.scope_type = 'other'
                                                                   THEN t.bytes
                                                                   ELSE 0
                                                               END), 0) AS other_upload_bytes,
                                                  COALESCE(SUM(CASE
                                                                   WHEN t.direction = 'inbound'
                                                                        AND t.scope_type = 'other'
                                                                   THEN t.bytes
                                                                   ELSE 0
                                                               END), 0) AS other_download_bytes
                                           FROM traffic t
                                           ;
                                           """);
        command.CommandText = sqlBuilder.ToString();

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

    public async Task<IReadOnlyList<AppTrafficSegment>> QueryAppSegmentsAsync(
        string appKey,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan segmentDuration,
        TrafficScopeFilter scopeFilter = TrafficScopeFilter.All,
        TrafficDirectionFilter directionFilter = TrafficDirectionFilter.Total,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        if (from >= to)
        {
            return Array.Empty<AppTrafficSegment>();
        }

        if (segmentDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentDuration), segmentDuration, "Segment duration must be positive.");
        }

        var rangeFrom = from.ToUniversalTime();
        var rangeTo = to.ToUniversalTime();

        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var appId = await QueryAppIdAsync(connection, appKey, cancellationToken);
        if (appId is null)
        {
            return Array.Empty<AppTrafficSegment>();
        }

        var ranges = BuildSegmentRanges(rangeFrom, rangeTo, segmentDuration);
        var results = new List<AppTrafficSegment>(ranges.Count);
        foreach (var range in ranges)
        {
            var summary = await QueryAppSegmentSummaryAsync(
                connection,
                appId.Value,
                range.From,
                range.To,
                scopeFilter,
                directionFilter,
                cancellationToken);

            results.Add(new AppTrafficSegment
            {
                From = range.From,
                To = range.To,
                TotalUploadBytes = summary.TotalUploadBytes,
                TotalDownloadBytes = summary.TotalDownloadBytes,
                WanUploadBytes = summary.WanUploadBytes,
                WanDownloadBytes = summary.WanDownloadBytes,
                LanUploadBytes = summary.LanUploadBytes,
                LanDownloadBytes = summary.LanDownloadBytes,
                LoopbackUploadBytes = summary.LoopbackUploadBytes,
                LoopbackDownloadBytes = summary.LoopbackDownloadBytes,
                OtherUploadBytes = summary.OtherUploadBytes,
                OtherDownloadBytes = summary.OtherDownloadBytes
            });
        }

        return results;
    }

    public async Task<int> RollupCompletedWindowsAsync(
        int maxWindows,
        CancellationToken cancellationToken = default)
    {
        if (maxWindows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWindows), maxWindows, "Rollup window count must be positive.");
        }

        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var earliestBucketTime = await GetEarliestBucketTimeAsync(connection, cancellationToken);
        if (earliestBucketTime is null)
        {
            return 0;
        }

        var firstEligibleWindowStart = AlignUpToRollupWindow(earliestBucketTime.Value);
        var currentWindowStart = AlignDownToRollupWindow(DateTimeOffset.UtcNow);
        if (firstEligibleWindowStart >= currentWindowStart)
        {
            return 0;
        }

        var existingWindowStarts = await GetRollupWindowMarkerStartsAsync(
            connection,
            firstEligibleWindowStart,
            currentWindowStart,
            cancellationToken);

        var processed = 0;
        var firstProcessedWindowStart = (DateTimeOffset?)null;
        var lastProcessedWindowStart = (DateTimeOffset?)null;

        for (var windowStart = firstEligibleWindowStart;
             windowStart < currentWindowStart && processed < maxWindows;
             windowStart = windowStart.Add(RollupWindowDuration))
        {
            if (existingWindowStarts.Contains(windowStart))
            {
                continue;
            }

            await RollupWindowAsync(connection, windowStart, cancellationToken);
            firstProcessedWindowStart ??= windowStart;
            lastProcessedWindowStart = windowStart;
            processed++;
        }

        if (processed > 0)
        {
            logger.LogInformation(
                "Rolled up {WindowCount} completed 12-hour network traffic window(s). FirstWindowStart={FirstWindowStart}, LastWindowStart={LastWindowStart}.",
                processed,
                firstProcessedWindowStart,
                lastProcessedWindowStart);
        }

        return processed;
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

    private static async Task<long?> QueryAppIdAsync(
        SqliteConnection connection,
        string appKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id
                              FROM app_registry
                              WHERE app_key = $appKey
                              LIMIT 1;
                              """;
        command.Parameters.AddWithValue("$appKey", appKey);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private static async Task<AppTrafficPeriodSummary> QueryAppSegmentSummaryAsync(
        SqliteConnection connection,
        long appId,
        DateTimeOffset from,
        DateTimeOffset to,
        TrafficScopeFilter scopeFilter,
        TrafficDirectionFilter directionFilter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var trafficSourceSql = await BuildTrafficSourceSqlAsync(
            connection,
            command,
            from,
            to,
            cancellationToken,
            BuildTrafficSourceFilters(scopeFilter, directionFilter));
        command.CommandText = $"""
                               WITH traffic AS (
                               {trafficSourceSql}
                               )
                               SELECT COALESCE(SUM(CASE
                                                       WHEN t.direction = 'outbound'
                                                       THEN t.bytes
                                                       ELSE 0
                                                   END), 0) AS total_upload_bytes,
                                      COALESCE(SUM(CASE
                                                       WHEN t.direction = 'inbound'
                                                       THEN t.bytes
                                                       ELSE 0
                                                   END), 0) AS total_download_bytes,
                                      COALESCE(SUM(CASE
                                                       WHEN t.direction = 'outbound'
                                                            AND t.scope_type = 'wan'
                                                       THEN t.bytes
                                                       ELSE 0
                                                   END), 0) AS wan_upload_bytes,
                                      COALESCE(SUM(CASE
                                                       WHEN t.direction = 'inbound'
                                                            AND t.scope_type = 'wan'
                                                       THEN t.bytes
                                                       ELSE 0
                                                   END), 0) AS wan_download_bytes,
                                      COALESCE(SUM(CASE
                                                       WHEN t.direction = 'outbound'
                                                            AND t.scope_type = 'lan'
                                                       THEN t.bytes
                                                       ELSE 0
                                                   END), 0) AS lan_upload_bytes,
                                      COALESCE(SUM(CASE
                                                       WHEN t.direction = 'inbound'
                                                            AND t.scope_type = 'lan'
                                                       THEN t.bytes
                                                       ELSE 0
                                                   END), 0) AS lan_download_bytes,
                                      COALESCE(SUM(CASE
                                                       WHEN t.direction = 'outbound'
                                                            AND t.scope_type = 'loopback'
                                                       THEN t.bytes
                                                       ELSE 0
                                                   END), 0) AS loopback_upload_bytes,
                                      COALESCE(SUM(CASE
                                                       WHEN t.direction = 'inbound'
                                                            AND t.scope_type = 'loopback'
                                                       THEN t.bytes
                                                       ELSE 0
                                                   END), 0) AS loopback_download_bytes,
                                      COALESCE(SUM(CASE
                                                       WHEN t.direction = 'outbound'
                                                            AND t.scope_type = 'other'
                                                       THEN t.bytes
                                                       ELSE 0
                                                   END), 0) AS other_upload_bytes,
                                      COALESCE(SUM(CASE
                                                       WHEN t.direction = 'inbound'
                                                            AND t.scope_type = 'other'
                                                       THEN t.bytes
                                                       ELSE 0
                                                   END), 0) AS other_download_bytes
                               FROM traffic t
                               WHERE t.app_id = $appId;
                               """;
        command.Parameters.AddWithValue("$appId", appId);

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

    private static async Task<string> BuildTrafficSourceSqlAsync(
        SqliteConnection connection,
        SqliteCommand command,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken,
        IReadOnlyList<TrafficSourceFilter>? sourceFilters = null)
    {
        var plan = await BuildTrafficSourcePlanAsync(
            connection,
            from.ToUniversalTime(),
            to.ToUniversalTime(),
            cancellationToken);

        var filters = sourceFilters is { Count: > 0 }
            ? sourceFilters
            : [TrafficSourceFilter.Empty];
        var sourceQueries = new List<string>();
        if (plan.RollupWindowStarts.Count > 0)
        {
            var rollupParameterNames = AddRollupWindowParameters(command, plan.RollupWindowStarts);
            foreach (var filter in filters)
            {
                sourceQueries.Add($"""
                                   SELECT r.app_id,
                                          r.direction,
                                          r.scope_type,
                                          r.bytes
                                   FROM network_usage_rollup_12h r
                                   WHERE r.window_start_time IN ({string.Join(", ", rollupParameterNames)})
                                   {BuildTrafficSourceFilterSql(filter, "r")}
                                   """);
            }
        }

        for (var index = 0; index < plan.RawRanges.Count; index++)
        {
            var range = plan.RawRanges[index];
            var fromParameterName = $"$rawFrom{index}";
            var toParameterName = $"$rawTo{index}";
            command.Parameters.AddWithValue(fromParameterName, ToDbTime(range.From));
            command.Parameters.AddWithValue(toParameterName, ToDbTime(range.To));

            foreach (var filter in filters)
            {
                sourceQueries.Add($"""
                                   SELECT n.app_id,
                                          n.direction,
                                          n.scope_type,
                                          n.bytes
                                   FROM network_usage_agg n
                                   WHERE n.bucket_start_time >= {fromParameterName}
                                     AND n.bucket_start_time < {toParameterName}
                                   {BuildTrafficSourceFilterSql(filter, "n")}
                                   """);
            }
        }

        if (sourceQueries.Count == 0)
        {
            return """
                   SELECT n.app_id,
                          n.direction,
                          n.scope_type,
                          n.bytes
                   FROM network_usage_agg n
                   WHERE 0 = 1
                   """;
        }

        return string.Join($"{Environment.NewLine}UNION ALL{Environment.NewLine}", sourceQueries);
    }

    private static IReadOnlyList<TrafficSourceFilter> BuildTrafficSourceFilters(
        TrafficScopeFilter scopeFilter,
        TrafficDirectionFilter directionFilter)
    {
        if (scopeFilter is TrafficScopeFilter.All && directionFilter is TrafficDirectionFilter.Total)
        {
            return [TrafficSourceFilter.Empty];
        }

        var scope = scopeFilter is TrafficScopeFilter.All ? null : MapScopeFilterToDbScope(scopeFilter);
        if (directionFilter is not TrafficDirectionFilter.Total)
        {
            return [new TrafficSourceFilter(scope, MapDirectionFilterToDbDirection(directionFilter))];
        }

        if (scope is null)
        {
            return [TrafficSourceFilter.Empty];
        }

        return
        [
            new TrafficSourceFilter(scope, "outbound"),
            new TrafficSourceFilter(scope, "inbound")
        ];
    }

    private static string BuildTrafficSourceFilterSql(TrafficSourceFilter filter, string tableAlias)
    {
        var sql = new StringBuilder();
        if (filter.ScopeType is not null)
        {
            sql.AppendLine($"  AND {tableAlias}.scope_type = '{filter.ScopeType}'");
        }

        if (filter.Direction is not null)
        {
            sql.AppendLine($"  AND {tableAlias}.direction = '{filter.Direction}'");
        }

        return sql.ToString().TrimEnd();
    }

    private static async Task<TrafficSourcePlan> BuildTrafficSourcePlanAsync(
        SqliteConnection connection,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        if (from >= to)
        {
            return new TrafficSourcePlan([], []);
        }

        var rawRanges = new List<TimeRange>();
        var rollupWindowStarts = new List<DateTimeOffset>();
        var firstFullWindowStart = AlignUpToRollupWindow(from);
        var firstPartialWindowEnd = firstFullWindowStart < to ? firstFullWindowStart : to;
        var fullWindowEnd = AlignDownToRollupWindow(to);

        if (from < firstPartialWindowEnd)
        {
            rawRanges.Add(new TimeRange(from, firstPartialWindowEnd));
        }

        if (firstFullWindowStart < fullWindowEnd)
        {
            var candidateWindowStarts = EnumerateRollupWindowStarts(firstFullWindowStart, fullWindowEnd).ToArray();
            var completedWindowStarts = await QueryCompletedRollupWindowStartsAsync(
                connection,
                candidateWindowStarts,
                cancellationToken);

            foreach (var windowStart in candidateWindowStarts)
            {
                if (completedWindowStarts.Contains(windowStart))
                {
                    rollupWindowStarts.Add(windowStart);
                    continue;
                }

                rawRanges.Add(new TimeRange(windowStart, windowStart.Add(RollupWindowDuration)));
            }
        }

        if (fullWindowEnd < to)
        {
            var lastPartialWindowStart = fullWindowEnd > from ? fullWindowEnd : from;
            if (lastPartialWindowStart < to)
            {
                rawRanges.Add(new TimeRange(lastPartialWindowStart, to));
            }
        }

        return new TrafficSourcePlan(rollupWindowStarts, MergeTimeRanges(rawRanges));
    }

    private static async Task<HashSet<DateTimeOffset>> QueryCompletedRollupWindowStartsAsync(
        SqliteConnection connection,
        IReadOnlyList<DateTimeOffset> windowStarts,
        CancellationToken cancellationToken)
    {
        var completedWindowStarts = new HashSet<DateTimeOffset>();
        if (windowStarts.Count == 0)
        {
            return completedWindowStarts;
        }

        for (var offset = 0; offset < windowStarts.Count; offset += SqlParameterBatchSize)
        {
            var count = Math.Min(SqlParameterBatchSize, windowStarts.Count - offset);
            await using var command = connection.CreateCommand();
            var sqlBuilder = new StringBuilder();
            sqlBuilder.Append("SELECT window_start_time FROM network_usage_rollup_12h_windows WHERE window_start_time IN (");

            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    sqlBuilder.Append(", ");
                }

                var parameterName = $"$windowStart{index}";
                sqlBuilder.Append(parameterName);
                command.Parameters.AddWithValue(parameterName, ToDbTime(windowStarts[offset + index]));
            }

            sqlBuilder.Append(");");
            command.CommandText = sqlBuilder.ToString();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                completedWindowStarts.Add(reader.GetDateTimeOffset(0).ToUniversalTime());
            }
        }

        return completedWindowStarts;
    }

    private static IReadOnlyList<string> AddRollupWindowParameters(
        SqliteCommand command,
        IReadOnlyList<DateTimeOffset> windowStarts)
    {
        var parameterNames = new List<string>(windowStarts.Count);
        for (var index = 0; index < windowStarts.Count; index++)
        {
            var parameterName = $"$rollupWindow{index}";
            command.Parameters.AddWithValue(parameterName, ToDbTime(windowStarts[index]));
            parameterNames.Add(parameterName);
        }

        return parameterNames;
    }

    private static IReadOnlyList<TimeRange> MergeTimeRanges(IReadOnlyList<TimeRange> ranges)
    {
        if (ranges.Count <= 1)
        {
            return ranges;
        }

        var orderedRanges = ranges
            .Where(static range => range.From < range.To)
            .OrderBy(static range => range.From)
            .ToArray();

        if (orderedRanges.Length == 0)
        {
            return [];
        }

        var mergedRanges = new List<TimeRange>();
        var current = orderedRanges[0];
        for (var index = 1; index < orderedRanges.Length; index++)
        {
            var next = orderedRanges[index];
            if (next.From <= current.To)
            {
                current = new TimeRange(current.From, next.To > current.To ? next.To : current.To);
                continue;
            }

            mergedRanges.Add(current);
            current = next;
        }

        mergedRanges.Add(current);
        return mergedRanges;
    }

    private static async Task<DateTimeOffset?> GetEarliestBucketTimeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT bucket_start_time
                              FROM network_usage_agg
                              ORDER BY bucket_start_time ASC
                              LIMIT 1;
                              """;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : DateTimeOffset.Parse((string)result).ToUniversalTime();
    }

    private static async Task<HashSet<DateTimeOffset>> GetRollupWindowMarkerStartsAsync(
        SqliteConnection connection,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var windowStarts = new HashSet<DateTimeOffset>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT window_start_time
                              FROM network_usage_rollup_12h_windows
                              WHERE window_start_time >= $from
                                AND window_start_time < $to;
                              """;
        command.Parameters.AddWithValue("$from", ToDbTime(from));
        command.Parameters.AddWithValue("$to", ToDbTime(to));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            windowStarts.Add(reader.GetDateTimeOffset(0).ToUniversalTime());
        }

        return windowStarts;
    }

    private static async Task RollupWindowAsync(
        SqliteConnection connection,
        DateTimeOffset windowStart,
        CancellationToken cancellationToken)
    {
        var windowEnd = windowStart.Add(RollupWindowDuration);
        var updatedAt = DateTimeOffset.UtcNow;

        await using var transactionHandle = await connection.BeginTransactionAsync(cancellationToken);
        var transaction = (SqliteTransaction)transactionHandle;

        await DeleteRollupWindowAsync(connection, transaction, windowStart, cancellationToken);

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                                        INSERT INTO network_usage_rollup_12h (
                                            window_start_time,
                                            window_duration_seconds,
                                            app_id,
                                            direction,
                                            scope_type,
                                            bytes,
                                            packets,
                                            source_bucket_count,
                                            updated_at
                                        )
                                        SELECT $windowStart,
                                               $windowDurationSeconds,
                                               app_id,
                                               direction,
                                               scope_type,
                                               SUM(bytes),
                                               COALESCE(SUM(packets), 0),
                                               COUNT(*),
                                               $updatedAt
                                        FROM network_usage_agg
                                        WHERE bucket_start_time >= $windowStart
                                          AND bucket_start_time < $windowEnd
                                        GROUP BY app_id, direction, scope_type;
                                        """;
            insertCommand.Parameters.AddWithValue("$windowStart", ToDbTime(windowStart));
            insertCommand.Parameters.AddWithValue("$windowEnd", ToDbTime(windowEnd));
            insertCommand.Parameters.AddWithValue("$windowDurationSeconds", RollupWindowDurationSeconds);
            insertCommand.Parameters.AddWithValue("$updatedAt", ToDbTime(updatedAt));
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var sourceBucketCount = await GetRollupSourceBucketCountAsync(
            connection,
            transaction,
            windowStart,
            cancellationToken);

        await using (var markerCommand = connection.CreateCommand())
        {
            markerCommand.Transaction = transaction;
            markerCommand.CommandText = """
                                        INSERT INTO network_usage_rollup_12h_windows (
                                            window_start_time,
                                            window_end_time,
                                            source_bucket_count,
                                            updated_at
                                        )
                                        VALUES (
                                            $windowStart,
                                            $windowEnd,
                                            $sourceBucketCount,
                                            $updatedAt
                                        )
                                        ON CONFLICT(window_start_time) DO UPDATE SET
                                            window_end_time = excluded.window_end_time,
                                            source_bucket_count = excluded.source_bucket_count,
                                            updated_at = excluded.updated_at;
                                        """;
            markerCommand.Parameters.AddWithValue("$windowStart", ToDbTime(windowStart));
            markerCommand.Parameters.AddWithValue("$windowEnd", ToDbTime(windowEnd));
            markerCommand.Parameters.AddWithValue("$sourceBucketCount", sourceBucketCount);
            markerCommand.Parameters.AddWithValue("$updatedAt", ToDbTime(updatedAt));
            await markerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<long> GetRollupSourceBucketCountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset windowStart,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              SELECT COALESCE(SUM(source_bucket_count), 0)
                              FROM network_usage_rollup_12h
                              WHERE window_start_time = $windowStart;
                              """;
        command.Parameters.AddWithValue("$windowStart", ToDbTime(windowStart));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private static async Task ApplyCompletedRollupBucketDeltaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset windowStart,
        long appId,
        string direction,
        string scopeType,
        long bytes,
        long packets,
        CancellationToken cancellationToken)
    {
        var updatedAt = DateTimeOffset.UtcNow;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT INTO network_usage_rollup_12h (
                                  window_start_time,
                                  window_duration_seconds,
                                  app_id,
                                  direction,
                                  scope_type,
                                  bytes,
                                  packets,
                                  source_bucket_count,
                                  updated_at
                              )
                              SELECT $windowStart,
                                     $windowDurationSeconds,
                                     $appId,
                                     $direction,
                                     $scopeType,
                                     $bytes,
                                     $packets,
                                     1,
                                     $updatedAt
                              WHERE EXISTS (
                                  SELECT 1
                                  FROM network_usage_rollup_12h_windows
                                  WHERE window_start_time = $windowStart
                              )
                              ON CONFLICT(window_start_time, app_id, direction, scope_type) DO UPDATE SET
                                  bytes = bytes + excluded.bytes,
                                  packets = packets + excluded.packets,
                                  source_bucket_count = source_bucket_count + excluded.source_bucket_count,
                                  updated_at = excluded.updated_at;

                              UPDATE network_usage_rollup_12h_windows
                              SET source_bucket_count = source_bucket_count + 1,
                                  updated_at = $updatedAt
                              WHERE window_start_time = $windowStart;
                              """;
        command.Parameters.AddWithValue("$windowStart", ToDbTime(windowStart));
        command.Parameters.AddWithValue("$windowDurationSeconds", RollupWindowDurationSeconds);
        command.Parameters.AddWithValue("$appId", appId);
        command.Parameters.AddWithValue("$direction", direction);
        command.Parameters.AddWithValue("$scopeType", scopeType);
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$packets", packets);
        command.Parameters.AddWithValue("$updatedAt", ToDbTime(updatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteRollupWindowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset windowStart,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              DELETE FROM network_usage_rollup_12h
                              WHERE window_start_time = $windowStart;

                              DELETE FROM network_usage_rollup_12h_windows
                              WHERE window_start_time = $windowStart;
                              """;
        command.Parameters.AddWithValue("$windowStart", ToDbTime(windowStart));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildTopAppSummariesSql(
        string trafficSourceSql,
        TrafficScopeFilter scopeFilter,
        TrafficDirectionFilter directionFilter)
    {
        var rankExpression = GetOrderByExpression(scopeFilter, directionFilter, "t");
        var sqlBuilder = new StringBuilder("""
                                           WITH traffic AS (
                                           """);
        sqlBuilder.AppendLine(trafficSourceSql);
        sqlBuilder.AppendLine($"""
                                            )
                                            SELECT a.app_key,
                                                   a.process_name,
                                                   a.display_name,
                                                   a.executable_path,
                                                   SUM(CASE WHEN t.direction = 'outbound' THEN t.bytes ELSE 0 END) AS total_upload_bytes,
                                                   SUM(CASE WHEN t.direction = 'inbound' THEN t.bytes ELSE 0 END) AS total_download_bytes,
                                                   SUM(CASE WHEN t.direction = 'outbound' AND t.scope_type = 'wan' THEN t.bytes ELSE 0 END) AS wan_upload_bytes,
                                                   SUM(CASE WHEN t.direction = 'inbound' AND t.scope_type = 'wan' THEN t.bytes ELSE 0 END) AS wan_download_bytes,
                                                   SUM(CASE WHEN t.direction = 'outbound' AND t.scope_type = 'lan' THEN t.bytes ELSE 0 END) AS lan_upload_bytes,
                                                   SUM(CASE WHEN t.direction = 'inbound' AND t.scope_type = 'lan' THEN t.bytes ELSE 0 END) AS lan_download_bytes,
                                                   SUM(CASE WHEN t.direction = 'outbound' AND t.scope_type = 'loopback' THEN t.bytes ELSE 0 END) AS loopback_upload_bytes,
                                                   SUM(CASE WHEN t.direction = 'inbound' AND t.scope_type = 'loopback' THEN t.bytes ELSE 0 END) AS loopback_download_bytes,
                                                   SUM(CASE WHEN t.direction = 'outbound' AND t.scope_type = 'other' THEN t.bytes ELSE 0 END) AS other_upload_bytes,
                                                   SUM(CASE WHEN t.direction = 'inbound' AND t.scope_type = 'other' THEN t.bytes ELSE 0 END) AS other_download_bytes,
                                                   {rankExpression} AS rank_bytes
                                            FROM traffic t
                                            JOIN app_registry a ON a.id = t.app_id
                                            GROUP BY a.app_key, a.process_name, a.display_name, a.executable_path
                                            HAVING rank_bytes > 0
                                            ORDER BY rank_bytes DESC, a.process_name ASC
                                            LIMIT $topN;
                                            """);

        return sqlBuilder.ToString();
    }

    private static string BuildAllAppSummariesSql(
        string trafficSourceSql,
        TrafficScopeFilter scopeFilter,
        TrafficDirectionFilter directionFilter)
    {
        var orderByExpression = GetOrderByExpression(scopeFilter, directionFilter, "t");
        var sql = """
                  WITH traffic AS (
                  """;
        sql += Environment.NewLine + trafficSourceSql + Environment.NewLine;
        sql += """
                  )
                  SELECT a.app_key,
                         a.process_name,
                         a.display_name,
                         a.executable_path,
                         SUM(CASE WHEN t.direction = 'outbound' THEN t.bytes ELSE 0 END) AS total_upload_bytes,
                         SUM(CASE WHEN t.direction = 'inbound' THEN t.bytes ELSE 0 END) AS total_download_bytes,
                         SUM(CASE WHEN t.direction = 'outbound' AND t.scope_type = 'wan' THEN t.bytes ELSE 0 END) AS wan_upload_bytes,
                         SUM(CASE WHEN t.direction = 'inbound' AND t.scope_type = 'wan' THEN t.bytes ELSE 0 END) AS wan_download_bytes,
                         SUM(CASE WHEN t.direction = 'outbound' AND t.scope_type = 'lan' THEN t.bytes ELSE 0 END) AS lan_upload_bytes,
                         SUM(CASE WHEN t.direction = 'inbound' AND t.scope_type = 'lan' THEN t.bytes ELSE 0 END) AS lan_download_bytes,
                         SUM(CASE WHEN t.direction = 'outbound' AND t.scope_type = 'loopback' THEN t.bytes ELSE 0 END) AS loopback_upload_bytes,
                         SUM(CASE WHEN t.direction = 'inbound' AND t.scope_type = 'loopback' THEN t.bytes ELSE 0 END) AS loopback_download_bytes,
                         SUM(CASE WHEN t.direction = 'outbound' AND t.scope_type = 'other' THEN t.bytes ELSE 0 END) AS other_upload_bytes,
                         SUM(CASE WHEN t.direction = 'inbound' AND t.scope_type = 'other' THEN t.bytes ELSE 0 END) AS other_download_bytes
                  FROM traffic t
                  JOIN app_registry a ON a.id = t.app_id
                  GROUP BY a.app_key, a.process_name, a.display_name, a.executable_path
                  ORDER BY
                  """;

        return sql + Environment.NewLine + orderByExpression + " DESC, a.process_name ASC;";
    }

    private static void AppendTrafficFilters(
        StringBuilder sqlBuilder,
        SqliteCommand command,
        TrafficScopeFilter scopeFilter,
        TrafficDirectionFilter directionFilter,
        string tableAlias)
    {
        if (scopeFilter is not TrafficScopeFilter.All)
        {
            sqlBuilder.AppendLine($"  AND {tableAlias}.scope_type = $scopeType");
            command.Parameters.AddWithValue("$scopeType", MapScopeFilterToDbScope(scopeFilter));
        }

        if (directionFilter is not TrafficDirectionFilter.Total)
        {
            sqlBuilder.AppendLine($"  AND {tableAlias}.direction = $direction");
            command.Parameters.AddWithValue("$direction", MapDirectionFilterToDbDirection(directionFilter));
        }
    }

    private static string GetOrderByExpression(
        TrafficScopeFilter scopeFilter,
        TrafficDirectionFilter directionFilter,
        string tableAlias)
    {
        return (scopeFilter, directionFilter) switch
        {
            (TrafficScopeFilter.Wan, TrafficDirectionFilter.Upload) =>
                $"SUM(CASE WHEN {tableAlias}.direction = 'outbound' AND {tableAlias}.scope_type = 'wan' THEN {tableAlias}.bytes ELSE 0 END)",
            (TrafficScopeFilter.Wan, TrafficDirectionFilter.Download) =>
                $"SUM(CASE WHEN {tableAlias}.direction = 'inbound' AND {tableAlias}.scope_type = 'wan' THEN {tableAlias}.bytes ELSE 0 END)",
            (TrafficScopeFilter.Wan, TrafficDirectionFilter.Total) =>
                $"SUM(CASE WHEN {tableAlias}.scope_type = 'wan' THEN {tableAlias}.bytes ELSE 0 END)",
            (TrafficScopeFilter.Lan, TrafficDirectionFilter.Upload) =>
                $"SUM(CASE WHEN {tableAlias}.direction = 'outbound' AND {tableAlias}.scope_type = 'lan' THEN {tableAlias}.bytes ELSE 0 END)",
            (TrafficScopeFilter.Lan, TrafficDirectionFilter.Download) =>
                $"SUM(CASE WHEN {tableAlias}.direction = 'inbound' AND {tableAlias}.scope_type = 'lan' THEN {tableAlias}.bytes ELSE 0 END)",
            (TrafficScopeFilter.Lan, TrafficDirectionFilter.Total) =>
                $"SUM(CASE WHEN {tableAlias}.scope_type = 'lan' THEN {tableAlias}.bytes ELSE 0 END)",
            (TrafficScopeFilter.Loopback, TrafficDirectionFilter.Upload) =>
                $"SUM(CASE WHEN {tableAlias}.direction = 'outbound' AND {tableAlias}.scope_type = 'loopback' THEN {tableAlias}.bytes ELSE 0 END)",
            (TrafficScopeFilter.Loopback, TrafficDirectionFilter.Download) =>
                $"SUM(CASE WHEN {tableAlias}.direction = 'inbound' AND {tableAlias}.scope_type = 'loopback' THEN {tableAlias}.bytes ELSE 0 END)",
            (TrafficScopeFilter.Loopback, TrafficDirectionFilter.Total) =>
                $"SUM(CASE WHEN {tableAlias}.scope_type = 'loopback' THEN {tableAlias}.bytes ELSE 0 END)",
            (TrafficScopeFilter.All, TrafficDirectionFilter.Upload) =>
                $"SUM(CASE WHEN {tableAlias}.direction = 'outbound' THEN {tableAlias}.bytes ELSE 0 END)",
            (TrafficScopeFilter.All, TrafficDirectionFilter.Download) =>
                $"SUM(CASE WHEN {tableAlias}.direction = 'inbound' THEN {tableAlias}.bytes ELSE 0 END)",
            _ =>
                $"SUM({tableAlias}.bytes)"
        };
    }

    private static string MapScopeFilterToDbScope(TrafficScopeFilter scopeFilter) => scopeFilter switch
    {
        TrafficScopeFilter.Wan => "wan",
        TrafficScopeFilter.Lan => "lan",
        TrafficScopeFilter.Loopback => "loopback",
        _ => throw new ArgumentOutOfRangeException(nameof(scopeFilter), scopeFilter, null)
    };

    private static string MapDirectionFilterToDbDirection(TrafficDirectionFilter directionFilter) => directionFilter switch
    {
        TrafficDirectionFilter.Upload => "outbound",
        TrafficDirectionFilter.Download => "inbound",
        _ => throw new ArgumentOutOfRangeException(nameof(directionFilter), directionFilter, null)
    };

    private static DateTimeOffset AlignDownToRollupWindow(DateTimeOffset value)
    {
        var utcValue = value.ToUniversalTime();
        var alignedTicks = utcValue.Ticks - utcValue.Ticks % RollupWindowDuration.Ticks;
        return new DateTimeOffset(alignedTicks, TimeSpan.Zero);
    }

    private static DateTimeOffset AlignUpToRollupWindow(DateTimeOffset value)
    {
        var utcValue = value.ToUniversalTime();
        var alignedValue = AlignDownToRollupWindow(utcValue);
        return alignedValue == utcValue ? alignedValue : alignedValue.Add(RollupWindowDuration);
    }

    private static IEnumerable<DateTimeOffset> EnumerateRollupWindowStarts(
        DateTimeOffset from,
        DateTimeOffset to)
    {
        for (var windowStart = from; windowStart < to; windowStart = windowStart.Add(RollupWindowDuration))
        {
            yield return windowStart;
        }
    }

    private static IReadOnlyList<TimeRange> BuildSegmentRanges(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan segmentDuration)
    {
        var rangeFrom = from.ToUniversalTime();
        var rangeTo = to.ToUniversalTime();
        if (rangeFrom >= rangeTo)
        {
            return Array.Empty<TimeRange>();
        }

        if (segmentDuration == RollupWindowDuration)
        {
            return BuildRollupAlignedSegmentRanges(rangeFrom, rangeTo);
        }

        var ranges = new List<TimeRange>();
        for (var segmentFrom = rangeFrom; segmentFrom < rangeTo; segmentFrom = segmentFrom.Add(segmentDuration))
        {
            var segmentTo = segmentFrom.Add(segmentDuration);
            ranges.Add(new TimeRange(segmentFrom, segmentTo < rangeTo ? segmentTo : rangeTo));
        }

        return ranges;
    }

    private static IReadOnlyList<TimeRange> BuildRollupAlignedSegmentRanges(
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var ranges = new List<TimeRange>();
        var segmentFrom = from;
        var nextAlignedWindowStart = AlignUpToRollupWindow(segmentFrom);
        if (segmentFrom < nextAlignedWindowStart)
        {
            var segmentTo = nextAlignedWindowStart < to ? nextAlignedWindowStart : to;
            ranges.Add(new TimeRange(segmentFrom, segmentTo));
            segmentFrom = segmentTo;
        }

        while (segmentFrom < to)
        {
            var segmentTo = segmentFrom.Add(RollupWindowDuration);
            ranges.Add(new TimeRange(segmentFrom, segmentTo < to ? segmentTo : to));
            segmentFrom = segmentTo;
        }

        return ranges;
    }

    private static string ToDbTime(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

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

    private readonly record struct TimeRange(DateTimeOffset From, DateTimeOffset To);

    private sealed record TrafficSourcePlan(
        IReadOnlyList<DateTimeOffset> RollupWindowStarts,
        IReadOnlyList<TimeRange> RawRanges);

    private sealed record TrafficSourceFilter(string? ScopeType, string? Direction)
    {
        public static TrafficSourceFilter Empty { get; } = new(null, null);
    }
}
