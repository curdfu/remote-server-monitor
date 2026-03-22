using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Monitor.Hardware.Abstractions;
using Monitor.Hardware.Models;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Monitor.Hardware.Implementations;

public sealed class LibreHardwareCollector : IHardwareCollector, IDiskUsageProvider, IDisposable
{
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceProperty = 0;
    private const int PropertyStandardQuery = 0;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorMoreData = 234;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private readonly ILogger<LibreHardwareCollector> _logger;
    private readonly Computer _computer;
    private readonly IReadOnlyList<DiskTopologyEntry> _diskTopologyEntries;
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
        _diskTopologyEntries = OperatingSystem.IsWindows()
            ? LoadDiskTopologyEntries()
            : Array.Empty<DiskTopologyEntry>();

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

                    var topologyEntry = ResolveDiskTopologyEntry(group.Key);

                    _logger.LogDebug(
                        "Disk temperature mapping. HardwareName={HardwareName}, MatchedDiskNumber={DiskNumber}, SizeBytes={SizeBytes}, Temperature={Temperature}, Volumes={Volumes}",
                        group.Key,
                        topologyEntry?.DiskNumber,
                        topologyEntry?.SizeBytes,
                        driveTemperature,
                        topologyEntry is null ? "<none>" : string.Join(", ", topologyEntry.VolumeNames));

