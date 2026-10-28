using System.Net;
using System.Net.NetworkInformation;
using NightWatch.Notification;
using Xunit;

namespace NightWatch.Tests;

/// <summary>
/// HostIdentity 主 IPv4 选择：纯函数（给假网卡快照），
/// 验证「优先默认网关物理网卡、排除虚拟网卡、无候选回退旧行为」。
/// </summary>
public class HostIdentitySelectionTests
{
    private static HostIdentity.InterfaceSnapshot Iface(string name, string description,
        IEnumerable<string> addresses, IEnumerable<string>? gateways = null,
        OperationalStatus status = OperationalStatus.Up,
        NetworkInterfaceType type = NetworkInterfaceType.Ethernet) =>
        new(name, description, type, status,
            addresses.Select(IPAddress.Parse).ToList(),
            (gateways ?? []).Select(IPAddress.Parse).ToList());

    [Fact]
    public void PrefersPhysicalInterfaceWithDefaultGateway_OverVirtualAdapters()
    {
        var interfaces = new[]
        {
            Iface("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", ["172.20.0.1"], ["172.20.0.1"]),
            Iface("Ethernet", "Intel(R) Ethernet Connection", ["192.168.2.127"], ["192.168.2.1"]),
            Iface("Tailscale", "Tailscale Tunnel", ["100.88.1.2"], ["100.88.1.2"])
        };

        Assert.Equal("192.168.2.127", HostIdentity.SelectPrimaryIPv4(interfaces));
    }

    [Fact]
    public void ExcludesVirtualByDescription_EvenWhenItHasGateway()
    {
        var interfaces = new[]
        {
            Iface("Ethernet 2", "Hyper-V Virtual Ethernet Adapter", ["172.28.1.1"], ["172.28.1.1"]),
            Iface("eth0", "Realtek PCIe GbE Family Controller", ["10.0.0.5"], ["10.0.0.1"])
        };

        Assert.Equal("10.0.0.5", HostIdentity.SelectPrimaryIPv4(interfaces));
    }

    [Fact]
    public void WithoutAnyGateway_PrefersNonVirtualInterface()
    {
        var interfaces = new[]
        {
            Iface("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", ["172.19.0.1"]),
            Iface("Ethernet", "Intel Ethernet", ["192.168.2.10"])
        };

        Assert.Equal("192.168.2.10", HostIdentity.SelectPrimaryIPv4(interfaces));
    }

    [Fact]
    public void WhenOnlyVirtualInterfacesExist_FallsBackToOldBehavior()
    {
        var interfaces = new[]
        {
            Iface("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", ["172.20.0.1"])
        };

        Assert.Equal("172.20.0.1", HostIdentity.SelectPrimaryIPv4(interfaces));
    }

    [Fact]
    public void SkipsDownLoopbackApipaAndIpv6OnlyInterfaces()
    {
        var interfaces = new[]
        {
            Iface("Ethernet", "Intel Ethernet", ["169.254.1.1"]),                                     // APIPA
            Iface("Ethernet 2", "Intel Ethernet", ["192.168.9.9"], status: OperationalStatus.Down),   // 未启用
            Iface("Loopback Pseudo-Interface 1", "Microsoft Loopback", ["127.0.0.1"],
                type: NetworkInterfaceType.Loopback),                                                 // 回环
            Iface("Ethernet 3", "Intel Ethernet", ["fe80::1"]),                                       // 只有 IPv6
            Iface("Ethernet 4", "Intel Ethernet", ["10.1.2.3"])
        };

        Assert.Equal("10.1.2.3", HostIdentity.SelectPrimaryIPv4(interfaces));
    }

    [Fact]
    public void TunnelType_IsTreatedAsVirtual()
    {
        var interfaces = new[]
        {
            Iface("tun0", "OpenVPN Tunnel", ["10.8.0.2"], ["10.8.0.1"], type: NetworkInterfaceType.Tunnel),
            Iface("Ethernet", "Intel Ethernet", ["192.168.1.20"])
        };

        Assert.Equal("192.168.1.20", HostIdentity.SelectPrimaryIPv4(interfaces));
    }

    [Fact]
    public void ReturnsNull_WhenNoUsableAddressExists() =>
        Assert.Null(HostIdentity.SelectPrimaryIPv4([Iface("WLAN", "Intel Wi-Fi", ["169.254.2.2"])]));

    [Fact]
    public void RealMachineProbe_ReturnsNullOrValidIPv4()
    {
        // 本机探测只做冒烟：无网卡环境（CI 容器）返回 null 也算正常
        var ip = HostIdentity.TryGetPrimaryIPv4();
        if (ip is not null)
        {
            Assert.True(IPAddress.TryParse(ip, out var parsed));
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, parsed!.AddressFamily);
        }
    }
}
