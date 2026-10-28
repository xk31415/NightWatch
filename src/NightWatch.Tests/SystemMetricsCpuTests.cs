using System.Runtime.Versioning;
using NightWatch.Platform;
using Xunit;

namespace NightWatch.Tests;

public class SystemMetricsCpuTests
{
    // ---- 纯函数：两次采样的差值 ----

    [Fact]
    public void ComputeCpuPercent_HalfBusy_IsFifty()
    {
        // 100 tick 里 50 tick 空闲
        Assert.Equal(50.0, LinuxSystemMetrics.ComputeCpuPercent((850, 1000), (900, 1100)));
    }

    [Fact]
    public void ComputeCpuPercent_AllIdle_IsZero()
    {
        Assert.Equal(0.0, LinuxSystemMetrics.ComputeCpuPercent((800, 1000), (900, 1100)));
    }

    [Fact]
    public void ComputeCpuPercent_AllBusy_IsHundred()
    {
        Assert.Equal(100.0, LinuxSystemMetrics.ComputeCpuPercent((800, 1000), (800, 1100)));
    }

    [Fact]
    public void ComputeCpuPercent_NoElapsedTicks_ReturnsNull()
    {
        // 计数器没有前进（采样间隔为 0）
        Assert.Null(LinuxSystemMetrics.ComputeCpuPercent((800, 1000), (800, 1000)));
        // 计数回绕：total 变小
        Assert.Null(LinuxSystemMetrics.ComputeCpuPercent((800, 1000), (700, 900)));
    }

    [Fact]
    public void ComputeCpuPercent_IdleDecrease_ClampsToHundred()
    {
        // idle 变小属于计数异常/回绕，按 0 空闲处理，结果不应超过 100
        Assert.Equal(100.0, LinuxSystemMetrics.ComputeCpuPercent((900, 1000), (800, 1100)));
    }

    // ---- /proc/meminfo 回退公式：MemAvailable 优先、SReclaimable 计入、结果 clamp ----

    [Fact]
    public void ComputeMemInfoUsedPercent_PrefersMemAvailable()
    {
        var map = new Dictionary<string, long>
        {
            ["MemTotal"] = 1000,
            ["MemAvailable"] = 250,
            // MemAvailable 存在时不应再走 free+buffers+cached 的旧公式
            ["MemFree"] = 900,
        };

        Assert.Equal(75.0, LinuxSystemMetrics.ComputeMemInfoUsedPercent(map));
    }

    /// <summary>老内核没有 MemAvailable：SReclaimable 属于可回收缓存，不计入会虚高。</summary>
    [Fact]
    public void ComputeMemInfoUsedPercent_CountsSReclaimable()
    {
        var map = LinuxSystemMetrics.ParseMemInfo("""
            MemTotal:       1000 kB
            MemFree:         100 kB
            Buffers:          50 kB
            Cached:          200 kB
            SReclaimable:    150 kB
            """);

        // 已用 = 1000 - 100 - 50 - 200 - 150 = 500
        Assert.Equal(50.0, LinuxSystemMetrics.ComputeMemInfoUsedPercent(map));
    }

    [Fact]
    public void ComputeMemInfoUsedPercent_ClampsOutOfRange()
    {
        // 计数异常导致「已用」比总量还大 → clamp 到 0，不能出现负数百分比
        var negative = new Dictionary<string, long>
        {
            ["MemTotal"] = 1000,
            ["MemFree"] = 500,
            ["Buffers"] = 300,
            ["Cached"] = 300,
            ["SReclaimable"] = 100,
        };
        Assert.Equal(0.0, LinuxSystemMetrics.ComputeMemInfoUsedPercent(negative));

        // MemAvailable 比 MemTotal 还大同样 clamp 到 0
        var availableTooLarge = new Dictionary<string, long>
        {
            ["MemTotal"] = 1000,
            ["MemAvailable"] = 2000,
        };
        Assert.Equal(0.0, LinuxSystemMetrics.ComputeMemInfoUsedPercent(availableTooLarge));
    }

    [Fact]
    public void ComputeMemInfoUsedPercent_MissingMemTotal_ReturnsNull() =>
        Assert.Null(LinuxSystemMetrics.ComputeMemInfoUsedPercent(new Dictionary<string, long>
        {
            ["MemFree"] = 100,
        }));

    [Theory]
    [InlineData(-5.0, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(42.5, 42.5)]
    [InlineData(100.0, 100.0)]
    [InlineData(120.0, 100.0)]
    public void ClampPercent_BoundsToZeroHundred(double input, double expected) =>
        Assert.Equal(expected, LinuxSystemMetrics.ClampPercent(input));

    // ---- 取消语义：必须向上抛，不能被 catch (Exception) 吞掉 ----

    [LinuxFact]
    public async Task LinuxCpu_CancelledToken_Throws()    {
        var metrics = new LinuxSystemMetrics();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            metrics.GetCpuPercentAsync(new CancellationToken(canceled: true)));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task WindowsCpu_CancelledToken_Throws()
    {
        var metrics = new WindowsSystemMetrics();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            metrics.GetCpuPercentAsync(new CancellationToken(canceled: true)));
    }
}
