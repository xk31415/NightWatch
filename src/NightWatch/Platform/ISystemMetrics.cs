namespace NightWatch.Platform;

/// <summary>
/// 跨平台系统资源采集：整机 CPU 使用率与内存使用率。
/// Windows 用 PerformanceCounter / GlobalMemoryStatusEx，Linux 读 /proc。
/// </summary>
public interface ISystemMetrics : IDisposable
{
    /// <summary>整机 CPU 使用率（%）；采集失败返回 null（跳过该项检查）。</summary>
    Task<double?> GetCpuPercentAsync(CancellationToken ct);

    /// <summary>内存使用率（%）；采集失败返回 0。</summary>
    double GetMemoryUsedPercent();
}
