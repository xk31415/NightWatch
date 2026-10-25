using NightWatch.Monitoring;
using NightWatch.Notification;
using System.Diagnostics;
using System.IO;
using NightWatch.Models;

namespace NightWatch.Heartbeat;

/// <summary>
/// 文件心跳监控器：被守护程序周期性 touch 一个心跳文件，
/// 本监控器检查其最后写入时间，超时视为失联，可告警并拉起目标进程。
/// </summary>
public class FileHeartbeatMonitor : PollingMonitorBase
{
    private readonly List<HeartbeatRule> _rules;
    private readonly ProcessLaunchCoordinator _launchGuard;
    private readonly RestartBackoff _backoff = new();

    public FileHeartbeatMonitor(WatchDogOptions options, ProcessLaunchCoordinator launchGuard,
        IEventNotifier notifier, ILogger<FileHeartbeatMonitor> logger)
        : base(notifier, logger)
    {
        _rules = options.FileHeartbeats;
        _launchGuard = launchGuard;
    }

    protected override string MonitorName => "文件心跳";
    protected override int IntervalSeconds =>
        _rules.Count > 0 ? _rules.Min(r => Math.Max(5, r.IntervalSeconds)) : 60;

    protected override async Task CheckAllAsync(CancellationToken ct)
    {
        foreach (var rule in _rules)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsRuleDue(rule.Name, rule.IntervalSeconds))
                continue;

            var result = CheckRule(rule, ct);
            await ReportAsync(rule.Name, result, ct, Minutes(rule.CooldownMinutes));
        }
    }

    private MonitorResult CheckRule(HeartbeatRule rule, CancellationToken ct)
    {
        DateTime lastWrite;
        try
        {
            if (!File.Exists(rule.HeartbeatFile))
                return MonitorResult.Unhealthy($"心跳文件不存在: {rule.HeartbeatFile}");
            lastWrite = File.GetLastWriteTime(rule.HeartbeatFile);
        }
        catch (Exception ex)
        {
            return MonitorResult.Unhealthy($"读取心跳文件失败: {rule.HeartbeatFile}（{ex.Message}）");
        }

        var age = DateTime.Now - lastWrite;
        if (age.TotalMinutes <= rule.StaleMinutes)
        {
            _backoff.Reset(rule.Name);
            return MonitorResult.Healthy($"心跳正常（{age.TotalSeconds:F0} 秒前更新）");
        }

        var msg = $"心跳超时: {rule.HeartbeatFile} 已 {age.TotalMinutes:F0} 分钟未更新（阈值 {rule.StaleMinutes} 分钟）";

        var cleanName = string.IsNullOrWhiteSpace(rule.ProcessName)
            ? ""
            : rule.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? rule.ProcessName[..^4] : rule.ProcessName;

        if (!rule.AllowLaunch)
            return MonitorResult.Unhealthy(msg + "（安全自检已禁用自动拉起，仅告警）");
        if (string.IsNullOrWhiteSpace(rule.FilePath))
            return MonitorResult.Unhealthy(msg + "（未配置 FilePath，仅告警、不结束进程）");
        if (!_backoff.TryClaim(rule.Name, rule.StaleMinutes, out var fused))
        {
            return fused
                ? MonitorResult.Unhealthy(msg + $"，已连续重启 {RestartBackoff.MaxAttempts} 次无效，已停止自动拉起，需人工介入")
                : MonitorResult.Unhealthy(msg + "，处于重启退避中，本轮不杀进程");
        }

        ProcessImage.KillTargets(cleanName, rule.FilePath, Logger);

        if (ProcessImage.IsRunning(cleanName, rule.FilePath))
            return MonitorResult.Unhealthy(msg + "；旧进程仍在运行（结束失败），已跳过重启避免双实例");

        _launchGuard.ForceBeginLaunch(ProcessLaunchCoordinator.MakeKey(rule.FilePath, rule.ProcessName));

        try
        {
            var psi = ProcessStartInfoFactory.Create(rule.FilePath, rule.Arguments, workingDirectory: null);
            using var p = Process.Start(psi);
            _backoff.NoteAttempt(rule.Name);
            Logger.LogInformation("心跳超时后已拉起 {File}", rule.FilePath);
            return MonitorResult.Unhealthy(msg + "，已重启目标进程");
        }
        catch (Exception ex)
        {
            _backoff.NoteAttempt(rule.Name);
            Logger.LogError(ex, "心跳超时后拉起进程 {File} 失败", rule.FilePath);
            return MonitorResult.Unhealthy(msg + "，且重启失败");
        }
    }
}
