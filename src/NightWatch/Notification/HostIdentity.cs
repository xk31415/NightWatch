using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NightWatch.Notification;

/// <summary>告警里展示的「主机」名称与 IPv4。</summary>
internal static class HostIdentity
{
    public static string ResolveHostName(string? configuredHostLabel) =>
        string.IsNullOrWhiteSpace(configuredHostLabel)
            ? Environment.MachineName
            : configuredHostLabel.Trim();

    /// <summary>取第一个已启用、非回环的 IPv4 地址（跳过 APIPA 169.254.x.x）。</summary>
    internal static string? TryGetPrimaryIPv4()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(addr.Address))
                        continue;
                    if (IsApipa(addr.Address))
                        continue;

                    return addr.Address.ToString();
                }
            }
        }
        catch
        {
            // 权限或平台限制时只展示计算机名
        }

        return null;
    }

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
