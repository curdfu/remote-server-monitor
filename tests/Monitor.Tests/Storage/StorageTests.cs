using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monitor.Contracts.Dtos;
using Monitor.Contracts.Options;
using Monitor.Hardware.Models;
using Monitor.Network.Abstractions;
using Monitor.Network.Enums;
using Monitor.Network.Models;
using Monitor.Storage.Abstractions;
using Monitor.Storage.Repositories;
using Monitor.Storage.Services;

namespace Monitor.Tests.Storage;

public sealed class StorageTests
{
    [Fact]
    public async Task HardwareUsesUtcAndPreservesExtremaAndBudgetAfterBackfill()
    {
        await using var db = await TestDatabase.CreateAsync();
        var repository = db.Hardware;
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var samples = Enumerable.Range(0, 8000).Select(i => new HardwareSnapshot
        {
            SampleTime = start.AddSeconds(i * 5).ToOffset(TimeSpan.FromHours(8)),
            Cpu = new CpuMetrics { UsagePercent = i == 1755 ? 100 : 10, TemperatureC = i == 2300 ? 99 : 30 },
            Memory = new MemoryMetrics { UsagePercent = i == 3477 ? 98 : 40 },
            Disk = new DiskMetrics { TemperatureC = i == 4545 ? 97 : 35 },
            System = new SystemMetrics { UptimeSeconds = i }
        }).ToArray();
        await repository.SaveBatchAsync(samples);
        var before = await repository.QueryRangeAsync(start.ToOffset(TimeSpan.FromHours(-5)), start.AddSeconds(40000), 2000);
        Assert.InRange(before.Count, 1, 2000);
        Assert.Equal(start, before[0].SampleTime);
        Assert.Equal(start.AddSeconds(7999 * 5), before[^1].SampleTime);
        Assert.Contains(before, x => x.Cpu.UsagePercent == 100);
        Assert.Contains(before, x => x.Memory.UsagePercent == 98);
        Assert.Contains(before, x => x.Cpu.TemperatureC == 99);
        Assert.Contains(before, x => x.Disk.TemperatureC == 97);
        var rounds = 0;
        while (await repository.BackfillMinutesAsync(50) == 50) rounds++;
        Assert.True(rounds > 0);
        var after = await repository.QueryRangeAsync(start, start.AddSeconds(40000), 2000);
        Assert.Equal(before.Select(x => x.SampleTime), after.Select(x => x.SampleTime));
        var edge = await repository.QueryRangeAsync(start.AddSeconds(5).ToOffset(TimeSpan.FromHours(3)), start.AddSeconds(15), null);
        Assert.Equal(2, edge.Count);
        Assert.Equal(start.AddSeconds(5), edge[0].SampleTime);
        await repository.SaveAsync(new HardwareSnapshot { SampleTime = start.AddSeconds(10), Cpu = new CpuMetrics { UsagePercent = 101 } });
        var late = await repository.QueryRangeAsync(start, start.AddSeconds(60), 2000);
        Assert.Contains(late, x => x.Cpu.UsagePercent == 101);
        while (await repository.BackfillMinutesAsync(50) == 50) { }
        var lateAfterBackfill = await repository.QueryRangeAsync(start, start.AddSeconds(40000), 2000);
        Assert.Contains(lateAfterBackfill, x => x.Cpu.UsagePercent == 101);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneHourHistoryKeepsCadenceAndRealGap(bool backfill)
    {
        await using var db = await TestDatabase.CreateAsync();
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var continuous = Enumerable.Range(0, 3600).Select(i => new HardwareSnapshot
        {
            SampleTime = start.AddSeconds(i),
            Cpu = new CpuMetrics { UsagePercent = i == 1577 ? 95 : 20 },
            Memory = new MemoryMetrics { UsagePercent = i == 1677 ? 99 : 40 },
            System = new SystemMetrics { UptimeSeconds = i }
        }).ToArray();
        await db.Hardware.SaveBatchAsync(continuous);
        var withGapStart = start.AddDays(1);
        await db.Hardware.SaveBatchAsync(Enumerable.Range(0, 3600)
            .Where(i => i < 600 || i >= 3000)
            .Select(i => new HardwareSnapshot { SampleTime = withGapStart.AddSeconds(i) }).ToArray());
        if (backfill)
            while (await db.Hardware.BackfillMinutesAsync(50) == 50) { }

        var values = await db.Hardware.QueryRangeAsync(start, start.AddHours(1), 2000);
        Assert.InRange(values.Count, 1, 2000);
        Assert.Equal(start, values[0].SampleTime);
        Assert.Equal(start.AddSeconds(3599), values[^1].SampleTime);
        Assert.All(values.Zip(values.Skip(1)), pair =>
            Assert.InRange((pair.Second.SampleTime - pair.First.SampleTime).TotalSeconds, 0, 15));
        Assert.Contains(values, sample => sample.Cpu.UsagePercent == 95);
        Assert.Contains(values, sample => sample.Memory.UsagePercent == 99);

        // Double the 1,200 raw samples to exercise reduction across the outage.
        await db.Hardware.SaveBatchAsync(Enumerable.Range(0, 3600)
            .Where(i => i < 600 || i >= 3000)
            .Select(i => new HardwareSnapshot { SampleTime = withGapStart.AddSeconds(i).AddMilliseconds(500) }).ToArray());
        if (backfill)
            while (await db.Hardware.BackfillMinutesAsync(50) == 50) { }
        var gapValues = await db.Hardware.QueryRangeAsync(withGapStart, withGapStart.AddHours(1), 2000);
        Assert.InRange(gapValues.Count, 1, 2000);
        Assert.Contains(gapValues.Zip(gapValues.Skip(1)), pair =>
            pair.Second.SampleTime - pair.First.SampleTime >= TimeSpan.FromMinutes(39));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HalfSecondOffsetWindowRetainsCadenceAndExtrema(bool backfill)
    {
        await using var db = await TestDatabase.CreateAsync();
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 13, TimeSpan.Zero).AddMilliseconds(250);
        var samples = Enumerable.Range(0, 7200).Select(i => new HardwareSnapshot
        {
            SampleTime = start.AddMilliseconds(i * 500),
            Cpu = new CpuMetrics { UsagePercent = i == 3457 ? 100 : 20, TemperatureC = i == 1235 ? 98 : 30 },
            Memory = new MemoryMetrics { UsagePercent = i == 2345 ? 99 : 40 },
            Disk = new DiskMetrics { TemperatureC = i == 4567 ? 97 : 30 }
        }).ToArray();
        await db.Hardware.SaveBatchAsync(samples);
        if (backfill)
            while (await db.Hardware.BackfillMinutesAsync(50) == 50) { }
        var values = await db.Hardware.QueryRangeAsync(start.ToOffset(TimeSpan.FromHours(8)), start.AddHours(1), 2000);
        Assert.InRange(values.Count, 1, 2000);
        Assert.Equal(samples[0].SampleTime, values[0].SampleTime);
        Assert.Equal(samples[^1].SampleTime, values[^1].SampleTime);
        Assert.All(values.Zip(values.Skip(1)), pair =>
            Assert.InRange((pair.Second.SampleTime - pair.First.SampleTime).TotalSeconds, 0, 15));
        Assert.Contains(values, x => x.Cpu.UsagePercent == 100);
        Assert.Contains(values, x => x.Cpu.TemperatureC == 98);
        Assert.Contains(values, x => x.Memory.UsagePercent == 99);
        Assert.Contains(values, x => x.Disk.TemperatureC == 97);
    }

    [Fact]
    public async Task DashboardExcludesIgnoredBeforeLimitAndPreservesFilterTotals()
    {
        await using var db = await TestDatabase.CreateAsync();
        var start = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var buckets = Enumerable.Range(0, 140).Select(i => Bucket(start.AddMinutes(1), $"app{i:D3}", 1000 - i)).ToArray();
        await db.Network.SaveAsync(buckets);
        await using (var connection = await db.Factory.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO ignored_network_apps(app_key,process_name,created_at,updated_at) SELECT app_key,process_name,$time,$time FROM app_registry WHERE app_key < 'app080';";
            command.Parameters.AddWithValue("$time", start.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        var dashboard = await db.Network.QueryDashboardAsync(start, start.AddHours(1), NetworkTrafficRepository.TrafficScopeFilter.Wan, NetworkTrafficRepository.TrafficDirectionFilter.Upload, 100);
        Assert.Equal(60, dashboard.Apps.Count);
        Assert.Equal(80, dashboard.IgnoredApps.Count);
        Assert.DoesNotContain(dashboard.Apps, x => string.CompareOrdinal(x.AppKey, "app080") < 0);
        Assert.Equal(buckets.Sum(x => x.Bytes), dashboard.Overview.TotalUploadBytes);
        Assert.Equal(dashboard.Overview.TotalUploadBytes, dashboard.Totals.TotalUploadBytes);
    }

    [Fact]
    public async Task DashboardFiltersMatchIndependentQueriesAndUseIndexedRanges()
    {
        await using var db = await TestDatabase.CreateAsync();
        var start = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var values = new[]
        {
            new TrafficBucket { BucketStartTime = start, AppKey = "a", Direction = TrafficDirection.Outbound, ScopeType = AddressScopeType.Wan, Bytes = 12 },
            new TrafficBucket { BucketStartTime = start.AddMinutes(1), AppKey = "a", Direction = TrafficDirection.Inbound, ScopeType = AddressScopeType.Wan, Bytes = 23 },
            new TrafficBucket { BucketStartTime = start.AddMinutes(2), AppKey = "b", Direction = TrafficDirection.Outbound, ScopeType = AddressScopeType.Lan, Bytes = 34 },
            new TrafficBucket { BucketStartTime = start.AddMinutes(3), AppKey = "b", Direction = TrafficDirection.Inbound, ScopeType = AddressScopeType.Loopback, Bytes = 45 },
            new TrafficBucket { BucketStartTime = start.AddHours(1), AppKey = "b", Direction = TrafficDirection.Outbound, ScopeType = AddressScopeType.Wan, Bytes = 999 }
        };
        await db.Network.SaveAsync(values);
        foreach (var scope in Enum.GetValues<NetworkTrafficRepository.TrafficScopeFilter>())
        foreach (var direction in Enum.GetValues<NetworkTrafficRepository.TrafficDirectionFilter>())
        {
            var dashboard = await db.Network.QueryDashboardAsync(start.ToOffset(TimeSpan.FromHours(8)), start.AddHours(1), scope, direction, 100);
            var overview = await db.Network.QueryTotalsAsync(start, start.AddHours(1), NetworkTrafficRepository.TrafficScopeFilter.All, direction);
            var totals = await db.Network.QueryTotalsAsync(start, start.AddHours(1), scope, NetworkTrafficRepository.TrafficDirectionFilter.Total);
            Assert.Equal(overview.TotalUploadBytes, dashboard.Overview.TotalUploadBytes);
            Assert.Equal(overview.TotalDownloadBytes, dashboard.Overview.TotalDownloadBytes);
            Assert.Equal(overview.WanUploadBytes, dashboard.Overview.WanUploadBytes);
            Assert.Equal(totals.TotalUploadBytes, dashboard.Totals.TotalUploadBytes);
            Assert.Equal(totals.TotalDownloadBytes, dashboard.Totals.TotalDownloadBytes);
            Assert.Equal(totals.LanUploadBytes, dashboard.Totals.LanUploadBytes);
            var apps = await db.Network.QueryAppSummariesAsync(start, start.AddHours(1), scope, direction, 100);
            Assert.Equal(apps.Select(x => x.AppKey), dashboard.Apps.Select(x => x.AppKey));
        }
        await using var connection = await db.Factory.OpenConnectionAsync();
        foreach (var sql in new[]
        {
            "SELECT * FROM hardware_samples WHERE sample_time >= $from AND sample_time < $to",
            "SELECT * FROM hardware_minute_windows WHERE minute_start >= $from AND minute_start < $to ORDER BY minute_start",
            "SELECT * FROM network_usage_agg WHERE bucket_start_time >= $from AND bucket_start_time < $to",
            "SELECT * FROM network_usage_rollup_12h_windows WHERE window_start_time >= $from AND window_start_time < $to ORDER BY window_start_time"
        })
        {
            await using var query = connection.CreateCommand();
            query.CommandText = "EXPLAIN QUERY PLAN " + sql;
            query.Parameters.AddWithValue("$from", start.ToString("O"));
            query.Parameters.AddWithValue("$to", start.AddHours(1).ToString("O"));
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Contains("SEARCH", reader.GetString(3), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task RawAndRollupRemainEquivalentAndLateBucketsUpdateMarker()
    {
        await using var db = await TestDatabase.CreateAsync();
        var anchor = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await db.Network.SaveAsync([Bucket(anchor.AddHours(-1), "old", 1), Bucket(anchor.AddHours(1), "one", 100), Bucket(anchor.AddHours(11), "two", 200)]);
        var before = await db.Network.QueryTotalsAsync(anchor, anchor.AddHours(12));
        Assert.True(await db.Network.RollupCompletedWindowsAsync(1) > 0);
        var after = await db.Network.QueryTotalsAsync(anchor, anchor.AddHours(12));
        Assert.Equal(before.TotalUploadBytes, after.TotalUploadBytes);
        await db.Network.SaveAsync([Bucket(anchor.AddHours(4), "one", 7)]);
        var late = await db.Network.QueryTotalsAsync(anchor, anchor.AddHours(12));
        Assert.Equal(before.TotalUploadBytes + 7, late.TotalUploadBytes);
    }

    [Fact]
    public async Task RollupSerializesSparseWindowSelectionWithLateSave()
    {
        await using var db = await TestDatabase.CreateAsync();
        var anchor = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var later = anchor.AddHours(24);
        await db.Network.SaveAsync([Bucket(later.AddHours(1), "app", 100)]);
        await using (var setup = await db.Factory.OpenConnectionAsync())
        {
            await using var cursor = setup.CreateCommand();
            cursor.CommandText = "INSERT INTO storage_progress(name, value) VALUES ('network_rollup_cursor', $start);";
            cursor.Parameters.AddWithValue("$start", anchor.ToString("O"));
            await cursor.ExecuteNonQueryAsync();
        }

        using var selected = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var gateTimedOut = 0;
        var tracedFactory = new TracedFactory(db.Factory.DatabasePath, sql =>
        {
            if (!sql.Contains("SELECT bucket_start_time FROM network_usage_agg", StringComparison.Ordinal) ||
                !sql.Contains("WHERE bucket_start_time >=", StringComparison.Ordinal)) return;
            selected.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15))) Interlocked.Exchange(ref gateTimedOut, 1);
        });
        var rollupRepository = new NetworkTrafficRepository(tracedFactory, new TestRegistry(), NullLogger<NetworkTrafficRepository>.Instance);
        var rollup = Task.Run(() => rollupRepository.RollupCompletedWindowsAsync(1));
        Task? late = null;
        try
        {
            Assert.True(selected.Wait(TimeSpan.FromSeconds(15)), "Rollup did not reach indexed next-window selection.");
            // Probe SQLite directly: SqliteCommand retries BUSY even with busy_timeout=0,
            // which would wait past the gate instead of observing the writer reservation.
            await using (var probe = db.Factory.CreateConnection())
            {
                await probe.OpenAsync();
                Assert.Equal(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_busy_timeout(probe.Handle, 0));
                var result = SQLitePCL.raw.sqlite3_exec(probe.Handle, "BEGIN IMMEDIATE;");
                if (result == SQLitePCL.raw.SQLITE_OK)
                    SQLitePCL.raw.sqlite3_exec(probe.Handle, "ROLLBACK;");
                Assert.Equal(SQLitePCL.raw.SQLITE_BUSY, result);
            }
            late = Task.Run(() => db.Network.SaveAsync([Bucket(anchor.AddHours(1), "app", 7)]));
            release.Set();
            Assert.Equal(1, await rollup.WaitAsync(TimeSpan.FromSeconds(15)));
            await late.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, Volatile.Read(ref gateTimedOut));
        }
        finally
        {
            release.Set();
            await rollup.WaitAsync(TimeSpan.FromSeconds(15));
            if (late is not null) await late.WaitAsync(TimeSpan.FromSeconds(15));
        }

        await using (var verify = await db.Factory.OpenConnectionAsync())
        {
            await using var progress = verify.CreateCommand();
            progress.CommandText = "SELECT value FROM storage_progress WHERE name = 'network_rollup_cursor';";
            Assert.Equal(anchor.ToString("O"), await progress.ExecuteScalarAsync());
        }
        Assert.Equal(1, await db.Network.RollupCompletedWindowsAsync(1));
        await using (var verify = await db.Factory.OpenConnectionAsync())
        {
            await using var marker = verify.CreateCommand();
            marker.CommandText = "SELECT source_bucket_count FROM network_usage_rollup_12h_windows WHERE window_start_time = $start;";
            marker.Parameters.AddWithValue("$start", anchor.ToString("O"));
            Assert.Equal(1L, (long)(await marker.ExecuteScalarAsync())!);
        }
        var total = await db.Network.QueryTotalsAsync(anchor, anchor.AddHours(12));
        Assert.Equal(7, total.TotalUploadBytes);
        await db.Network.SaveAsync([Bucket(anchor.AddHours(2), "app", 3)]);
        var incremented = await db.Network.QueryTotalsAsync(anchor, anchor.AddHours(12));
        Assert.Equal(10, incremented.TotalUploadBytes);
    }

    [Fact]
    public async Task LateUnmarkedFirstWindowIsDiscoveredWithoutRebuildingCompletedWindows()
    {
        await using var db = await TestDatabase.CreateAsync();
        var anchor = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await db.Network.SaveAsync([Bucket(anchor.AddHours(1), "a", 1), Bucket(anchor.AddHours(13), "a", 2)]);
        await db.Network.RollupCompletedWindowsAsync(1);
        await db.Network.SaveAsync([Bucket(anchor.AddHours(2), "a", 3)]);
        await db.Network.RollupCompletedWindowsAsync(1);
        await using var connection = await db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM network_usage_rollup_12h_windows WHERE window_start_time = $start;";
        command.Parameters.AddWithValue("$start", anchor.ToString("O"));
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        var total = await db.Network.QueryTotalsAsync(anchor, anchor.AddHours(12));
        Assert.Equal(4, total.TotalUploadBytes);
    }

    [Fact]
    public async Task RetentionInvalidatesCutWindowAndRollupCannotRecreateIt()
    {
        await using var db = await TestDatabase.CreateAsync();
        db.Settings.Current.HistoryRetentionDays = 1;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        var window = new DateTimeOffset(cutoff.UtcTicks / TimeSpan.FromHours(12).Ticks * TimeSpan.FromHours(12).Ticks, TimeSpan.Zero);
        var last = window.AddHours(12).AddTicks(-1);
        var retainedTime = cutoff.AddMinutes(5) < last ? cutoff.AddMinutes(5) : last;
        var expiredTime = cutoff.AddMinutes(-5) > window ? cutoff.AddMinutes(-5) : window;
        await db.Network.SaveAsync([Bucket(window.AddHours(-1), "old", 1), Bucket(expiredTime, "a", 100), Bucket(retainedTime, "a", 200)]);
        await db.Network.RollupCompletedWindowsAsync(3);
        await using (var connection = await db.Factory.OpenConnectionAsync())
        {
            await using var marker = connection.CreateCommand();
            marker.CommandText = "SELECT COUNT(*) FROM network_usage_rollup_12h_windows WHERE window_start_time = $start;";
            marker.Parameters.AddWithValue("$start", window.ToString("O"));
            Assert.Equal(1L, (long)(await marker.ExecuteScalarAsync())!);
        }
        await db.Retention.CleanupAsync();
        var sum = await db.Network.QueryTotalsAsync(window, window.AddHours(12));
        Assert.Equal(200, sum.TotalUploadBytes);
        await db.Network.RollupCompletedWindowsAsync(5);
        var after = await db.Network.QueryTotalsAsync(window, window.AddHours(12));
        Assert.Equal(200, after.TotalUploadBytes);
    }

    [Fact]
    public async Task RetentionPreservesOnlyAvailableLegacyRollupAtCut()
    {
        await using var db = await TestDatabase.CreateAsync();
        db.Settings.Current.HistoryRetentionDays = 1;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        var window = new DateTimeOffset(cutoff.UtcTicks / TimeSpan.FromHours(12).Ticks * TimeSpan.FromHours(12).Ticks, TimeSpan.Zero);
        await db.Network.SaveAsync([Bucket(window.AddHours(-1), "old", 1), Bucket(window.AddHours(1), "app", 123)]);
        await db.Network.RollupCompletedWindowsAsync(3);
        await using (var connection = await db.Factory.OpenConnectionAsync())
        {
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM network_usage_agg WHERE bucket_start_time >= $start AND bucket_start_time < $end;";
            delete.Parameters.AddWithValue("$start", window.ToString("O"));
            delete.Parameters.AddWithValue("$end", window.AddHours(12).ToString("O"));
            await delete.ExecuteNonQueryAsync();
        }
        await db.Retention.CleanupAsync();
        var total = await db.Network.QueryTotalsAsync(window, window.AddHours(12));
        Assert.Equal(123, total.TotalUploadBytes);
    }

    [Fact]
    public async Task RetentionInterruptionDoesNotLeaveIncompleteMarkedRollup()
    {
        await using var db = await TestDatabase.CreateAsync();
        db.Settings.Current.HistoryRetentionDays = 1;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        var window = new DateTimeOffset(cutoff.UtcTicks / TimeSpan.FromHours(12).Ticks * TimeSpan.FromHours(12).Ticks, TimeSpan.Zero);
        var expired = window.AddTicks(1);
        var retained = window.AddHours(12).AddTicks(-1);
        await db.Network.SaveAsync(Enumerable.Range(0, 6000).Select(_ => Bucket(expired, "app", 1))
            .Prepend(Bucket(window.AddHours(-1), "old", 1)).Append(Bucket(retained, "app", 42)).ToArray());
        await db.Network.RollupCompletedWindowsAsync(3);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Retention.CleanupAsync(cancellation.Token));
        await using (var connection = await db.Factory.OpenConnectionAsync())
        {
            await using var verify = connection.CreateCommand();
            verify.CommandText = """
                SELECT COUNT(*) FROM network_usage_rollup_12h_windows w
                WHERE w.window_start_time = $start
                  AND w.source_bucket_count != (SELECT COUNT(*) FROM network_usage_agg WHERE bucket_start_time >= $start AND bucket_start_time < $end);
                """;
            verify.Parameters.AddWithValue("$start", window.ToString("O"));
            verify.Parameters.AddWithValue("$end", window.AddHours(12).ToString("O"));
            Assert.Equal(0L, (long)(await verify.ExecuteScalarAsync())!);
        }
        await db.Retention.CleanupAsync();
        await db.Network.RollupCompletedWindowsAsync(10);
        var total = await db.Network.QueryTotalsAsync(window, window.AddHours(12));
        Assert.Equal(42, total.TotalUploadBytes);
    }

    [Fact]
    public async Task ConnectionsHavePragmasAndOldUtcDatabaseInitializationIsIdempotent()
    {
        await using var db = await TestDatabase.CreateAsync();
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await db.Hardware.SaveAsync(new HardwareSnapshot { SampleTime = start });
        await using (var oldDatabase = await db.Factory.OpenConnectionAsync())
        {
            await using var removeNewTables = oldDatabase.CreateCommand();
            removeNewTables.CommandText = "DROP TABLE hardware_minute_candidates; DROP TABLE hardware_minute_windows; DROP TABLE storage_progress;";
            await removeNewTables.ExecuteNonQueryAsync();
        }
        await db.Initializer.InitializeAsync();
        await db.Initializer.InitializeAsync();
        Assert.Single(await db.Hardware.QueryRangeAsync(start, start.AddSeconds(1)));
        for (var i = 0; i < 2; i++)
        {
            await using var connection = await db.Factory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous;";
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = "PRAGMA foreign_keys;";
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = "PRAGMA busy_timeout;";
            Assert.Equal(2000L, (long)(await command.ExecuteScalarAsync())!);
        }
    }

    private static TrafficBucket Bucket(DateTimeOffset time, string app, long bytes) => new()
    {
        BucketStartTime = time, BucketGranularitySeconds = 10, AppKey = app,
        Direction = TrafficDirection.Outbound, ScopeType = AddressScopeType.Wan, Bytes = bytes, Packets = 1
    };

    private sealed class TestDatabase : IAsyncDisposable
    {
        public required IDbConnectionFactory Factory { get; init; }
        public required DatabaseInitializer Initializer { get; init; }
        public required HardwareRepository Hardware { get; init; }
        public required NetworkTrafficRepository Network { get; init; }
        public required RetentionService Retention { get; init; }
        public required TestSettings Settings { get; init; }
        public static async Task<TestDatabase> CreateAsync()
        {
            var factory = new TestFactory(Path.Combine(Path.GetTempPath(), $"monitor-test-{Guid.NewGuid():N}.sqlite"));
            var settings = new TestSettings();
            var initializer = new DatabaseInitializer(factory, new TestOptions(settings.Current), NullLogger<DatabaseInitializer>.Instance);
            await initializer.InitializeAsync();
            return new TestDatabase
            {
                Factory = factory, Initializer = initializer, Settings = settings,
                Hardware = new HardwareRepository(factory, NullLogger<HardwareRepository>.Instance),
                Network = new NetworkTrafficRepository(factory, new TestRegistry(), NullLogger<NetworkTrafficRepository>.Instance),
                Retention = new RetentionService(factory, settings, NullLogger<RetentionService>.Instance)
            };
        }
        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(Factory.DatabasePath + suffix)) File.Delete(Factory.DatabasePath + suffix);
            return ValueTask.CompletedTask;
        }
    }
    private sealed class TestFactory(string path) : IDbConnectionFactory
    {
        public string DatabasePath => path;
        public SqliteConnection CreateConnection() => new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
    }
    private sealed class TracedFactory(string path, Action<string> trace) : IDbConnectionFactory
    {
        public string DatabasePath => path;
        public SqliteConnection CreateConnection() => new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = CreateConnection();
            try
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 2000;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)((_, sql) => trace(sql)), null);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
    }
    private sealed class TestRegistry : IAppRegistry
    {
        public AppRegistryEntry GetOrAdd(int pid) => throw new NotSupportedException();
        public AppRegistryEntry? GetByAppKey(string appKey) => null;
        public IReadOnlyList<AppRegistryEntry> GetAll() => [];
    }
    private sealed class TestSettings : IMonitorSettingsProvider
    {
        public MonitorSettings Current { get; } = new();
        public MonitorSettings Update(AppSettingsDto settings) => throw new NotSupportedException();
        public IDisposable RegisterChangeCallback(Action<MonitorSettings> callback) => throw new NotSupportedException();
    }
    private sealed class TestOptions(MonitorSettings settings) : IOptionsMonitor<MonitorSettings>
    {
        public MonitorSettings CurrentValue => settings;
        public MonitorSettings Get(string? name) => settings;
        public IDisposable? OnChange(Action<MonitorSettings, string?> listener) => null;
    }
}
