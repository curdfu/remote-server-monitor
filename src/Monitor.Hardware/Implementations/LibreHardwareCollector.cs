using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Monitor.Hardware.Abstractions;
using Monitor.Hardware.Models;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Monitor.Hardware.Implementations;

// LibreHardwareCollector 是硬件采集边界：传感器数值来自 LibreHardwareMonitor，磁盘空间和物理盘拓扑来自 Windows API。
// 两类数据源的刷新成本和可用性不同，所以传感器树、磁盘拓扑、磁盘空间快照分别缓存，采样入口只组合当前可读到的数据。
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
    // Windows 卷到物理磁盘的映射在启动时构建；用于把温度传感器和磁盘空间展示关联起来。
    private readonly IReadOnlyList<DiskTopologyEntry> _diskTopologyEntries;
    private readonly object _syncRoot = new();
    private readonly object _diskUsageSyncRoot = new();
    private DiskUsageSnapshot? _diskUsageSnapshot;
    // 传感器对象本身可复用，采样时只刷新硬件树后读取 Value，避免每次遍历整棵树做名称匹配。
    private SensorCache? _sensorCache;
    private bool _isOpen;
    private bool _disposed;
    private static readonly TimeSpan DiskUsageCacheLifetime = TimeSpan.FromSeconds(10);

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
            var sensorCache = GetOrBuildSensorCacheCore();

            // LibreHardwareMonitor 不同硬件暴露的传感器名称不完全一致，后续读取都按优先级选择可用值。
            var cpuUsage = ReadSensorValue(sensorCache.CpuUsageSensor);
            var (cpuTemperature, cpuTemperatureSource) = ReadPreferredCpuTemperature(sensorCache);
            var (cpuFrequencyMhz, cpuFrequencySource) = ReadCpuClock(sensorCache.CpuSensors);
            var (cpuPowerWatts, cpuPowerSource) = ReadCpuPower(sensorCache.CpuSensors);

            var memoryUsed = ReadSensorValue(sensorCache.MemoryUsedSensor);
            var memoryAvailable = ReadSensorValue(sensorCache.MemoryAvailableSensor);
            var memoryUsagePercent = ReadSensorValue(sensorCache.MemoryUsageSensor);

            var (diskDrives, diskTemperature, diskTemperatureSource) = ReadDiskMetrics(sensorCache);
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
                    Name = sensorCache.CpuHardwareName,
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
                    Name = sensorCache.TotalMemoryHardwareName,
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

    private SensorCache GetOrBuildSensorCacheCore()
    {
        // 传感器树通常稳定，首次采样后缓存具体 ISensor 引用；重新 Open Computer 时会清空缓存。
        if (_sensorCache is not null)
        {
            return _sensorCache;
        }

        var builder = new SensorCacheBuilder();
        foreach (var hardware in _computer.Hardware)
        {
            builder.Visit(hardware);
        }

        _sensorCache = builder.Build(this);
        return _sensorCache;
    }

    private static (double? Value, string? Source) ReadPreferredCpuTemperature(SensorCache sensorCache)
    {
        // CPU Package 更接近整颗 CPU 的温度；缺失时回退到 Core Max，保证不同主板/CPU 上尽量有值。
        var preferred = ReadSensor(sensorCache.CpuPackageTemperatureSensor, sensorCache.CpuPackageTemperatureSource);
        if (preferred.Value.HasValue)
        {
            return preferred;
        }

        return ReadSensor(sensorCache.CpuCoreMaxTemperatureSensor, sensorCache.CpuCoreMaxTemperatureSource);
    }

    private static double? ReadSensorValue(ISensor? sensor)
    {
        return sensor is null ? null : Normalize(sensor.Value);
    }

    private static (double? Value, string? Source) ReadSensor(ISensor? sensor, string? source)
    {
        var value = ReadSensorValue(sensor);
        return value.HasValue ? (value.Value, source) : (null, null);
    }

    private static (DiskDriveMetrics[] Drives, double? Temperature, string? TemperatureSource) ReadDiskMetrics(SensorCache sensorCache)
    {
        if (sensorCache.StorageTemperatureGroups.Count == 0)
        {
            return (Array.Empty<DiskDriveMetrics>(), null, null);
        }

        var diskDrives = new DiskDriveMetrics[sensorCache.StorageTemperatureGroups.Count];
        double? diskTemperature = null;
        string? diskTemperatureSource = null;

        for (var index = 0; index < sensorCache.StorageTemperatureGroups.Count; index++)
        {
            var group = sensorCache.StorageTemperatureGroups[index];
            var (driveTemperature, driveTemperatureSource) = ReadPreferredDiskTemperature(group);

            if (driveTemperature.HasValue && (!diskTemperature.HasValue || driveTemperature.Value > diskTemperature.Value))
            {
                diskTemperature = driveTemperature.Value;
                diskTemperatureSource = driveTemperatureSource;
            }

            diskDrives[index] = new DiskDriveMetrics
            {
                Name = group.HardwareName,
                DiskNumber = group.TopologyEntry?.DiskNumber,
                SizeBytes = group.TopologyEntry?.SizeBytes,
                TemperatureC = driveTemperature,
                TemperatureSource = driveTemperatureSource
            };
        }

        return (diskDrives, diskTemperature, diskTemperatureSource);
    }

    private static (double? Value, string? Source) ReadPreferredDiskTemperature(StorageTemperatureGroup group)
    {
        // NVMe、SATA 和不同控制器暴露的温度名称不一致；先选常见主温度，再回退到任一可用温度传感器。
        var preferredNames = new[]
        {
            "Temperature",
            "Composite Temperature",
            "Drive Temperature",
            "Temperature 1"
        };

        foreach (var preferredName in preferredNames)
        {
            var match = ReadFirstSensor(
                group.Sensors,
                sensor => sensor.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase),
                sensor => $"{group.HardwareName}/{sensor.Name}");

            if (match.Value.HasValue)
            {
                return match;
            }
        }

        var composite = ReadFirstSensor(
            group.Sensors,
            sensor => sensor.Name.Contains("Composite", StringComparison.OrdinalIgnoreCase),
            sensor => $"{group.HardwareName}/{sensor.Name}");

        if (composite.Value.HasValue)
        {
            return composite;
        }

        return ReadFirstSensor(
            group.Sensors,
            static _ => true,
            sensor => $"{group.HardwareName}/{sensor.Name}");
    }

    public IReadOnlyDictionary<uint, long?> GetCurrentUsedBytesByDiskNumber(IEnumerable<uint> diskNumbers)
    {
        ArgumentNullException.ThrowIfNull(diskNumbers);

        if (!OperatingSystem.IsWindows() || _diskTopologyEntries.Count == 0)
        {
            return new Dictionary<uint, long?>();
        }

        var snapshot = GetDiskUsageSnapshot();
        var results = new Dictionary<uint, long?>();
        foreach (var diskNumber in diskNumbers.Distinct())
        {
            results[diskNumber] = snapshot.UsedBytesByDiskNumber.TryGetValue(diskNumber, out var usedBytes)
                ? usedBytes
                : null;
        }

        return results;
    }

    public IReadOnlyList<DiskSpaceInfo> GetCurrentDiskSpaces()
    {
        if (!OperatingSystem.IsWindows() || _diskTopologyEntries.Count == 0)
        {
            return Array.Empty<DiskSpaceInfo>();
        }

        return GetDiskUsageSnapshot().DiskSpaces;
    }

    [SupportedOSPlatform("windows")]
    private DiskUsageSnapshot GetDiskUsageSnapshot()
    {
        // 磁盘空间通过 Win32 API 读取，成本高于普通传感器；短缓存可避免首页频繁刷新时重复枚举卷。
        var now = DateTimeOffset.UtcNow;
        lock (_diskUsageSyncRoot)
        {
            if (_diskUsageSnapshot is not null && now - _diskUsageSnapshot.CreatedAt <= DiskUsageCacheLifetime)
            {
                return _diskUsageSnapshot;
            }

            _diskUsageSnapshot = LoadDiskUsageSnapshot(now);
            return _diskUsageSnapshot;
        }
    }

    [SupportedOSPlatform("windows")]
    private DiskUsageSnapshot LoadDiskUsageSnapshot(DateTimeOffset createdAt)
    {
        // 先按卷读取使用量，再按物理磁盘聚合；一个物理盘可能包含多个卷或盘符。
        var volumeNames = _diskTopologyEntries
            .SelectMany(entry => entry.VolumeNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var usageByVolume = GetCurrentVolumeUsage(volumeNames);
        var usedBytesByDiskNumber = new Dictionary<uint, long?>();
        var diskSpaces = new List<DiskSpaceInfo>();

        foreach (var group in _diskTopologyEntries.GroupBy(entry => entry.DiskNumber))
        {
            var entry = group.First();
            var usedBytes = ResolveDiskUsedBytes(entry, usageByVolume);
            usedBytesByDiskNumber[entry.DiskNumber] = usedBytes;
            diskSpaces.Add(new DiskSpaceInfo
            {
                Name = BuildDiskSpaceDisplayName(entry),
                DiskNumber = entry.DiskNumber,
                TotalBytes = entry.SizeBytes,
                UsedBytes = usedBytes,
                FreeBytes = usedBytes.HasValue
                    ? Math.Max(0, entry.SizeBytes - usedBytes.Value)
                    : null
            });
        }

        return new DiskUsageSnapshot(
            createdAt,
            usedBytesByDiskNumber,
            diskSpaces.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray());
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
            _sensorCache = null;
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
        // 频率传感器按可信度分层：Frequency 总值优先，其次 Effective Clock，最后普通 Clock/Core 平均。
        // 这样能兼容不同 CPU 和主板暴露的传感器名称，同时尽量避免 Bus Clock 这类非核心频率污染结果。
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
                // 优先使用 Total / CPU
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

                // Core 频率
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
        // CPU Package/Package 功耗最贴近整颗 CPU；如果没有，再退回任意 Power 传感器。
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
        // 磁盘温度来自硬件名称，空间来自卷；这里构建可匹配的物理盘候选名，把两条数据链路接起来。
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
        // 使用 Windows volume name 枚举而不是盘符枚举，才能覆盖无盘符卷和挂载到目录的卷。
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
        // IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS 可把卷映射回物理 DiskNumber；跨盘卷会返回多个 extent。
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
        // DeviceIoControl 返回的是非托管结构数组，需要 pin 住托管 buffer 后按结构偏移解析。
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
        // 一个物理盘可能对应多个卷，展示物理盘已用空间时需要把这些卷的使用量相加。
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
        // 优先用盘符/挂载点作为前端展示名；没有可见路径时再退回“磁盘 N”。
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
        // LibreHardwareMonitor 的硬件名和 Windows 物理盘名不一定完全一致，先精确匹配，再做包含匹配。
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
        // 去掉空格、标点和大小写差异，提升 “Samsung SSD” 与 “PHYSICALDRIVE0/DISK0” 候选名的匹配容错。
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
        // STORAGE_DEVICE_DESCRIPTOR 内的字符串字段是 offset 指向的 ANSI 结尾字符串，而不是固定长度字段。
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
        // 查询卷和磁盘元数据只需要打开设备句柄，不需要读写权限；共享读写删除避免影响系统正常挂载。
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

    // 磁盘空间快照按物理 DiskNumber 建索引，同时保留前端可直接展示的磁盘空间列表。
    private sealed record DiskUsageSnapshot(
        DateTimeOffset CreatedAt,
        IReadOnlyDictionary<uint, long?> UsedBytesByDiskNumber,
        IReadOnlyList<DiskSpaceInfo> DiskSpaces);

    private sealed record DiskTopologyEntry(string NormalizedName, uint DiskNumber, long SizeBytes, IReadOnlyList<string> VolumeNames);

    // SensorCache 保存已选中的传感器引用和存储温度分组，避免每次采样都做全树名称匹配。
    private sealed class SensorCache(
        string? cpuHardwareName,
        string? totalMemoryHardwareName,
        IReadOnlyList<ISensor> cpuSensors,
        ISensor? cpuUsageSensor,
        ISensor? cpuPackageTemperatureSensor,
        string? cpuPackageTemperatureSource,
        ISensor? cpuCoreMaxTemperatureSensor,
        string? cpuCoreMaxTemperatureSource,
        ISensor? memoryUsedSensor,
        ISensor? memoryAvailableSensor,
        ISensor? memoryUsageSensor,
        IReadOnlyList<StorageTemperatureGroup> storageTemperatureGroups)
    {
        public string? CpuHardwareName { get; } = cpuHardwareName;
        public string? TotalMemoryHardwareName { get; } = totalMemoryHardwareName;
        public IReadOnlyList<ISensor> CpuSensors { get; } = cpuSensors;
        public ISensor? CpuUsageSensor { get; } = cpuUsageSensor;
        public ISensor? CpuPackageTemperatureSensor { get; } = cpuPackageTemperatureSensor;
        public string? CpuPackageTemperatureSource { get; } = cpuPackageTemperatureSource;
        public ISensor? CpuCoreMaxTemperatureSensor { get; } = cpuCoreMaxTemperatureSensor;
        public string? CpuCoreMaxTemperatureSource { get; } = cpuCoreMaxTemperatureSource;
        public ISensor? MemoryUsedSensor { get; } = memoryUsedSensor;
        public ISensor? MemoryAvailableSensor { get; } = memoryAvailableSensor;
        public ISensor? MemoryUsageSensor { get; } = memoryUsageSensor;
        public IReadOnlyList<StorageTemperatureGroup> StorageTemperatureGroups { get; } = storageTemperatureGroups;
    }

    private sealed class StorageTemperatureGroup(string hardwareName, DiskTopologyEntry? topologyEntry, IReadOnlyList<ISensor> sensors)
    {
        public string HardwareName { get; } = hardwareName;
        public DiskTopologyEntry? TopologyEntry { get; } = topologyEntry;
        public IReadOnlyList<ISensor> Sensors { get; } = sensors;
    }

    private sealed class SensorCacheBuilder
    {
        private readonly List<ISensor> _cpuSensors = [];
        private readonly Dictionary<string, List<ISensor>> _storageTemperatureSensors = new(StringComparer.OrdinalIgnoreCase);

        public string? CpuHardwareName { get; private set; }
        public string? TotalMemoryHardwareName { get; private set; }
        public ISensor? CpuUsageSensor { get; private set; }
        public ISensor? CpuPackageTemperatureSensor { get; private set; }
        public string? CpuPackageTemperatureSource { get; private set; }
        public ISensor? CpuCoreMaxTemperatureSensor { get; private set; }
        public string? CpuCoreMaxTemperatureSource { get; private set; }
        public ISensor? MemoryUsedSensor { get; private set; }
        public ISensor? MemoryAvailableSensor { get; private set; }
        public ISensor? MemoryUsageSensor { get; private set; }

        public void Visit(IHardware hardware)
        {
            VisitCore(hardware);
        }

        public SensorCache Build(LibreHardwareCollector owner)
        {
            // 构建缓存时完成温度传感器到物理盘拓扑的匹配，后续采样只读传感器值。
            var storageTemperatureGroups = _storageTemperatureSensors
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair =>
                {
                    var topologyEntry = owner.ResolveDiskTopologyEntry(pair.Key);
                    if (owner._logger.IsEnabled(LogLevel.Debug))
                    {
                        owner._logger.LogDebug(
                            "Disk temperature mapping cached. HardwareName={HardwareName}, MatchedDiskNumber={DiskNumber}, SizeBytes={SizeBytes}, Volumes={Volumes}",
                            pair.Key,
                            topologyEntry?.DiskNumber,
                            topologyEntry?.SizeBytes,
                            topologyEntry is null ? "<none>" : string.Join(", ", topologyEntry.VolumeNames));
                    }

                    return new StorageTemperatureGroup(pair.Key, topologyEntry, pair.Value.ToArray());
                })
                .ToArray();

            return new SensorCache(
                CpuHardwareName,
                TotalMemoryHardwareName,
                _cpuSensors.ToArray(),
                CpuUsageSensor,
                CpuPackageTemperatureSensor,
                CpuPackageTemperatureSource,
                CpuCoreMaxTemperatureSensor,
                CpuCoreMaxTemperatureSource,
                MemoryUsedSensor,
                MemoryAvailableSensor,
                MemoryUsageSensor,
                storageTemperatureGroups);
        }

        private void VisitCore(IHardware hardware)
        {
            // LibreHardwareMonitor 的硬件树可能有多层 SubHardware，必须递归访问才能收集完整传感器。
            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu:
                    CpuHardwareName ??= hardware.Name;
                    CollectCpuSensors(hardware);
                    break;
                case HardwareType.Memory:
                    CollectMemorySensors(hardware);
                    break;
                case HardwareType.Storage:
                    CollectStorageSensors(hardware);
                    break;
            }

            foreach (var subHardware in hardware.SubHardware)
            {
                VisitCore(subHardware);
            }
        }

        private void CollectCpuSensors(IHardware hardware)
        {
            foreach (var sensor in hardware.Sensors)
            {
                _cpuSensors.Add(sensor);

                if (CpuUsageSensor is null &&
                    sensor.SensorType == SensorType.Load &&
                    sensor.Name.Contains("CPU Total", StringComparison.OrdinalIgnoreCase))
                {
                    CpuUsageSensor = sensor;
                }

                if (CpuPackageTemperatureSensor is null &&
                    sensor.SensorType == SensorType.Temperature &&
                    sensor.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase))
                {
                    CpuPackageTemperatureSensor = sensor;
                    CpuPackageTemperatureSource = sensor.Name;
                }

                if (CpuCoreMaxTemperatureSensor is null &&
                    sensor.SensorType == SensorType.Temperature &&
                    sensor.Name.Equals("Core Max", StringComparison.OrdinalIgnoreCase))
                {
                    CpuCoreMaxTemperatureSensor = sensor;
                    CpuCoreMaxTemperatureSource = sensor.Name;
                }
            }
        }

        private void CollectMemorySensors(IHardware hardware)
        {
            var isTotalMemoryHardware = hardware.Name.Equals("Total Memory", StringComparison.OrdinalIgnoreCase);
            if (isTotalMemoryHardware)
            {
                TotalMemoryHardwareName ??= hardware.Name;
            }

            foreach (var sensor in hardware.Sensors)
            {
                if (!isTotalMemoryHardware)
                {
                    continue;
                }

                if (MemoryUsedSensor is null &&
                    sensor.SensorType == SensorType.Data &&
                    sensor.Name.Equals("Memory Used", StringComparison.OrdinalIgnoreCase))
                {
                    MemoryUsedSensor = sensor;
                }

                if (MemoryAvailableSensor is null &&
                    sensor.SensorType == SensorType.Data &&
                    sensor.Name.Equals("Memory Available", StringComparison.OrdinalIgnoreCase))
                {
                    MemoryAvailableSensor = sensor;
                }

                if (MemoryUsageSensor is null &&
                    sensor.SensorType == SensorType.Load &&
                    sensor.Name.Equals("Memory", StringComparison.OrdinalIgnoreCase))
                {
                    MemoryUsageSensor = sensor;
                }
            }
        }

        private void CollectStorageSensors(IHardware hardware)
        {
            // 存储设备可能暴露多个温度传感器，先按硬件名分组，读取时再按优先级选择代表温度。
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature)
                {
                    continue;
                }

                if (!_storageTemperatureSensors.TryGetValue(hardware.Name, out var sensors))
                {
                    sensors = [];
                    _storageTemperatureSensors[hardware.Name] = sensors;
                }

                sensors.Add(sensor);
            }
        }
    }
}



