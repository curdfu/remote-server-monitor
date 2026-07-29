using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Monitor.Contracts.Options;
using Monitor.Network.Abstractions;
using Monitor.Network.Enums;

namespace Monitor.Network.Services;

// 地址分类器负责把 ETW 事件里的远端/本地地址归入 WAN、LAN、Loopback 或 Other。
// 判定顺序是业务语义的一部分：显式配置优先于自动推断，本机回环和广播/组播等特殊地址要先处理，避免被私网规则误归类。
public sealed class AddressClassifier : IAddressClassifier, IDisposable
{
    private static readonly TimeSpan LocalSubnetRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly object _syncRoot = new();
    private readonly ILogger<AddressClassifier> _logger;
    private readonly IDisposable _settingsRegistration;
    private SubnetDefinition[] _localSubnets = [];
    private long _nextRefreshAtUtcTicks;
    private AdditionalSubnetCache _additionalSubnetCache = AdditionalSubnetCache.Empty;
    private AddressClassificationSettings _settings;
    private bool _disposed;

    public AddressClassifier(
        IMonitorSettingsProvider settingsMonitor,
        ILogger<AddressClassifier> logger)
    {
        _logger = logger;
        _settings = CloneSettings(settingsMonitor.Current.AddressClassification);
        _settingsRegistration = settingsMonitor.RegisterChangeCallback(OnSettingsChanged);
    }

    public AddressScopeType Classify(string? remoteAddress, string? localAddress = null)
    {
        var remote = TryParseAddress(remoteAddress);
        var local = TryParseAddress(localAddress);
        return Classify(remote, local);
    }

    public AddressScopeType Classify(IPAddress? remoteAddress, IPAddress? localAddress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (remoteAddress is null)
        {
            return AddressScopeType.Other;
        }

        var remote = Normalize(remoteAddress);
        var local = localAddress is null ? null : Normalize(localAddress);
        var settings = Volatile.Read(ref _settings);
        var additionalSubnets = GetAdditionalSubnets(settings);
        Span<byte> remoteAddressBuffer = stackalloc byte[16];
        if (!remote.TryWriteBytes(remoteAddressBuffer, out var remoteAddressLength))
        {
            return AddressScopeType.Other;
        }

        var remoteAddressBytes = remoteAddressBuffer[..remoteAddressLength];
        var remoteAddressFamily = remote.AddressFamily;

        // 回环地址可能出现在 remote 或 local 任一侧，只要配置允许就直接归为 Loopback。
        if (settings.TreatLoopbackAsLoopback &&
            (IPAddress.IsLoopback(remote) || (local is not null && IPAddress.IsLoopback(local))))
        {
            return AddressScopeType.Loopback;
        }

        if (IsBroadcast(remote) ||
            IsMulticast(remoteAddressFamily, remoteAddressBytes) ||
            IsUnspecified(remote))
        {
            return AddressScopeType.Other;
        }

        // 用户显式配置的 WAN/LAN CIDR 优先级高于自动私网判断，用于处理 VPN、代理网段等特殊拓扑。
        if (MatchesAny(additionalSubnets.WanSubnets, remoteAddressFamily, remoteAddressBytes))
        {
            return AddressScopeType.Wan;
        }

        if (MatchesAny(additionalSubnets.LanSubnets, remoteAddressFamily, remoteAddressBytes))
        {
            return AddressScopeType.Lan;
        }

        // 本机网卡子网比通用私网规则更贴近真实局域网，但枚举成本更高，所以结果带短缓存。
        if (settings.TreatLocalSubnetsAsLan &&
            IsInLocalSubnet(remoteAddressFamily, remoteAddressBytes))
        {
            return AddressScopeType.Lan;
        }

        if (settings.TreatPrivateAddressesAsLan &&
            IsPrivateOrLinkLocal(remoteAddressFamily, remoteAddressBytes, remote.IsIPv6SiteLocal))
        {
            return AddressScopeType.Lan;
        }

        return remote.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
            ? AddressScopeType.Wan
            : AddressScopeType.Other;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _settingsRegistration.Dispose();
        _disposed = true;
    }

    private void OnSettingsChanged(MonitorSettings updatedSettings)
    {
        // 设置对象克隆后再发布，避免外部数组引用变化导致缓存匹配和分类结果不稳定。
        Volatile.Write(ref _settings, CloneSettings(updatedSettings.AddressClassification));

        lock (_syncRoot)
        {
            _additionalSubnetCache = AdditionalSubnetCache.Empty;
            Volatile.Write(ref _nextRefreshAtUtcTicks, 0);
        }
    }

