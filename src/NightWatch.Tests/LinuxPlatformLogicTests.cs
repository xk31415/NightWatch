using NightWatch.Platform;
using Xunit;

namespace NightWatch.Tests;

/// <summary>
/// Linux 平台实现的纯逻辑测试（/proc 解析、权限位判定、systemd 状态映射）。
/// 不触碰真实 /proc 或 systemctl，任意 OS 上都可跑。
/// </summary>
public class LinuxPlatformLogicTests
{
    // ---- /proc/stat 解析 ----

    [Fact]
    public void ParseCpuLine_ComputesIdleAndTotal()
    {
        // user nice system idle iowait irq softirq steal
        var line = "cpu  100 0 50 800 50 0 0 0";
        var (idle, total) = LinuxSystemMetrics.ParseCpuLine(line);

        Assert.Equal(850, idle);   // idle + iowait
        Assert.Equal(1000, total); // 所有计数之和
    }

    [Fact]
    public void ParseCpuLine_ToleratesMissingTrailingFields()
    {
        // 旧内核可能没有 steal 等字段
        var line = "cpu  10 0 5 85";
        var (idle, total) = LinuxSystemMetrics.ParseCpuLine(line);

        Assert.Equal(85, idle);
        Assert.Equal(100, total);
    }

    [Fact]
    public void ParseCpuLine_InvalidFormat_Throws()
    {
        Assert.ThrowsAny<Exception>(() => LinuxSystemMetrics.ParseCpuLine("cpu0 1 2 3 4"));
        Assert.ThrowsAny<Exception>(() => LinuxSystemMetrics.ParseCpuLine("bogus"));
    }

    // ---- /proc/meminfo 解析 ----

    [Fact]
    public void ParseMemInfo_ReadsKbValues()
    {
        const string content = """
            MemTotal:       16333728 kB
            MemFree:         1234567 kB
            MemAvailable:    8000000 kB
            Buffers:          200000 kB
            Cached:          3000000 kB
            """;

        var map = LinuxSystemMetrics.ParseMemInfo(content);

        Assert.Equal(16333728, map["MemTotal"]);
        Assert.Equal(8000000, map["MemAvailable"]);
        Assert.Equal(1234567, map["MemFree"]);
    }

    // ---- Linux 权限判定 ----

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute, 0, false)]   // root 755：可信
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute, 0, false)] // root 775：组可写但属主特权，放行
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute, 0, true)] // 777：other 可写
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute, 1000, true)] // 普通用户属主
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute, 33, false)]  // www-data 等系统账户
    public void LinuxPermission_JudgesOwnerAndWorldWrite(UnixFileMode mode, int uid, bool expected)
    {
        Assert.Equal(expected, LinuxPermissionAuditor.IsUntrusted(mode, uid));
    }

    // ---- systemd 状态映射 ----

    [Theory]
    [InlineData("active", ServiceUnitStatus.Running)]
    [InlineData("reloading", ServiceUnitStatus.Running)]
    [InlineData("inactive", ServiceUnitStatus.Stopped)]
    [InlineData("failed", ServiceUnitStatus.Stopped)]
    [InlineData("activating", ServiceUnitStatus.StartPending)]
    [InlineData("deactivating", ServiceUnitStatus.Other)]
    [InlineData("maintenance", ServiceUnitStatus.Other)]
    [InlineData("", ServiceUnitStatus.Other)]
    public void SystemdStatus_MapsToUnifiedEnum(string output, ServiceUnitStatus expected)
    {
        Assert.Equal(expected, SystemdServiceControl.Map(output));
    }

    /// <summary>单元不存在必须与“已停止”区分开，否则 unit 名写错会被当成状态异常反复尝试启动。</summary>
    [Fact]
    public void SystemdStatus_UnknownUnit_MapsToNotFound()
    {
        // is-active 对不存在的单元返回退出码 4
        Assert.Equal(ServiceUnitStatus.NotFound, SystemdServiceControl.Map("inactive", 4));
        Assert.Equal(ServiceUnitStatus.NotFound, SystemdServiceControl.Map("unknown", 0));
        // 正常查询（退出码 3 = 未运行）仍是 Stopped
        Assert.Equal(ServiceUnitStatus.Stopped, SystemdServiceControl.Map("inactive", 3));
    }
}
