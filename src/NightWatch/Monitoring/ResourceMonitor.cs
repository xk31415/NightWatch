using NightWatch.Notification;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NightWatch.Models;

namespace NightWatch.Monitoring;

/// <summary>
/// 系统资源监控器：CPU 使用率、内存使用率、磁盘剩余空间阈值告警。
/// </summary>
public class ResourceMonitor : PollingMonitorBase
{
    private readonly ResourceRule _rule;
    private PerformanceCounter? _cpuCounter;

    public ResourceMonitor(WatchDogOptions options, IEventNotifier notifier, ILogger<ResourceMonitor> logger)
        : base(notifier, logger)
    {
        _rule = options.Resources;
    }

    protected override string MonitorName => "资源监控";
    protected override int IntervalSeconds => Math.Max(15, _rule.IntervalSeconds);

    protected override async Task CheckAllAsync(CancellationToken ct)
    {
        var problems = new List<string>();

        if (_rule.CpuPercent > 0)
        {
            var cpu = await GetCpuPercentAsync(ct);
            if (cpu is not null)
            {
                if (cpu > _rule.CpuPercent)
                    problems.Add($"CPU 使用率 {cpu:F1}% 超过阈值 {_rule.CpuPercent}%");
                else
                    Logger.LogDebug("CPU 使用率 {Cpu:F1}%", cpu);
            }
        }

        if (_rule.MemoryPercent > 0)
        {
            var mem = GetMemoryUsedPercent();
            if (mem > _rule.MemoryPercent)
                problems.Add($"内存使用率 {mem:F1}% 超过阈值 {_rule.MemoryPercent}%");
            else
                Logger.LogDebug("内存使用率 {Mem:F1}%", mem);
        }

        foreach (var disk in _rule.Disks)
        {
            try
            {
                var drive = new DriveInfo(disk.Drive);
                if (!drive.IsReady)
                {
                    problems.Add($"磁盘 {disk.Drive} 不可用");
                    continue;
                }
                var freeGb = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
                if (freeGb < disk.MinFreeGB)
                    problems.Add($"磁盘 {disk.Drive} 剩余 {freeGb:F1} GB，低于阈值 {disk.MinFreeGB} GB");
            }
            catch (Exception ex)
            {
                problems.Add($"检查磁盘 {disk.Drive} 失败: {ex.Message}");
            }
        }

        var key = "system-resources";
        var result = problems.Count > 0
            ? MonitorResult.Unhealthy(string.Join("; ", problems))
            : MonitorResult.Healthy("系统资源正常");
        await ReportAsync(key, result, ct, Minutes(_rule.CooldownMinutes));
    }

    /// <summary>取整机 CPU 使用率。PerformanceCounter 首次调用无数据，跳过返回 null。</summary>
    private async Task<double?> GetCpuPercentAsync(CancellationToken ct)
    {
        try
        {
            _cpuCounter ??= new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _cpuCounter.NextValue(); // 首次调用返回 0，丢弃
            await Task.Delay(500, ct);
            var v = _cpuCounter.NextValue();
            return v < 0 ? null : v;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "读取 CPU 计数器失败");
            return null;
        }
    }

    public override void Dispose()
    {
        _cpuCounter?.Dispose();
        base.Dispose();
    }

    private static double GetMemoryUsedPercent()
    {
        var ms = new MEMORY_STATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORY_STATUSEX>() };
        if (!GlobalMemoryStatusEx(ref ms))
            return 0;
        return 100.0 * (ms.ullTotalPhys - ms.ullAvailPhys) / ms.ullTotalPhys;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_STATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORY_STATUSEX lpBuffer);
}