    private AdditionalSubnetCache GetAdditionalSubnets(AddressClassificationSettings settings)
    {
        // AdditionalSubnetCache 按配置数组引用做快速匹配；设置热更新时会替换数组并清空缓存。
        var cache = Volatile.Read(ref _additionalSubnetCache);
        if (cache.Matches(settings))
        {
            return cache;
        }

        lock (_syncRoot)
        {
            if (_additionalSubnetCache.Matches(settings))
            {
                return _additionalSubnetCache;
            }

            _additionalSubnetCache = AdditionalSubnetCache.Create(settings, _logger);
            return _additionalSubnetCache;
        }
    }

    private bool IsInLocalSubnet(AddressFamily addressFamily, ReadOnlySpan<byte> addressBytes)
    {
        RefreshLocalSubnetsIfNeeded();
        return MatchesAny(Volatile.Read(ref _localSubnets), addressFamily, addressBytes);
    }

    private void RefreshLocalSubnetsIfNeeded()
    {
        // 网卡状态变化不需要每个包都重新枚举；30 秒缓存能兼顾拓扑变化和高频分类成本。
        var now = DateTimeOffset.UtcNow;
        if (now.UtcTicks < Volatile.Read(ref _nextRefreshAtUtcTicks))
        {
            return;
        }

        lock (_syncRoot)
        {
            if (now.UtcTicks < Volatile.Read(ref _nextRefreshAtUtcTicks))
            {
                return;
            }

            Volatile.Write(ref _localSubnets, LoadLocalSubnets());
            Volatile.Write(ref _nextRefreshAtUtcTicks, now.Add(LocalSubnetRefreshInterval).UtcTicks);
        }
    }

    private SubnetDefinition[] LoadLocalSubnets()
    {
        try
        {
            var subnets = new List<SubnetDefinition>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = Normalize(unicast.Address);
                    if (IPAddress.IsLoopback(address))
                    {
                        continue;
                    }

                    if (!SubnetDefinition.TryCreate(address, unicast.PrefixLength, out var subnet))
                    {
                        continue;
                    }

                    subnets.Add(subnet);
                }
            }

