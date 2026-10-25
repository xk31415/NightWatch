using NightWatch.Notification;
using System.IO;
using NightWatch.Models;

namespace NightWatch.Monitoring;

/// <summary>
/// 文件/日志监控器：检测日志停更（程序卡死常表现为日志不再滚动）
/// 与文件异常膨胀（如暴涨到超大体积）。
/// </summary>
public class FileLogMonitor : PollingMonitorBase
{
    private readonly List<FileRule> _rules;

    public FileLogMonitor(WatchDogOptions options, IEventNotifier notifier, ILogger<FileLogMonitor> logger)
        : base(notifier, logger)
    {
        _rules = options.Files;
    }

    protected override string MonitorName => "文件监控";
    protected override int IntervalSeconds =>
        _rules.Count > 0 ? _rules.Min(r => Math.Max(5, r.IntervalSeconds)) : 60;

    protected override async Task CheckAllAsync(CancellationToken ct)
    {
        foreach (var rule in _rules)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsRuleDue(rule.Name, rule.IntervalSeconds))
                continue;

            var result = CheckRule(rule);
            await ReportAsync(rule.Name, result, ct, Minutes(rule.CooldownMinutes));
        }
    }

    private MonitorResult CheckRule(FileRule rule)
    {
        var target = FindTarget(rule);
        if (target is null)
            return MonitorResult.Unhealthy($"路径不存在或无匹配文件: {rule.Path}");

        FileInfo fi;
        DateTime lastWrite;
        long length;
        try
        {
            fi = new FileInfo(target);
            if (!fi.Exists)
                return MonitorResult.Unhealthy($"目标不是文件: {target}");
            lastWrite = fi.LastWriteTime;
            length = fi.Length;
        }
        catch (Exception ex)
        {
            return MonitorResult.Unhealthy($"读取文件失败: {target}（{ex.Message}）");
        }

        var problems = new List<string>();

        if (rule.StaleMinutes > 0)
        {
            var age = DateTime.Now - lastWrite;
            if (age.TotalMinutes > rule.StaleMinutes)
                problems.Add($"已 {age.TotalMinutes:F0} 分钟未更新（阈值 {rule.StaleMinutes} 分钟）");
        }

        if (rule.MaxSizeMB > 0 && length > (long)rule.MaxSizeMB * 1024 * 1024)
            problems.Add($"文件大小 {length / 1024.0 / 1024:F1} MB 超过阈值 {rule.MaxSizeMB} MB");

        return problems.Count > 0
            ? MonitorResult.Unhealthy($"{target}: {string.Join("; ", problems)}")
            : MonitorResult.Healthy($"{target} 正常");
    }

    /// <summary>规则路径是目录时，取匹配 Pattern 的最近修改文件；是文件时直接返回。</summary>
    private static string? FindTarget(FileRule rule)
    {
        try
        {
            if (File.Exists(rule.Path))
                return rule.Path;

            if (!Directory.Exists(rule.Path))
                return null;

            var dir = new DirectoryInfo(rule.Path);
            return dir.GetFiles(rule.Pattern, SearchOption.TopDirectoryOnly)
                      .OrderByDescending(f => f.LastWriteTime)
                      .FirstOrDefault()?.FullName;
        }
        catch
        {
            return null;
        }
    }
}