                    return new DiskDriveMetrics
                    {
                        Name = group.Key,
                        DiskNumber = topologyEntry?.DiskNumber,
                        SizeBytes = topologyEntry?.SizeBytes,
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

    public IReadOnlyDictionary<uint, long?> GetCurrentUsedBytesByDiskNumber(IEnumerable<uint> diskNumbers)
    {
        ArgumentNullException.ThrowIfNull(diskNumbers);

        if (!OperatingSystem.IsWindows() || _diskTopologyEntries.Count == 0)
        {
            return new Dictionary<uint, long?>();
        }

        var requestedEntries = diskNumbers
            .Distinct()
            .Select(diskNumber => new
            {
                DiskNumber = diskNumber,
                Entry = ResolveDiskTopologyEntry(diskNumber)
            })
            .ToArray();

        var usageByVolume = GetCurrentVolumeUsage(
            requestedEntries
                .SelectMany(item => item.Entry?.VolumeNames ?? Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase));

        var results = new Dictionary<uint, long?>();

        foreach (var item in requestedEntries)
        {
            results[item.DiskNumber] = ResolveDiskUsedBytes(item.Entry, usageByVolume);
        }

        return results;
    }

    public IReadOnlyList<DiskSpaceInfo> GetCurrentDiskSpaces()
    {
        if (!OperatingSystem.IsWindows() || _diskTopologyEntries.Count == 0)
        {
            return Array.Empty<DiskSpaceInfo>();
        }

        var usageByVolume = GetCurrentVolumeUsage(
            _diskTopologyEntries
                .SelectMany(entry => entry.VolumeNames)
                .Distinct(StringComparer.OrdinalIgnoreCase));

        return _diskTopologyEntries
            .GroupBy(entry => entry.DiskNumber)
            .Select(group =>
            {
                var entry = group.First();
                var usedBytes = ResolveDiskUsedBytes(entry, usageByVolume);
                var totalBytes = entry.SizeBytes;
                return new DiskSpaceInfo
                {
                    Name = BuildDiskSpaceDisplayName(entry),
                    DiskNumber = entry.DiskNumber,
                    TotalBytes = totalBytes,
                    UsedBytes = usedBytes,
                    FreeBytes = usedBytes.HasValue
                        ? Math.Max(0, totalBytes - usedBytes.Value)
                        : null
                };
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
        double freqSum = 0;
        int freqCount = 0;
        double? freqTotal = null;
        string? freqTotalName = null;

        double effSum = 0;
        int effCount = 0;

        double clockSum = 0;
        int clockCount = 0;

        double coreClockSum = 0;
        int coreClockCount = 0;

        foreach (var sensor in cpuSensors)
        {
            var value = Normalize(sensor.Value);
            if (!value.HasValue)
                continue;

            var name = sensor.Name;

            // ---------- Frequency ----------
            if (sensor.SensorType == SensorType.Frequency)
            {
                // 优先 Total / CPU
                if (freqTotal == null &&
                    (name.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("CPU", StringComparison.OrdinalIgnoreCase)))
                {
                    freqTotal = value.Value;
                    freqTotalName = name;
                }

                freqSum += value.Value;
                freqCount++;
                continue;
            }

            // ---------- Clock ----------
            if (sensor.SensorType == SensorType.Clock)
            {
                // Effective Clock
                if (name.Contains("Effective", StringComparison.OrdinalIgnoreCase))
                {
                    effSum += value.Value;
                    effCount++;
                    continue;
                }

                // 排除 Bus
                if (name.Contains("Bus", StringComparison.OrdinalIgnoreCase))
                    continue;

                clockSum += value.Value;
                clockCount++;

                // Core 优先
                if (name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                {
                    coreClockSum += value.Value;
                    coreClockCount++;
                }
            }
        }

        // ---------- 1. Frequency ----------
        if (freqCount > 0)
        {
            if (freqTotal.HasValue)
            {
                return (freqTotal.Value, $"Frequency/{freqTotalName}");
            }

            return (freqSum / freqCount, "Frequency/Average");
        }

        // ---------- 2. Effective Clock ----------
        if (effCount > 0)
        {
            return (effSum / effCount, "EffectiveClock/Average");
        }

        // ---------- 3. Clock ----------
        if (clockCount > 0)
        {
            if (coreClockCount > 0)
            {
                return (coreClockSum / coreClockCount, "Clock/CoreAverage");
            }

            return (clockSum / clockCount, "Clock/Average");
        }

        return (null, null);
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

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<DiskTopologyEntry> LoadDiskTopologyEntries()
    {
        try
        {
            var volumeNamesByDiskNumber = new Dictionary<uint, HashSet<string>>();
            foreach (var volumeName in EnumerateVolumeNames())
            {
                foreach (var diskNumber in GetDiskNumbersForVolume(volumeName))
                {
                    if (!volumeNamesByDiskNumber.TryGetValue(diskNumber, out var volumeNames))
                    {
                        volumeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        volumeNamesByDiskNumber[diskNumber] = volumeNames;
                    }

                    volumeNames.Add(volumeName);
                }
            }

            var entries = new List<DiskTopologyEntry>();
            foreach (var pair in volumeNamesByDiskNumber.OrderBy(item => item.Key))
            {
                var diskNumber = pair.Key;
                var volumeNames = pair.Value.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
                var sizeBytes = GetTotalVolumeBytes(volumeNames);
                if (!sizeBytes.HasValue || sizeBytes.Value <= 0)
                {
                    continue;
                }

                AddDiskTopologyEntry(entries, $"PHYSICALDRIVE{diskNumber}", diskNumber, sizeBytes.Value, volumeNames);
                AddDiskTopologyEntry(entries, $"DISK{diskNumber}", diskNumber, sizeBytes.Value, volumeNames);
            }

            foreach (var topologyEntry in entries
                         .GroupBy(entry => entry.DiskNumber)
                         .Select(group => group.First())
                         .OrderBy(entry => entry.DiskNumber))
            {
                _logger.LogDebug(
                    "Disk topology cached. DiskNumber={DiskNumber}, SizeBytes={SizeBytes}, Volumes={Volumes}",
                    topologyEntry.DiskNumber,
                    topologyEntry.SizeBytes,
                    string.Join(", ", topologyEntry.VolumeNames));
            }

            _logger.LogInformation(
                "Initialized disk topology cache. PhysicalDisks: {PhysicalDiskCount}, NameCandidates: {NameCandidateCount}",
                volumeNamesByDiskNumber.Count,
                entries.Count);
            return entries;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to initialize physical disk topology cache. Disk size and usage columns may be unavailable.");
            return Array.Empty<DiskTopologyEntry>();
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> EnumerateVolumeNames()
    {
        const int bufferLength = 1024;
        var volumeNames = new List<string>();
        var volumeNameBuffer = new StringBuilder(bufferLength);

        var findHandle = FindFirstVolume(volumeNameBuffer, (uint)volumeNameBuffer.Capacity);
        if (findHandle == InvalidHandleValue)
        {
            return Array.Empty<string>();
        }

        try
        {
            volumeNames.Add(volumeNameBuffer.ToString());

            while (true)
            {
                volumeNameBuffer.Clear();
                volumeNameBuffer.EnsureCapacity(bufferLength);
                if (!FindNextVolume(findHandle, volumeNameBuffer, (uint)volumeNameBuffer.Capacity))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreFiles)
                    {
                        break;
                    }

                    break;
                }

                volumeNames.Add(volumeNameBuffer.ToString());
            }
        }
        finally
        {
            FindVolumeClose(findHandle);
        }

        return volumeNames;
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<uint> GetDiskNumbersForVolume(string volumeName)
    {
        if (string.IsNullOrWhiteSpace(volumeName))
        {
            return Array.Empty<uint>();
        }

        var handle = OpenDeviceHandle(volumeName.TrimEnd('\\'));
        if (handle == InvalidHandleValue)
        {
            return Array.Empty<uint>();
        }

        try
        {
            var buffer = new byte[Marshal.SizeOf<VOLUME_DISK_EXTENTS_WITH_FIRST>() + Marshal.SizeOf<DISK_EXTENT>() * 32];

            while (true)
            {
                if (DeviceIoControl(handle, IoctlVolumeGetVolumeDiskExtents, IntPtr.Zero, 0, buffer, buffer.Length, out _, IntPtr.Zero))
                {
                    return ParseDiskNumbers(buffer);
                }

                var error = Marshal.GetLastWin32Error();
                if (error != ErrorMoreData)
                {
                    return Array.Empty<uint>();
                }

                buffer = new byte[buffer.Length * 2];
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<uint> ParseDiskNumbers(byte[] buffer)
    {
        var diskNumbers = new HashSet<uint>();
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        try
        {
            var basePointer = handle.AddrOfPinnedObject();
            var extentsHeader = Marshal.PtrToStructure<VOLUME_DISK_EXTENTS_WITH_FIRST>(basePointer);
            var extentCount = (int)extentsHeader.NumberOfDiskExtents;
            var firstExtentOffset = Marshal.OffsetOf<VOLUME_DISK_EXTENTS_WITH_FIRST>(nameof(VOLUME_DISK_EXTENTS_WITH_FIRST.Extents)).ToInt32();
            var extentSize = Marshal.SizeOf<DISK_EXTENT>();

            for (var index = 0; index < extentCount; index++)
            {
                var extentPointer = IntPtr.Add(basePointer, firstExtentOffset + index * extentSize);
                var extent = Marshal.PtrToStructure<DISK_EXTENT>(extentPointer);
                diskNumbers.Add(extent.DiskNumber);
            }
        }
        finally
        {
            handle.Free();
        }

        return diskNumbers.ToArray();
    }

    private static long? ResolveDiskUsedBytes(DiskTopologyEntry? entry, IReadOnlyDictionary<string, long> usageByVolume)
    {
        if (entry is null || entry.VolumeNames.Count == 0)
        {
            return null;
        }

        long totalUsedBytes = 0;
        var hasAnyUsage = false;

        foreach (var volumeName in entry.VolumeNames)
        {
            if (!usageByVolume.TryGetValue(volumeName, out var usedBytes))
            {
                continue;
            }

            totalUsedBytes += usedBytes;
            hasAnyUsage = true;
        }

        return hasAnyUsage ? totalUsedBytes : null;
    }

    [SupportedOSPlatform("windows")]
    private static string BuildDiskSpaceDisplayName(DiskTopologyEntry entry)
    {
        var pathNames = entry.VolumeNames
            .SelectMany(GetVolumePathNames)
            .Select(path => path.Trim().TrimEnd('\\'))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return pathNames.Length > 0
            ? string.Join(" + ", pathNames)
            : $"磁盘 {entry.DiskNumber}";
    }

    private DiskTopologyEntry? ResolveDiskTopologyEntry(string hardwareName)
    {
        var normalizedHardwareName = NormalizeDiskName(hardwareName);
        if (string.IsNullOrWhiteSpace(normalizedHardwareName) || _diskTopologyEntries.Count == 0)
        {
            return null;
        }

        var exactMatch = _diskTopologyEntries.FirstOrDefault(entry => entry.NormalizedName == normalizedHardwareName);
        if (exactMatch is not null)
        {
            return exactMatch;
        }

        return _diskTopologyEntries.FirstOrDefault(entry =>
            normalizedHardwareName.Contains(entry.NormalizedName, StringComparison.OrdinalIgnoreCase) ||
            entry.NormalizedName.Contains(normalizedHardwareName, StringComparison.OrdinalIgnoreCase));
    }

    private DiskTopologyEntry? ResolveDiskTopologyEntry(uint diskNumber)
    {
        return _diskTopologyEntries.FirstOrDefault(entry => entry.DiskNumber == diskNumber);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyDictionary<string, long> GetCurrentVolumeUsage(IEnumerable<string> volumeNames)
    {
        var usages = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var volumeName in volumeNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!GetDiskFreeSpaceEx(volumeName, out _, out var totalBytes, out var totalFreeBytes) || totalBytes == 0)
            {
                continue;
            }

            usages[volumeName] = checked((long)(totalBytes - totalFreeBytes));
        }

        return usages;
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> GetVolumePathNames(string volumeName)
    {
        if (string.IsNullOrWhiteSpace(volumeName))
        {
            return Array.Empty<string>();
        }

        var capacity = 256u;

        while (true)
        {
            var buffer = new char[capacity];
            if (GetVolumePathNamesForVolumeName(volumeName, buffer, (uint)buffer.Length, out var returnLength))
            {
                return buffer
                    .AsSpan()
                    .ToString()
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToArray();
            }

            var error = Marshal.GetLastWin32Error();
            if (error != ErrorMoreData || returnLength <= capacity)
            {
                return Array.Empty<string>();
            }

            capacity = returnLength;
        }
    }

    [SupportedOSPlatform("windows")]
    private static long? GetTotalVolumeBytes(IEnumerable<string> volumeNames)
    {
        long totalBytes = 0;
        var hasAnyVolume = false;

        foreach (var volumeName in volumeNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!GetDiskFreeSpaceEx(volumeName, out _, out var currentVolumeBytes, out _) || currentVolumeBytes == 0)
            {
                continue;
            }

            totalBytes += checked((long)currentVolumeBytes);
            hasAnyVolume = true;
        }

        return hasAnyVolume ? totalBytes : null;
    }

    private static void AddDiskTopologyEntry(
        ICollection<DiskTopologyEntry> entries,
        string? rawName,
        uint diskNumber,
        long sizeBytes,
        IReadOnlyList<string> volumeNames)
    {
        var normalizedName = NormalizeDiskName(rawName);
        if (string.IsNullOrWhiteSpace(normalizedName) || sizeBytes <= 0)
        {
            return;
        }

        if (entries.Any(entry => entry.NormalizedName == normalizedName && entry.DiskNumber == diskNumber))
        {
            return;
        }

        entries.Add(new DiskTopologyEntry(normalizedName, diskNumber, sizeBytes, volumeNames));
    }

    private static string NormalizeDiskName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static string? ReadAnsiString(byte[] buffer, uint offset)
    {
        if (offset == 0 || offset >= buffer.Length)
        {
            return null;
        }

        var start = (int)offset;
        var end = start;
        while (end < buffer.Length && buffer[end] != 0)
        {
            end++;
        }

        return end <= start
            ? null
            : Encoding.ASCII.GetString(buffer, start, end - start).Trim();
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr OpenDeviceHandle(string path)
    {
        return CreateFile(
            path,
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            0,
            IntPtr.Zero);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstVolume(StringBuilder lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextVolume(IntPtr hFindVolume, StringBuilder lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindVolumeClose(IntPtr hFindVolume);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        out GET_LENGTH_INFORMATION lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNamesForVolumeName(
        string lpszVolumeName,
        char[] lpszVolumePathNames,
        uint cchBufferLength,
        out uint lpcchReturnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct DISK_EXTENT
    {
        public uint DiskNumber;
        public long StartingOffset;
        public long ExtentLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VOLUME_DISK_EXTENTS_WITH_FIRST
    {
        public uint NumberOfDiskExtents;
        public DISK_EXTENT Extents;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GET_LENGTH_INFORMATION
    {
        public long Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public byte DeviceType;
        public byte DeviceTypeModifier;
        [MarshalAs(UnmanagedType.U1)]
        public bool RemovableMedia;
        [MarshalAs(UnmanagedType.U1)]
        public bool CommandQueueing;
        public uint VendorIdOffset;
        public uint ProductIdOffset;
        public uint ProductRevisionOffset;
        public uint SerialNumberOffset;
        public uint BusType;
        public uint RawPropertiesLength;
        public byte RawDeviceProperties;
    }

    private sealed record DiskTopologyEntry(string NormalizedName, uint DiskNumber, long SizeBytes, IReadOnlyList<string> VolumeNames);
}