            return subnets
                .Distinct()
                .ToArray();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to enumerate local network subnets for LAN classification.");
            return [];
        }
    }

    private static AddressClassificationSettings CloneSettings(AddressClassificationSettings? settings)
    {
        var source = settings ?? new AddressClassificationSettings();
        return new AddressClassificationSettings
        {
            TreatPrivateAddressesAsLan = source.TreatPrivateAddressesAsLan,
            TreatLocalSubnetsAsLan = source.TreatLocalSubnetsAsLan,
            TreatLoopbackAsLoopback = source.TreatLoopbackAsLoopback,
            AdditionalLanCidrs = source.AdditionalLanCidrs?.ToArray() ?? [],
            AdditionalWanCidrs = source.AdditionalWanCidrs?.ToArray() ?? []
        };
    }

    private static bool MatchesAny(
        IReadOnlyList<SubnetDefinition> subnets,
        AddressFamily addressFamily,
        ReadOnlySpan<byte> addressBytes)
    {
        for (var index = 0; index < subnets.Count; index++)
        {
            if (subnets[index].Contains(addressFamily, addressBytes))
            {
                return true;
            }
        }

        return false;
    }

    private static IPAddress? TryParseAddress(string? value)
    {
        return IPAddress.TryParse(value, out var address)
            ? Normalize(address)
            : null;
    }

    private static IPAddress Normalize(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private static bool IsPrivateOrLinkLocal(
        AddressFamily addressFamily,
        ReadOnlySpan<byte> addressBytes,
        bool isIpv6SiteLocal)
    {
        if (addressFamily == AddressFamily.InterNetwork)
        {
            return addressBytes[0] == 10 ||
                   (addressBytes[0] == 172 && addressBytes[1] is >= 16 and <= 31) ||
                   (addressBytes[0] == 192 && addressBytes[1] == 168) ||
                   (addressBytes[0] == 169 && addressBytes[1] == 254);
        }

        if (addressFamily == AddressFamily.InterNetworkV6)
        {
            var firstByte = addressBytes[0];
            var secondByte = addressBytes[1];
            var isUniqueLocal = (firstByte & 0b1111_1110) == 0b1111_1100;
            var isLinkLocal = firstByte == 0xfe && (secondByte & 0b1100_0000) == 0b1000_0000;

            return isUniqueLocal || isLinkLocal || isIpv6SiteLocal;
        }

        return false;
    }

    private static bool IsMulticast(AddressFamily addressFamily, ReadOnlySpan<byte> addressBytes)
    {
        if (addressFamily == AddressFamily.InterNetwork)
        {
            return addressBytes[0] is >= 224 and <= 239;
        }

        return addressFamily == AddressFamily.InterNetworkV6 && addressBytes[0] == 0xff;
    }

    private static bool IsBroadcast(IPAddress address)
    {
        return address.AddressFamily == AddressFamily.InterNetwork &&
               address.Equals(IPAddress.Broadcast);
    }

    private static bool IsUnspecified(IPAddress address)
    {
        return address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
    }

    private readonly record struct SubnetDefinition(AddressFamily AddressFamily, byte[] NetworkBytes, int PrefixLength)
    {
        public bool Contains(AddressFamily addressFamily, ReadOnlySpan<byte> candidateBytes)
        {
            if (addressFamily != AddressFamily || candidateBytes.Length != NetworkBytes.Length)
            {
                return false;
            }

            var wholeBytes = PrefixLength / 8;
            var remainingBits = PrefixLength % 8;

            for (var index = 0; index < wholeBytes; index++)
            {
                if (candidateBytes[index] != NetworkBytes[index])
                {
                    return false;
                }
            }

            if (remainingBits == 0)
            {
                return true;
            }

            var mask = 0xFF << (8 - remainingBits);
            return (candidateBytes[wholeBytes] & mask) == (NetworkBytes[wholeBytes] & mask);
        }

        public static bool TryParse(string? cidr, out SubnetDefinition subnet)
        {
            subnet = default;
            if (string.IsNullOrWhiteSpace(cidr))
            {
                return false;
            }

            var parts = cidr.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 ||
                !IPAddress.TryParse(parts[0], out var address) ||
                !int.TryParse(parts[1], out var prefixLength))
            {
                return false;
            }

            return TryCreate(address, prefixLength, out subnet);
        }

        public static bool TryCreate(IPAddress address, int prefixLength, out SubnetDefinition subnet)
        {
            subnet = default;
            var normalized = Normalize(address);
            var bytes = normalized.GetAddressBytes();
            var maxPrefixLength = normalized.AddressFamily == AddressFamily.InterNetwork ? 32 :
                normalized.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : -1;

            if (maxPrefixLength < 0 || prefixLength < 0 || prefixLength > maxPrefixLength)
            {
                return false;
            }

            var networkBytes = new byte[bytes.Length];
            Array.Copy(bytes, networkBytes, bytes.Length);

            var wholeBytes = prefixLength / 8;
            var remainingBits = prefixLength % 8;

            if (wholeBytes < networkBytes.Length)
            {
                if (remainingBits > 0)
                {
                    var mask = 0xFF << (8 - remainingBits);
                    networkBytes[wholeBytes] = (byte)(networkBytes[wholeBytes] & mask);
                    wholeBytes++;
                }

                for (var index = wholeBytes; index < networkBytes.Length; index++)
                {
                    networkBytes[index] = 0;
                }
            }

            subnet = new SubnetDefinition(normalized.AddressFamily, networkBytes, prefixLength);
            return true;
        }
    }

    private sealed class AdditionalSubnetCache(
        string[]? sourceLanCidrs,
        string[]? sourceWanCidrs,
        IReadOnlyList<SubnetDefinition> lanSubnets,
        IReadOnlyList<SubnetDefinition> wanSubnets)
    {
        public static readonly AdditionalSubnetCache Empty = new(Array.Empty<string>(), Array.Empty<string>(), [], []);

        public IReadOnlyList<SubnetDefinition> LanSubnets { get; } = lanSubnets;
        public IReadOnlyList<SubnetDefinition> WanSubnets { get; } = wanSubnets;

        public bool Matches(AddressClassificationSettings settings)
        {
            return ReferenceEquals(sourceLanCidrs, settings.AdditionalLanCidrs) &&
                   ReferenceEquals(sourceWanCidrs, settings.AdditionalWanCidrs);
        }

        public static AdditionalSubnetCache Create(AddressClassificationSettings settings, ILogger logger)
        {
            return new AdditionalSubnetCache(
                settings.AdditionalLanCidrs,
                settings.AdditionalWanCidrs,
                ParseSubnets(settings.AdditionalLanCidrs, logger, "LAN"),
                ParseSubnets(settings.AdditionalWanCidrs, logger, "WAN"));
        }

        private static IReadOnlyList<SubnetDefinition> ParseSubnets(
            IEnumerable<string>? cidrs,
            ILogger logger,
            string scopeName)
        {
            if (cidrs is null)
            {
                return [];
            }

            var subnets = new List<SubnetDefinition>();
            foreach (var cidr in cidrs)
            {
                if (!SubnetDefinition.TryParse(cidr, out var subnet))
                {
                    logger.LogWarning("Skipped invalid additional {ScopeName} CIDR '{Cidr}'.", scopeName, cidr);
                    continue;
                }

                subnets.Add(subnet);
            }

            return subnets;
        }
    }
}
