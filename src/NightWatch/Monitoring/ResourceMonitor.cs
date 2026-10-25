using NightWatch.Notification;
using NightWatch.Platform;
using NightWatch.Models;

namespace NightWatch.Monitoring;

/// <summary>
/// 系统资源监控器：CPU 使用率、内存使用率、磁盘剩余空间阈值告警。
/// CPU/内存采集走 <see cref="ISystemMetrics"/>（Windows: 计数器/GlobalMemoryStatusEx；Linux: /proc）。
/// </summary>
public class ResourceMonitor : PollingMonitorBase
{
    private readonly ResourceRule _rule;
    private readonly ScheduleRule? _defaultSchedule;
    private readonly ISystemMetrics _metrics;

    public ResourceMonitor(WatchDogOptions options, IEventNotifier notifier, ISystemMetrics metrics,
        ILogger<ResourceMonitor> logger)
        : base(notifier, logger)
    {
        _rule = options.Resources;
        _defaultSchedule = options.Schedule;
        _metrics = metrics;
    }

    protected override string MonitorName => "资源监控";
    protected override int IntervalSeconds => Math.Max(15, _rule.IntervalSeconds);

    protected override async Task CheckAllAsync(CancellationToken ct)
    {
        if (!TryEnterSchedule("system-resources", Schedule.Resolve(_rule.Schedule, _defaultSchedule), out var suppress))
            return;

        var problems = new List<string>();

        if (_rule.CpuPercent > 0)
        {
            var cpu = await _metrics.GetCpuPercentAsync(ct);
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
            var mem = _metrics.GetMemoryUsedPercent();
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
        await ReportAsync(key, result, ct, Minutes(_rule.CooldownMinutes), suppress);
    }

    public override void Dispose()
    {
        _metrics.Dispose();
        base.Dispose();
    }
}
