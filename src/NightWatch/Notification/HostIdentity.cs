using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NightWatch.Notification;

/// <summary>告警里展示的「主机」名称与 IPv4。</summary>
internal static class HostIdentity
{
    /// <summary>已知虚拟网卡关键字：名称或描述命中即视为虚拟（VPN / 容器 / 虚拟化网桥等）。</summary>
    private static readonly string[] VirtualInterfaceMarkers =
    [
        "vethernet", "hyper-v", "hyperv", "wsl", "virtualbox", "vmware", "docker",
        "veth", "br-", "tun", "tap", "tailscale", "zerotier", "loopback", "vpn", "hamachi"
    ];

    public static string ResolveHostName(string? configuredHostLabel) =>
        string.IsNullOrWhiteSpace(configuredHostLabel)
            ? Environment.MachineName
            : configuredHostLabel.Trim();

    /// <summary>
    /// 网卡快照：把 <see cref="NetworkInterface"/> 的字段抽出来，
    /// 让选择逻辑成为可用假数据单测的纯函数。
    /// </summary>
    internal sealed record InterfaceSnapshot(
        string Name,
        string Description,
        NetworkInterfaceType Type,
        OperationalStatus Status,
        IReadOnlyList<IPAddress> UnicastAddresses,
        IReadOnlyList<IPAddress> GatewayAddresses);

    /// <summary>取本机主 IPv4；权限/平台限制或没有可用地址时返回 null。</summary>
    internal static string? TryGetPrimaryIPv4()
    {
        try
        {
            var interfaces = new List<InterfaceSnapshot>();
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    // 个别网卡（半断开/VPN 适配器）读 IP 属性会抛异常：只跳过它，不影响其它候选。
                    interfaces.Add(Snapshot(networkInterface));
                }
                catch
                {
                    // 忽略该网卡
                }
            }

            return SelectPrimaryIPv4(interfaces);
        }
        catch
        {
            // 权限或平台限制时只展示计算机名
            return null;
        }
    }

    private static InterfaceSnapshot Snapshot(NetworkInterface networkInterface)
    {
        var properties = networkInterface.GetIPProperties();
        return new InterfaceSnapshot(
            networkInterface.Name,
            networkInterface.Description,
            networkInterface.NetworkInterfaceType,
            networkInterface.OperationalStatus,
            properties.UnicastAddresses.Select(unicast => unicast.Address).ToList(),
            properties.GatewayAddresses.Select(gateway => gateway.Address).ToList());
    }

    /// <summary>
    /// 纯函数：按优先级选主 IPv4——
    /// 1) 有 IPv4 默认网关且不是虚拟网卡；
    /// 2) 非虚拟网卡的任一可用 IPv4；
    /// 3) 全部落空时回退旧行为（枚举顺序第一个可用 IPv4，允许虚拟网卡）。
    /// 同优先级内保持枚举顺序（OrderBy 稳定），行为可预期。
    /// </summary>
    internal static string? SelectPrimaryIPv4(IReadOnlyList<InterfaceSnapshot> interfaces) =>
        interfaces
            .Where(IsUsableInterface)
            .Select(info => (Info: info, Address: FindCandidateIPv4(info)))
            .Where(candidate => candidate.Address is not null)
            .OrderBy(candidate => Rank(candidate.Info))
            .Select(candidate => candidate.Address)
            .FirstOrDefault();

    private static bool IsUsableInterface(InterfaceSnapshot info) =>
        info.Status == OperationalStatus.Up
        && info.Type != NetworkInterfaceType.Loopback;

    /// <summary>候选排序：0 = 物理网卡且有默认网关，1 = 物理网卡，2 = 虚拟网卡（回退）。</summary>
    private static int Rank(InterfaceSnapshot info)
    {
        if (IsVirtualInterface(info))
            return 2;
        return HasIPv4Gateway(info) ? 0 : 1;
    }

    /// <summary>名称/描述命中已知虚拟网卡关键字，或接口类型是隧道（VPN）。</summary>
    internal static bool IsVirtualInterface(InterfaceSnapshot info)
    {
        if (info.Type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback)
            return true;

        var text = $"{info.Name} {info.Description}";
        return VirtualInterfaceMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasIPv4Gateway(InterfaceSnapshot info) =>
        info.GatewayAddresses.Any(gateway =>
            gateway.AddressFamily == AddressFamily.InterNetwork
            && !gateway.Equals(IPAddress.Any)
            && !IPAddress.IsLoopback(gateway));

    private static string? FindCandidateIPv4(InterfaceSnapshot info) =>
        info.UnicastAddresses
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Where(address => !IPAddress.IsLoopback(address))
            .Where(address => !IsApipa(address))
            .Select(address => address.ToString())
            .FirstOrDefault();

    private static bool IsApipa(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}

/// <summary>
/// 本机 IPv4 的短 TTL 缓存。
/// 看门狗是常驻服务，DHCP 续租 / 网卡切换 / VPN 上下线后 IP 会变，
/// 构造时取一次会一直推过期地址；而每条告警都枚举一遍网卡又没必要。
/// </summary>
internal sealed class HostIpCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    private readonly Func<string?> _probe;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();

    private string? _value;
    private DateTime _fetchedAtUtc = DateTime.MinValue;

    /// <param name="probe">取 IP 的方式，默认读本机网卡；测试可注入。</param>
    /// <param name="ttl">缓存时长，默认 30 秒。</param>
    /// <param name="now">当前时间（UTC），测试可注入。</param>
    public HostIpCache(Func<string?>? probe = null, TimeSpan? ttl = null, Func<DateTime>? now = null)
    {
        _probe = probe ?? HostIdentity.TryGetPrimaryIPv4;
        _ttl = ttl ?? DefaultTtl;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public string? Get()
    {
        var now = _now();
        lock (_lock)
        {
            // _fetchedAtUtc 初值为 MinValue，首次调用必然刷新
            if (now - _fetchedAtUtc < _ttl)
                return _value;

            _value = _probe();
            _fetchedAtUtc = now;
            return _value;
        }
    }
}
