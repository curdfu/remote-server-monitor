using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Monitor.Hardware.Models;
using Monitor.Storage.Abstractions;

namespace Monitor.Storage.Repositories;

public sealed class HardwareRepository(
    IDbConnectionFactory dbConnectionFactory,
    ILogger<HardwareRepository> logger)
{
    public Task SaveAsync(HardwareSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return SaveBatchAsync([snapshot], cancellationToken);
    }

    public async Task SaveBatchAsync(IReadOnlyCollection<HardwareSnapshot> snapshots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            return;
        }

        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transactionHandle = await connection.BeginTransactionAsync(cancellationToken);
        var transaction = (SqliteTransaction)transactionHandle;

        foreach (var snapshot in snapshots)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                                  INSERT INTO hardware_samples (
                                      sample_time,
                                      cpu_usage_percent,
                                      cpu_temperature_c,
                                      cpu_frequency_mhz,
                                      memory_total_mb,
                                      memory_used_mb,
                                      memory_usage_percent,
                                      disk_temperature_c,
                                      uptime_seconds
                                  )
                                  VALUES (
                                      $sampleTime,
                                      $cpuUsagePercent,
                                      $cpuTemperatureC,
                                      $cpuFrequencyMhz,
                                      $memoryTotalMb,
                                      $memoryUsedMb,
                                      $memoryUsagePercent,
                                      $diskTemperatureC,
                                      $uptimeSeconds
                                  );
                                  """;

            command.Parameters.AddWithValue("$sampleTime", snapshot.SampleTime.ToString("O"));
            command.Parameters.AddWithValue("$cpuUsagePercent", ToDbValue(snapshot.Cpu.UsagePercent));
            command.Parameters.AddWithValue("$cpuTemperatureC", ToDbValue(snapshot.Cpu.TemperatureC));
            command.Parameters.AddWithValue("$cpuFrequencyMhz", ToDbValue(snapshot.Cpu.FrequencyMhz));
            command.Parameters.AddWithValue("$memoryTotalMb", ToDbValue(snapshot.Memory.TotalMb));
            command.Parameters.AddWithValue("$memoryUsedMb", ToDbValue(snapshot.Memory.UsedMb));
            command.Parameters.AddWithValue("$memoryUsagePercent", ToDbValue(snapshot.Memory.UsagePercent));
            command.Parameters.AddWithValue("$diskTemperatureC", ToDbValue(snapshot.Disk.TemperatureC));
            command.Parameters.AddWithValue("$uptimeSeconds", snapshot.System.UptimeSeconds);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogDebug("Inserted {Count} hardware samples into SQLite.", snapshots.Count);
    }

    public async Task<IReadOnlyList<HardwareSnapshot>> QueryRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT sample_time,
                                     cpu_usage_percent,
                                     cpu_temperature_c,
                                     cpu_frequency_mhz,
                                     memory_total_mb,
                                     memory_used_mb,
                                     memory_usage_percent,
                                     disk_temperature_c,
                                     uptime_seconds
                              FROM hardware_samples
                              WHERE sample_time >= $from
                                AND sample_time < $to
                              ORDER BY sample_time ASC;
                              """;

        command.Parameters.AddWithValue("$from", from.ToString("O"));
        command.Parameters.AddWithValue("$to", to.ToString("O"));

        var results = new List<HardwareSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new HardwareSnapshot
            {
                SampleTime = reader.GetDateTimeOffset(0),
                Cpu = new CpuMetrics
                {
                    UsagePercent = ReadNullableDouble(reader, 1),
                    TemperatureC = ReadNullableDouble(reader, 2),
                    FrequencyMhz = ReadNullableDouble(reader, 3)
                },
                Memory = new MemoryMetrics
                {
                    TotalMb = ReadNullableDouble(reader, 4),
                    UsedMb = ReadNullableDouble(reader, 5),
                    UsagePercent = ReadNullableDouble(reader, 6)
                },
                Disk = new DiskMetrics
                {
                    TemperatureC = ReadNullableDouble(reader, 7)
                },
                System = new SystemMetrics
                {
                    UptimeSeconds = reader.GetInt64(8)
                }
            });
        }

        return results;
    }

    private static object ToDbValue(double? value) => value.HasValue ? value.Value : DBNull.Value;

    private static double? ReadNullableDouble(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }
}
