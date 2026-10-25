using NightWatch.Notification;
using System.Diagnostics;
using System.IO;
using NightWatch.Models;

namespace NightWatch.Monitoring;

/// <summary>
/// 进程守护监控器：检测目标进程是否存活，
/// 退出则自动拉起（配置了 FilePath 且允许拉起时），无响应可选强杀重启。
/// </summary>
public class ProcessMonitor : PollingMonitorBase
{
    private readonly List<ProcessRule> _rules;
    private readonly ProcessLaunchCoordinator _launchGuard;
    private readonly Dictionary<string, RestartState> _restarts = new(StringComparer.OrdinalIgnoreCase);

    public ProcessMonitor(WatchDogOptions options, ProcessLaunchCoordinator launchGuard,
        IEventNotifier notifier, ILogger<ProcessMonitor> logger)
        : base(notifier, logger)
    {
        _rules = options.Processes;
        _launchGuard = launchGuard;
    }

    protected override string MonitorName => "进程监控";
    protected override int IntervalSeconds =>
        _rules.Count > 0 ? _rules.Min(r => Math.Max(5, r.IntervalSeconds)) : 30;

    protected override async Task CheckAllAsync(CancellationToken ct)
    {
        foreach (var rule in _rules)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsRuleDue(rule.Name, rule.IntervalSeconds))
                continue;

            var result = await CheckRuleAsync(rule, ct);
            await ReportAsync(rule.Name, result, ct, Minutes(rule.CooldownMinutes));
        }
    }

    private Task<MonitorResult> CheckRuleAsync(ProcessRule rule, CancellationToken ct)
    {
        var cleanName = rule.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? rule.ProcessName[..^4]
            : rule.ProcessName;

        List<Process> procs;
        try
        {
            procs = ProcessImage.FindTargets(cleanName, rule.FilePath);
        }
        catch (Exception ex)
        {
            return Task.FromResult(MonitorResult.Unhealthy($"无法枚举进程 {cleanName}: {ex.Message}"));
        }

        try
        {
            if (procs.Count == 0)
            {
                return Task.FromResult(TryStart(rule) switch
                {
                    StartOutcome.Started => MonitorResult.Unhealthy($"进程 {cleanName} 不存在，已自动拉起"),
                    StartOutcome.Deduped => MonitorResult.Unhealthy(
                        $"进程 {cleanName} 不存在，但去重窗口内刚触发过启动（防重复拉起），本轮跳过",
                        notify: false),
                    StartOutcome.NotConfigured => MonitorResult.Unhealthy(
                        $"进程 {cleanName} 不存在（未配置 FilePath，仅告警）"),
                    StartOutcome.Disabled => MonitorResult.Unhealthy(
                        $"进程 {cleanName} 不存在（安全自检已禁用自动拉起，仅告警）"),
                    StartOutcome.Fused => MonitorResult.Unhealthy(
                        $"进程 {cleanName} 不存在，自动拉起已熔断（连续失败），需人工介入"),
                    StartOutcome.BackingOff => MonitorResult.Unhealthy(
                        $"进程 {cleanName} 不存在，处于重启退避中，本轮只告警",
                        notify: false),
                    _ => MonitorResult.Unhealthy($"进程 {cleanName} 不存在，且自动拉起失败")
                });
            }

            ResetRestart(rule.Name);

            if (rule.RestartIfNotResponding)
            {
                var hung = procs.Where(p =>
                {
                    try { return p.Responding == false && p.MainWindowHandle != IntPtr.Zero; }
                    catch { return false; }
                }).ToList();

                if (hung.Count > 0)
                {
                    foreach (var p in hung)
                    {
                        Logger.LogWarning("进程 {Name} (PID {Pid}) 无响应，强制结束", cleanName, p.Id);
                        try
                        {
                            p.Kill(entireProcessTree: true);
                            p.WaitForExit(5000);
                        }
                        catch { /* 忽略 */ }
                    }

                    TryStart(rule);
                    return Task.FromResult(MonitorResult.Unhealthy($"进程 {cleanName} 无响应，已强杀并重启 {hung.Count} 个实例"));
                }
            }

            var label = string.IsNullOrWhiteSpace(rule.FilePath) ? cleanName : rule.FilePath;
            return Task.FromResult(MonitorResult.Healthy($"{label} 运行中 ({procs.Count} 实例)"));
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private enum StartOutcome { Started, Deduped, Failed, NotConfigured, Disabled, Fused, BackingOff }

    private StartOutcome TryStart(ProcessRule rule)
    {
        if (!rule.AllowLaunch)
            return StartOutcome.Disabled;
        if (string.IsNullOrWhiteSpace(rule.FilePath))
            return StartOutcome.NotConfigured;
        if (!CanLaunch(rule.Name, Math.Max(1, rule.IntervalSeconds / 60.0)))
            return CurrentRestart(rule.Name).Failures >= RestartState.MaxFailures
                ? StartOutcome.Fused
                : StartOutcome.BackingOff;

        if (!_launchGuard.TryBeginLaunch(ProcessLaunchCoordinator.MakeKey(rule.FilePath, rule.ProcessName)))
            return StartOutcome.Deduped;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = rule.FilePath,
                Arguments = rule.Arguments ?? "",
                UseShellExecute = false,
                WorkingDirectory = rule.WorkingDirectory ?? Path.GetDirectoryName(rule.FilePath) ?? ""
            };
            using var p = Process.Start(psi);
            Logger.LogInformation("已启动进程 {File} (PID {Pid})", rule.FilePath, p?.Id);
            NoteLaunch(rule.Name);
            return p is not null ? StartOutcome.Started : StartOutcome.Failed;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "拉起进程 {File} 失败", rule.FilePath);
            NoteLaunch(rule.Name);
            return StartOutcome.Failed;
        }
    }

    private RestartState CurrentRestart(string key) =>
        _restarts.TryGetValue(key, out var s) ? s : new RestartState();

    private bool CanLaunch(string key, double staleMinutes)
    {
        if (!_restarts.TryGetValue(key, out var state))
            return true;
        if (state.Failures >= RestartState.MaxFailures)
            return false;
        var delay = TimeSpan.FromMinutes(Math.Max(1, staleMinutes) * (1 << Math.Min(state.Failures, 2)));
        return DateTime.UtcNow - state.LastLaunchUtc >= delay;
    }

    private void NoteLaunch(string key)
    {
        if (!_restarts.TryGetValue(key, out var state))
            state = _restarts[key] = new RestartState();
        state.LastLaunchUtc = DateTime.UtcNow;
        state.Failures++;
    }

    private void ResetRestart(string key) => _restarts.Remove(key);

    private sealed class RestartState
    {
        public const int MaxFailures = 5;
        public DateTime LastLaunchUtc;
        public int Failures;
    }
}
