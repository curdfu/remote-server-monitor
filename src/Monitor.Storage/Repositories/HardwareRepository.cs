using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Monitor.Hardware.Models;
using Monitor.Storage.Abstractions;
using Monitor.Storage.Services;

namespace Monitor.Storage.Repositories;

public sealed class HardwareRepository(IDbConnectionFactory dbConnectionFactory, ILogger<HardwareRepository> logger)
{
    private const long MinuteTicks = TimeSpan.TicksPerMinute;
    private const int CandidatesPerBucket = 10;
    private const int MaximumAnchorsPerBucket = 6;
    private static readonly TimeSpan ShortHistoryWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan AnchorInterval = TimeSpan.FromSeconds(10);

    public Task SaveAsync(HardwareSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return SaveBatchAsync([snapshot], cancellationToken);
    }

    public Task SaveBatchAsync(IReadOnlyCollection<HardwareSnapshot> snapshots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        return snapshots.Count == 0 ? Task.CompletedTask : SqliteBusyRetry.ExecuteAsync(token => SaveBatchCoreAsync(snapshots, token), cancellationToken);
    }

    private async Task SaveBatchCoreAsync(IReadOnlyCollection<HardwareSnapshot> snapshots, CancellationToken cancellationToken)
    {
        await using var connection = await dbConnectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO hardware_samples(sample_time, cpu_usage_percent, cpu_temperature_c, cpu_frequency_mhz,
                memory_total_mb, memory_used_mb, memory_usage_percent, disk_temperature_c, uptime_seconds)
            VALUES ($time, $cpu, $temperature, $frequency, $total, $used, $memory, $disk, $uptime);
            """;
        foreach (var name in new[] { "$time", "$cpu", "$temperature", "$frequency", "$total", "$used", "$memory", "$disk", "$uptime" })
            command.Parameters.AddWithValue(name, DBNull.Value);
        var minutes = new HashSet<DateTimeOffset>();
        foreach (var snapshot in snapshots)
        {
            var time = snapshot.SampleTime.ToUniversalTime();
            command.Parameters["$time"].Value = DbTime(time);
            command.Parameters["$cpu"].Value = Value(snapshot.Cpu.UsagePercent);
            command.Parameters["$temperature"].Value = Value(snapshot.Cpu.TemperatureC);
            command.Parameters["$frequency"].Value = Value(snapshot.Cpu.FrequencyMhz);
            command.Parameters["$total"].Value = Value(snapshot.Memory.TotalMb);
            command.Parameters["$used"].Value = Value(snapshot.Memory.UsedMb);
            command.Parameters["$memory"].Value = Value(snapshot.Memory.UsagePercent);
            command.Parameters["$disk"].Value = Value(snapshot.Disk.TemperatureC);
            command.Parameters["$uptime"].Value = snapshot.System.UptimeSeconds;
            await command.ExecuteNonQueryAsync(cancellationToken);
            minutes.Add(AlignMinute(time));
        }
        var currentMinute = AlignMinute(DateTimeOffset.UtcNow);
        foreach (var minute in minutes.Where(minute => minute < currentMinute))
        {
            await using var invalidate = connection.CreateCommand();
            invalidate.Transaction = transaction;
            invalidate.CommandText = """
                DELETE FROM hardware_minute_candidates WHERE minute_start = $minute;
                DELETE FROM hardware_minute_windows WHERE minute_start = $minute;
                DELETE FROM hardware_minute_anchor_windows WHERE minute_start = $minute;
                UPDATE storage_progress SET value = MIN(value, $minute) WHERE name = 'hardware_minute_cursor';
                UPDATE storage_progress SET value = MIN(value, $minute) WHERE name = 'hardware_anchor_cursor';
                """;
            invalidate.Parameters.AddWithValue("$minute", DbTime(minute));
            await invalidate.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        logger.LogDebug("Inserted {Count} hardware samples into SQLite.", snapshots.Count);
    }

    // Each minute is independently reconstructible from raw rows. Progress and the marker
    // commit together; late writes invalidate the minute in the same transaction as the insert.
    public async Task<int> BackfillMinutesAsync(int maxMinutes, CancellationToken cancellationToken = default)
    {
        if (maxMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(maxMinutes));
        var processed = 0;
        while (processed < maxMinutes)
        {
            var found = await SqliteBusyRetry.ExecuteAsync(async token =>
            {
                await using var connection = await dbConnectionFactory.OpenConnectionAsync(token);
                await using var transaction = connection.BeginTransaction();
                await using var find = connection.CreateCommand();
                find.Transaction = transaction;
                find.CommandText = """
                    SELECT sample_time FROM hardware_samples
                    WHERE sample_time >= COALESCE((SELECT value FROM storage_progress WHERE name = 'hardware_anchor_cursor'), '')
                      AND sample_time < $current
                    ORDER BY sample_time LIMIT 1;
                    """;
                find.Parameters.AddWithValue("$current", DbTime(AlignMinute(DateTimeOffset.UtcNow)));
                var result = await find.ExecuteScalarAsync(token);
                if (result is not string text) return false;
                var minute = AlignMinute(DateTimeOffset.Parse(text).ToUniversalTime());
                await using (var exists = connection.CreateCommand())
                {
                    exists.Transaction = transaction;
                    exists.CommandText = "SELECT EXISTS(SELECT 1 FROM hardware_minute_anchor_windows WHERE minute_start = $minute);";
                    exists.Parameters.AddWithValue("$minute", DbTime(minute));
                    if (Convert.ToInt32(await exists.ExecuteScalarAsync(token)) != 0)
                    {
                        await using var advance = connection.CreateCommand();
                        advance.Transaction = transaction;
                        advance.CommandText = """
                            INSERT INTO storage_progress(name, value) VALUES ('hardware_anchor_cursor', $next)
                            ON CONFLICT(name) DO UPDATE SET value = excluded.value;
                            """;
                        advance.Parameters.AddWithValue("$next", DbTime(minute.AddMinutes(1)));
                        await advance.ExecuteNonQueryAsync(token);
                        await transaction.CommitAsync(token);
                        return true;
                    }
                }
                var end = minute.AddMinutes(1);
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    DELETE FROM hardware_minute_candidates WHERE minute_start = $from;
                    DELETE FROM hardware_minute_windows WHERE minute_start = $from;
                    DELETE FROM hardware_minute_anchor_windows WHERE minute_start = $from;
                    INSERT OR IGNORE INTO hardware_minute_candidates(minute_start, sample_id)
                    SELECT $from, id FROM hardware_samples WHERE id IN (
                        SELECT id FROM hardware_samples WHERE sample_time >= $from AND sample_time < $to ORDER BY sample_time ASC, id ASC LIMIT 1
                    );
                    """;
                update.Parameters.AddWithValue("$from", DbTime(minute));
                update.Parameters.AddWithValue("$to", DbTime(end));
                await update.ExecuteNonQueryAsync(token);
                foreach (var order in new[] { "sample_time DESC", "cpu_usage_percent ASC", "cpu_usage_percent DESC", "memory_usage_percent ASC", "memory_usage_percent DESC", "cpu_temperature_c ASC", "cpu_temperature_c DESC", "disk_temperature_c ASC", "disk_temperature_c DESC" })
                {
                    var column = order.Split(' ')[0];
                    await using var candidate = connection.CreateCommand();
                    candidate.Transaction = transaction;
                    candidate.CommandText = $"""
                        INSERT OR IGNORE INTO hardware_minute_candidates(minute_start, sample_id)
                        SELECT $from, id FROM hardware_samples
                        WHERE sample_time >= $from AND sample_time < $to AND {column} IS NOT NULL
                        ORDER BY {order}, id ASC LIMIT 1;
                        """;
                    candidate.Parameters.AddWithValue("$from", DbTime(minute));
                    candidate.Parameters.AddWithValue("$to", DbTime(end));
                    await candidate.ExecuteNonQueryAsync(token);
                }
                // One real sample from each ten-second slice bounds the apparent gap
                // for steady one-second sampling without inventing data during outages.
                for (var offset = 0; offset < 60; offset += 10)
                {
                    await using var anchor = connection.CreateCommand();
                    anchor.Transaction = transaction;
                    anchor.CommandText = """
                        INSERT OR IGNORE INTO hardware_minute_candidates(minute_start, sample_id)
                        SELECT $minute, id FROM hardware_samples
                        WHERE sample_time >= $from AND sample_time < $to
                        ORDER BY sample_time, id LIMIT 1;
                        """;
                    anchor.Parameters.AddWithValue("$minute", DbTime(minute));
                    anchor.Parameters.AddWithValue("$from", DbTime(minute.AddSeconds(offset)));
                    anchor.Parameters.AddWithValue("$to", DbTime(minute.AddSeconds(offset + 10)));
                    await anchor.ExecuteNonQueryAsync(token);
                }
                await using var marker = connection.CreateCommand();
                marker.Transaction = transaction;
                marker.CommandText = """
                    INSERT INTO hardware_minute_windows(minute_start) VALUES ($from);
                    INSERT INTO hardware_minute_anchor_windows(minute_start) VALUES ($from);
                    INSERT INTO storage_progress(name, value) VALUES ('hardware_anchor_cursor', $to)
                    ON CONFLICT(name) DO UPDATE SET value = excluded.value;
                    """;
                marker.Parameters.AddWithValue("$from", DbTime(minute));
                marker.Parameters.AddWithValue("$to", DbTime(end));
                await marker.ExecuteNonQueryAsync(token);
                await transaction.CommitAsync(token);
                return true;
            }, cancellationToken);
            if (!found) break;
            processed++;
        }
        return processed;
    }

    public async Task<IReadOnlyList<HardwareSnapshot>> QueryRangeAsync(DateTimeOffset from, DateTimeOffset to,
        int? maxPoints = null, CancellationToken cancellationToken = default)
    {
        from = from.ToUniversalTime();
        to = to.ToUniversalTime();
        if (from >= to) return Array.Empty<HardwareSnapshot>();
        await using var connection = await dbConnectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var results = new List<Sample>();
        if (maxPoints is not > 0)
        {
            await ReadSamplesAsync(connection, transaction, from, to, false, results, cancellationToken);
            return results.Select(x => x.Snapshot).ToArray();
        }
        var budget = Math.Min(maxPoints.Value, 2000);
        await ReadSamplesAsync(connection, transaction, from, to, false, results, cancellationToken, limit: budget + 1);
        if (results.Count <= budget) return results.Select(x => x.Snapshot).ToArray();
        results.Clear();

        // UTC-aligned buckets are whole multiples of one minute, so an interior minute's
        // extrema remain extrema candidates for exactly one display bucket. Short ranges
        // also reserve six ten-second anchors per bucket to preserve chart cadence.
        var anchorTicks = to - from <= ShortHistoryWindow && budget >= 1200 ? AnchorInterval.Ticks : 0;
        var candidatesPerBucket = CandidatesPerBucket + (anchorTicks == 0 ? 0 : MaximumAnchorsPerBucket);
        var bucketCount = Math.Max(1, budget / candidatesPerBucket - 1);
        var minutesPerBucket = Math.Max(1L, ((to - from).Ticks + bucketCount * MinuteTicks - 1) / (bucketCount * MinuteTicks));
        var bucketTicks = minutesPerBucket * MinuteTicks;
        var buckets = new Dictionary<long, Bucket>();
        var fullFrom = AlignMinute(from);
        if (fullFrom < from) fullFrom = fullFrom.AddMinutes(1);
        var fullTo = AlignMinute(to);
        var cursor = from;
        await using (var marker = connection.CreateCommand())
        {
            marker.Transaction = transaction;
            var markerTable = anchorTicks > 0 ? "hardware_minute_anchor_windows" : "hardware_minute_windows";
            marker.CommandText = $"SELECT minute_start FROM {markerTable} WHERE minute_start >= $from AND minute_start < $to ORDER BY minute_start;";
            marker.Parameters.AddWithValue("$from", DbTime(fullFrom));
            marker.Parameters.AddWithValue("$to", DbTime(fullTo));
            var ranges = new List<(DateTimeOffset From, DateTimeOffset To)>();
            await using (var reader = await marker.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var start = reader.GetDateTimeOffset(0).ToUniversalTime();
                    if (ranges.Count > 0 && ranges[^1].To == start) ranges[^1] = (ranges[^1].From, start.AddMinutes(1));
                    else ranges.Add((start, start.AddMinutes(1)));
                }
            }
            foreach (var range in ranges)
            {
                if (cursor < range.From)
                {
                    results.Clear();
                    await ReadSamplesAsync(connection, transaction, cursor, range.From, false, results, cancellationToken, buckets, bucketTicks, anchorTicks);
                }
                results.Clear();
                await ReadSamplesAsync(connection, transaction, range.From, range.To, true, results, cancellationToken, buckets, bucketTicks, anchorTicks);
                cursor = range.To;
            }
        }
        if (cursor < to)
            await ReadSamplesAsync(connection, transaction, cursor, to, false, results, cancellationToken, buckets, bucketTicks, anchorTicks);
        var selected = buckets.OrderBy(x => x.Key).SelectMany(x => x.Value.Selected()).DistinctBy(x => x.Id)
            .OrderBy(x => x.Snapshot.SampleTime).ThenBy(x => x.Id).ToList();
        // Tiny caller budgets cannot represent all extrema; keep endpoints before filling.
        if (selected.Count > budget)
        {
            var kept = new List<Sample> { selected[0] };
            if (budget > 1) kept.Add(selected[^1]);
            kept.AddRange(selected.Skip(1).Take(Math.Max(0, budget - kept.Count)));
            selected = kept.OrderBy(x => x.Snapshot.SampleTime).ThenBy(x => x.Id).ToList();
        }
        return selected.Select(x => x.Snapshot).ToArray();
    }

    private static async Task ReadSamplesAsync(SqliteConnection connection, SqliteTransaction transaction,
        DateTimeOffset from, DateTimeOffset to, bool summarized, List<Sample> results, CancellationToken token,
        Dictionary<long, Bucket>? buckets = null, long bucketTicks = 1, long anchorTicks = 0, int? limit = null)
    {
        if (from >= to) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var source = summarized
            ? "hardware_minute_candidates c JOIN hardware_samples s ON s.id = c.sample_id"
            : "hardware_samples s";
        var time = summarized ? "c.minute_start" : "s.sample_time";
        command.CommandText = $"""
            SELECT s.id, s.sample_time, s.cpu_usage_percent, s.cpu_temperature_c, s.cpu_frequency_mhz,
                   s.memory_total_mb, s.memory_used_mb, s.memory_usage_percent, s.disk_temperature_c, s.uptime_seconds
            FROM {source} WHERE {time} >= $from AND {time} < $to ORDER BY {time}, s.id
            {(limit.HasValue ? "LIMIT $limit" : "")};
            """;
        if (limit.HasValue) command.Parameters.AddWithValue("$limit", limit.Value);
        command.Parameters.AddWithValue("$from", DbTime(from));
        command.Parameters.AddWithValue("$to", DbTime(to));
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var sample = new Sample(reader.GetInt64(0), new HardwareSnapshot
            {
                SampleTime = reader.GetDateTimeOffset(1).ToUniversalTime(),
                Cpu = new CpuMetrics { UsagePercent = Number(reader, 2), TemperatureC = Number(reader, 3), FrequencyMhz = Number(reader, 4) },
                Memory = new MemoryMetrics { TotalMb = Number(reader, 5), UsedMb = Number(reader, 6), UsagePercent = Number(reader, 7) },
                Disk = new DiskMetrics { TemperatureC = Number(reader, 8) },
                System = new SystemMetrics { UptimeSeconds = reader.GetInt64(9) }
            });
            if (buckets is null) results.Add(sample);
            else
            {
                var key = sample.Snapshot.SampleTime.Ticks / bucketTicks;
                if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new Bucket(anchorTicks);
                bucket.Add(sample);
            }
        }
    }

    private sealed record Sample(long Id, HardwareSnapshot Snapshot);
    private sealed class Bucket(long anchorTicks)
    {
        private readonly Sample?[] _values = new Sample?[CandidatesPerBucket];
        private readonly Dictionary<long, Sample> _anchors = new();
        public void Add(Sample sample)
        {
            if (anchorTicks > 0)
            {
                var anchor = sample.Snapshot.SampleTime.Ticks / anchorTicks;
                if (!_anchors.TryGetValue(anchor, out var previous) || Compare(sample, previous) < 0)
                    _anchors[anchor] = sample;
            }
            if (_values[0] is null || Compare(sample, _values[0]!) < 0) _values[0] = sample;
            if (_values[1] is null || Compare(sample, _values[1]!) > 0) _values[1] = sample;
            Update(sample, 2, x => x.Cpu.UsagePercent);
            Update(sample, 4, x => x.Memory.UsagePercent);
            Update(sample, 6, x => x.Cpu.TemperatureC);
            Update(sample, 8, x => x.Disk.TemperatureC);
        }
        private void Update(Sample sample, int index, Func<HardwareSnapshot, double?> selector)
        {
            var value = selector(sample.Snapshot);
            if (!value.HasValue) return;
            if (_values[index] is null || value < selector(_values[index]!.Snapshot)) _values[index] = sample;
            if (_values[index + 1] is null || value > selector(_values[index + 1]!.Snapshot)) _values[index + 1] = sample;
        }
        private static int Compare(Sample left, Sample right)
        {
            var time = left.Snapshot.SampleTime.CompareTo(right.Snapshot.SampleTime);
            return time != 0 ? time : left.Id.CompareTo(right.Id);
        }
        public IEnumerable<Sample> Selected() => _values.OfType<Sample>().Concat(_anchors.Values);
    }

    private static DateTimeOffset AlignMinute(DateTimeOffset value) => new(value.ToUniversalTime().Ticks / MinuteTicks * MinuteTicks, TimeSpan.Zero);
    private static string DbTime(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static object Value(double? value) => value.HasValue ? value.Value : DBNull.Value;
    private static double? Number(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
}
