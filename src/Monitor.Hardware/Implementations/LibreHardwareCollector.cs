using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Monitor.Hardware.Abstractions;
using Monitor.Hardware.Models;

namespace Monitor.Hardware.Implementations;

public sealed class LibreHardwareCollector : IHardwareCollector, IDisposable
{
    private readonly ILogger<LibreHardwareCollector> _logger;
    private readonly Computer _computer;
    private readonly object _syncRoot = new();
    private bool _isOpen;
    private bool _disposed;

    public LibreHardwareCollector(ILogger<LibreHardwareCollector> logger)
    {
        _logger = logger;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true
        };

        TryOpenComputer();
    }

    public Task<HardwareSnapshot> GetCurrentSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            ThrowIfDisposed();

            if (!_isOpen)
            {
                TryOpenComputer();
            }

            RefreshHardwareTree();

            var cpuHardware = _computer.Hardware.FirstOrDefault(hardware => hardware.HardwareType == HardwareType.Cpu);
            var totalMemoryHardware = _computer.Hardware.FirstOrDefault(hardware =>
                hardware.HardwareType == HardwareType.Memory &&
                hardware.Name.Equals("Total Memory", StringComparison.OrdinalIgnoreCase));

            var cpuSensors = EnumerateSensors(hardware => hardware.HardwareType == HardwareType.Cpu).ToArray();
            var memorySensors = EnumerateSensors(hardware => hardware.HardwareType == HardwareType.Memory).ToArray();
            var storageSensors = EnumerateSensors(hardware => hardware.HardwareType == HardwareType.Storage).ToArray();

            var cpuUsage = ReadSensorValue(
                cpuSensors,
                sensor => sensor.SensorType == SensorType.Load &&
                          sensor.Name.Contains("CPU Total", StringComparison.OrdinalIgnoreCase));

            var (cpuTemperature, cpuTemperatureSource) = ReadFirstSensor(
                cpuSensors,
                sensor => sensor.SensorType == SensorType.Temperature &&
                          sensor.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase),
                sensor => sensor.Name);

            if (!cpuTemperature.HasValue)
            {
                (cpuTemperature, cpuTemperatureSource) = ReadFirstSensor(
                    cpuSensors,
                    sensor => sensor.SensorType == SensorType.Temperature &&
                              sensor.Name.Equals("Core Max", StringComparison.OrdinalIgnoreCase),
                    sensor => sensor.Name);
            }

            var (cpuFrequencyMhz, cpuFrequencySource) = ReadCpuClock(cpuSensors);
            var (cpuPowerWatts, cpuPowerSource) = ReadCpuPower(cpuSensors);

            var (memoryUsed, _) = ReadFirstSensor(
                memorySensors,
                sensor => sensor.SensorType == SensorType.Data &&
                          sensor.Name.Equals("Memory Used", StringComparison.OrdinalIgnoreCase) &&
                          sensor.Hardware.Name.Equals("Total Memory", StringComparison.OrdinalIgnoreCase),
                sensor => sensor.Name);

            var (memoryAvailable, _) = ReadFirstSensor(
                memorySensors,
                sensor => sensor.SensorType == SensorType.Data &&
                          sensor.Name.Equals("Memory Available", StringComparison.OrdinalIgnoreCase) &&
                          sensor.Hardware.Name.Equals("Total Memory", StringComparison.OrdinalIgnoreCase),
                sensor => sensor.Name);

            var memoryUsagePercent = ReadSensorValue(
                memorySensors,
                sensor => sensor.SensorType == SensorType.Load &&
                          sensor.Name.Equals("Memory", StringComparison.OrdinalIgnoreCase) &&
                          sensor.Hardware.Name.Equals("Total Memory", StringComparison.OrdinalIgnoreCase));

            var diskTemperatureCandidates = storageSensors
                .Where(sensor => sensor.SensorType == SensorType.Temperature)
                .Select(sensor => new
                {
                    Value = Normalize(sensor.Value),
                    Source = $"{sensor.Hardware.Name}/{sensor.Name}"
                })
                .Where(item => item.Value.HasValue)
                .ToArray();

            var diskDrives = storageSensors
                .Where(sensor => sensor.SensorType == SensorType.Temperature)
                .GroupBy(sensor => sensor.Hardware.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var candidates = group
                        .Select(sensor => new
                        {
                            Value = Normalize(sensor.Value),
                            Source = $"{sensor.Hardware.Name}/{sensor.Name}"
                        })
                        .Where(item => item.Value.HasValue)
                        .ToArray();

                    var driveTemperature = candidates
                        .Select(item => item.Value!.Value)
                        .DefaultIfEmpty()
                        .Max() is var maxTemperature && maxTemperature > 0
                            ? maxTemperature
                            : (double?)null;

                    return new DiskDriveMetrics
                    {
                        Name = group.Key,
                        TemperatureC = driveTemperature,
                        TemperatureSource = driveTemperature.HasValue
                            ? candidates.FirstOrDefault(item => item.Value == driveTemperature)?.Source
                            : null
                    };
                })
                .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var diskTemperature = diskTemperatureCandidates
                .Select(item => item.Value!.Value)
                .DefaultIfEmpty()
                .Max() is var maxTemperature && maxTemperature > 0
                    ? maxTemperature
                    : (double?)null;

            var diskTemperatureSource = diskTemperature.HasValue
                ? diskTemperatureCandidates.FirstOrDefault(item => item.Value == diskTemperature)?.Source
                : null;

            var uptimeSeconds = Environment.TickCount64 / 1000;
            var sampleTime = DateTimeOffset.UtcNow;

            var snapshot = new HardwareSnapshot
            {
                CollectorName = "LibreHardwareMonitor",
                SampleTime = sampleTime,
                IsPartial = !cpuUsage.HasValue ||
                            !cpuTemperature.HasValue ||
                            !cpuFrequencyMhz.HasValue ||
                            !memoryUsed.HasValue ||
                            !memoryAvailable.HasValue ||
                            !memoryUsagePercent.HasValue ||
                            !diskTemperature.HasValue,
                Cpu = new CpuMetrics
                {
                    Name = cpuHardware?.Name,
                    UsagePercent = cpuUsage,
                    TemperatureC = cpuTemperature,
                    TemperatureSource = cpuTemperatureSource,
                    FrequencyMhz = cpuFrequencyMhz,
                    FrequencySource = cpuFrequencySource,
                    PowerWatts = cpuPowerWatts,
                    PowerSource = cpuPowerSource
                },
                Memory = new MemoryMetrics
                {
                    Name = totalMemoryHardware?.Name,
                    TotalMb = memoryUsed.HasValue && memoryAvailable.HasValue
                        ? memoryUsed.Value + memoryAvailable.Value
                        : null,
                    UsedMb = memoryUsed,
                    AvailableMb = memoryAvailable,
                    UsagePercent = memoryUsagePercent
                },
                Disk = new DiskMetrics
                {
                    MonitoredDiskCount = diskDrives.Length,
                    TemperatureC = diskTemperature,
                    TemperatureSource = diskTemperatureSource,
                    Drives = diskDrives
                },
                System = new SystemMetrics
                {
                    BootTime = sampleTime.AddSeconds(-uptimeSeconds),
                    UptimeSeconds = uptimeSeconds
                }
            };

            return Task.FromResult(snapshot);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (_isOpen)
                {
                    _computer.Close();
                    _isOpen = false;
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to close LibreHardwareMonitor computer cleanly.");
            }
            finally
            {
                _disposed = true;
            }
        }
    }

    private void TryOpenComputer()
    {
        try
        {
            _computer.Open();
            _isOpen = true;
            _logger.LogInformation("LibreHardwareMonitor initialized with CPU/Memory/Storage sensors enabled.");
        }
        catch (Exception exception)
        {
            _isOpen = false;
            _logger.LogError(exception, "Failed to initialize LibreHardwareMonitor.");
        }
    }

    private void RefreshHardwareTree()
    {
        if (!_isOpen)
        {
            return;
        }

        foreach (var hardware in _computer.Hardware)
        {
            UpdateHardwareRecursive(hardware);
        }
    }

    private static void UpdateHardwareRecursive(IHardware hardware)
    {
        hardware.Update();

        foreach (var subHardware in hardware.SubHardware)
        {
            UpdateHardwareRecursive(subHardware);
        }
    }

    private IEnumerable<ISensor> EnumerateSensors(Func<IHardware, bool> hardwarePredicate)
    {
        foreach (var hardware in _computer.Hardware)
        {
            foreach (var sensor in EnumerateSensorsRecursive(hardware, hardwarePredicate))
            {
                yield return sensor;
            }
        }
    }

    private static IEnumerable<ISensor> EnumerateSensorsRecursive(IHardware hardware, Func<IHardware, bool> hardwarePredicate)
    {
        if (hardwarePredicate(hardware))
        {
            foreach (var sensor in hardware.Sensors)
            {
                yield return sensor;
            }
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            foreach (var sensor in EnumerateSensorsRecursive(subHardware, hardwarePredicate))
            {
                yield return sensor;
            }
        }
    }

    private static double? ReadSensorValue(IEnumerable<ISensor> sensors, Func<ISensor, bool> predicate)
    {
        return sensors
            .Where(predicate)
            .Select(sensor => Normalize(sensor.Value))
            .FirstOrDefault(value => value.HasValue);
    }

    private static (double? Value, string? Source) ReadFirstSensor(
        IEnumerable<ISensor> sensors,
        Func<ISensor, bool> predicate,
        Func<ISensor, string> sourceSelector)
    {
        foreach (var sensor in sensors.Where(predicate))
        {
            var value = Normalize(sensor.Value);
            if (value.HasValue)
            {
                return (value.Value, sourceSelector(sensor));
            }
        }

        return (null, null);
    }

    private static (double? Value, string? Source) ReadCpuClock(IEnumerable<ISensor> cpuSensors)
    {
        var clockSensors = cpuSensors
            .Where(sensor => sensor.SensorType == SensorType.Clock &&
                             !sensor.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase))
            .Select(sensor => new
            {
                sensor.Name,
                Value = Normalize(sensor.Value)
            })
            .Where(item => item.Value.HasValue)
            .ToArray();

        if (clockSensors.Length > 0)
        {
            return (clockSensors.Average(item => item.Value!.Value), "Clock/Average");
        }

        var fallback = cpuSensors
            .Where(sensor => sensor.SensorType == SensorType.Frequency)
            .Select(sensor => new
            {
                sensor.Name,
                Value = Normalize(sensor.Value)
            })
            .FirstOrDefault(item => item.Value.HasValue);

        return fallback is null
            ? (null, null)
            : (fallback.Value!.Value, $"Frequency/{fallback.Name}");
    }

    private static (double? Value, string? Source) ReadCpuPower(IEnumerable<ISensor> cpuSensors)
    {
        var preferredPatterns = new[]
        {
            "CPU Package",
            "Package"
        };

        foreach (var pattern in preferredPatterns)
        {
            var match = ReadFirstSensor(
                cpuSensors,
                sensor => sensor.SensorType == SensorType.Power &&
                          sensor.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase),
                sensor => sensor.Name);

            if (match.Value.HasValue)
            {
                return match;
            }
        }

        return ReadFirstSensor(
            cpuSensors,
            sensor => sensor.SensorType == SensorType.Power,
            sensor => sensor.Name);
    }

    private static double? Normalize(float? value)
    {
        if (!value.HasValue || float.IsNaN(value.Value) || float.IsInfinity(value.Value))
        {
            return null;
        }

        return Math.Round(value.Value, 2, MidpointRounding.AwayFromZero);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
