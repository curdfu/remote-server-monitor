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

        var sampleTimeParameter = command.Parameters.Add("$sampleTime", SqliteType.Text);
        var cpuUsageParameter = command.Parameters.Add("$cpuUsagePercent", SqliteType.Real);
        var cpuTemperatureParameter = command.Parameters.Add("$cpuTemperatureC", SqliteType.Real);
        var cpuFrequencyParameter = command.Parameters.Add("$cpuFrequencyMhz", SqliteType.Real);
        var memoryTotalParameter = command.Parameters.Add("$memoryTotalMb", SqliteType.Real);
        var memoryUsedParameter = command.Parameters.Add("$memoryUsedMb", SqliteType.Real);
        var memoryUsageParameter = command.Parameters.Add("$memoryUsagePercent", SqliteType.Real);
        var diskTemperatureParameter = command.Parameters.Add("$diskTemperatureC", SqliteType.Real);
        var uptimeParameter = command.Parameters.Add("$uptimeSeconds", SqliteType.Integer);

        foreach (var snapshot in snapshots)
        {
            sampleTimeParameter.Value = snapshot.SampleTime.ToString("O");
            cpuUsageParameter.Value = ToDbValue(snapshot.Cpu.UsagePercent);
            cpuTemperatureParameter.Value = ToDbValue(snapshot.Cpu.TemperatureC);
            cpuFrequencyParameter.Value = ToDbValue(snapshot.Cpu.FrequencyMhz);
            memoryTotalParameter.Value = ToDbValue(snapshot.Memory.TotalMb);
            memoryUsedParameter.Value = ToDbValue(snapshot.Memory.UsedMb);
            memoryUsageParameter.Value = ToDbValue(snapshot.Memory.UsagePercent);
            diskTemperatureParameter.Value = ToDbValue(snapshot.Disk.TemperatureC);
            uptimeParameter.Value = snapshot.System.UptimeSeconds;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogDebug("Inserted {Count} hardware samples into SQLite.", snapshots.Count);
    }

    public async Task<IReadOnlyList<HardwareSnapshot>> QueryRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int? maxPoints = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = dbConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        if (maxPoints is > 0)
        {
            command.CommandText = """
                                  WITH filtered AS (
                                      SELECT sample_time,
                                             cpu_usage_percent,
                                             cpu_temperature_c,
                                             cpu_frequency_mhz,
                                             memory_total_mb,
                                             memory_used_mb,
                                             memory_usage_percent,
                                             disk_temperature_c,
                                             uptime_seconds,
                                             ROW_NUMBER() OVER (ORDER BY sample_time ASC) AS row_num,
                                             COUNT(*) OVER () AS total_count
                                      FROM hardware_samples
                                      WHERE sample_time >= $from
                                        AND sample_time < $to
                                  )
                                  SELECT sample_time,
                                         cpu_usage_percent,
                                         cpu_temperature_c,
                                         cpu_frequency_mhz,
                                         memory_total_mb,
                                         memory_used_mb,
                                         memory_usage_percent,
                                         disk_temperature_c,
                                         uptime_seconds
                                  FROM filtered
                                  WHERE total_count <= $maxPoints
                                     OR ((row_num - 1) % CASE
                                             WHEN total_count <= $maxPoints THEN 1
                                             ELSE ((total_count + $maxPoints - 1) / $maxPoints)
                                         END) = 0
                                  ORDER BY sample_time ASC;
                                  """;
            command.Parameters.AddWithValue("$maxPoints", maxPoints.Value);
        }
        else
        {
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
        }

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
