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

    // ---- 取消语义：必须向上抛，不能被 catch (Exception) 吞掉 ----

    [LinuxFact]
    public async Task LinuxCpu_CancelledToken_Throws()
    {
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
