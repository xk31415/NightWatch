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
    private readonly RestartBackoff _backoff = new();

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
                        $"进程 {cleanName} 不存在，自动拉起已熔断（已连续尝试 {RestartBackoff.MaxAttempts} 次），需人工介入"),
                    StartOutcome.BackingOff => MonitorResult.Unhealthy(
                        $"进程 {cleanName} 不存在，处于重启退避中，本轮只告警",
                        notify: false),
                    _ => MonitorResult.Unhealthy($"进程 {cleanName} 不存在，且自动拉起失败")
                });
            }

            if (rule.RestartIfNotResponding)
            {
                var hung = procs.Where(p =>
                {
                    try { return p.Responding == false && p.MainWindowHandle != IntPtr.Zero; }
                    catch { return false; }
                }).ToList();

                if (hung.Count > 0)
                    return Task.FromResult(RestartHung(rule, hung, cleanName));
            }

            _backoff.Reset(rule.Name);

            var label = string.IsNullOrWhiteSpace(rule.FilePath) ? cleanName : rule.FilePath;
            return Task.FromResult(MonitorResult.Healthy($"{label} 运行中 ({procs.Count} 实例)"));
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    /// <summary>
    /// 未允许拉起或没有可执行路径时，强杀只能把目标留在“已死且不会拉起”的状态，
    /// 因此必须在结束进程之前拦下。返回 null 表示可以继续强杀重启。
    /// </summary>
    internal static string? HungRestartBlockReason(ProcessRule rule)
    {
        if (!rule.AllowLaunch)
            return "（已禁用自动拉起，仅告警、不结束进程）";
        if (string.IsNullOrWhiteSpace(rule.FilePath))
            return "（未配置 FilePath，仅告警、不结束进程）";
        return null;
    }

    /// <summary>
    /// 强杀无响应进程并重启。结果文案只在真正拉起成功时才说“已重启”，
    /// 避免退避熔断或去重挡住拉起时谎报成功。
    /// </summary>
    private MonitorResult RestartHung(ProcessRule rule, List<Process> hung, string cleanName)
    {
        if (!_backoff.TryClaim(rule.Name, BackoffBaseMinutes(rule), out var fused))
        {
            return fused
                ? MonitorResult.Unhealthy($"进程 {cleanName} 无响应，自动拉起已熔断（已连续尝试 {RestartBackoff.MaxAttempts} 次），需人工介入；未结束进程")
                : MonitorResult.Unhealthy($"进程 {cleanName} 无响应，处于重启退避中，本轮只告警", notify: false);
        }

        if (!_launchGuard.TryBeginLaunch(ProcessLaunchCoordinator.MakeKey(rule.FilePath, rule.ProcessName)))
        {
            return MonitorResult.Unhealthy(
                $"进程 {cleanName} 无响应，但去重窗口内刚触发过启动（防重复拉起），本轮跳过", notify: false);
        }

        var stillRunning = 0;
        foreach (var p in hung)
        {
            Logger.LogWarning("进程 {Name} (PID {Pid}) 无响应，强制结束", cleanName, p.Id);
            try
            {
                p.Kill(entireProcessTree: true);
                if (!p.WaitForExit(5000))
                    stillRunning++;
            }
            catch
            {
                // 进程可能在结束前已自行退出
            }
        }

        // 旧实例没死就再拉一个，等于制造双实例；宁可放弃本轮重启。
        if (stillRunning > 0)
        {
            return MonitorResult.Unhealthy(
                $"进程 {cleanName} 无响应，{stillRunning} 个实例在 5 秒内未退出，已跳过重启以避免双实例");
        }

        return StartProcess(rule) switch
        {
            StartOutcome.Started => MonitorResult.Unhealthy(
                $"进程 {cleanName} 无响应，已强杀并重启 {hung.Count} 个实例"),
            _ => MonitorResult.Unhealthy($"进程 {cleanName} 无响应，已强杀但拉起失败，需人工介入")
        };
    }

    private enum StartOutcome { Started, Deduped, Failed, NotConfigured, Disabled, Fused, BackingOff }

    private StartOutcome TryStart(ProcessRule rule)
    {
        if (!rule.AllowLaunch)
            return StartOutcome.Disabled;
        if (string.IsNullOrWhiteSpace(rule.FilePath))
            return StartOutcome.NotConfigured;
        if (!_backoff.TryClaim(rule.Name, BackoffBaseMinutes(rule), out var fused))
            return fused ? StartOutcome.Fused : StartOutcome.BackingOff;

        if (!_launchGuard.TryBeginLaunch(ProcessLaunchCoordinator.MakeKey(rule.FilePath, rule.ProcessName)))
            return StartOutcome.Deduped;

        return StartProcess(rule);
    }

    private StartOutcome StartProcess(ProcessRule rule)
    {
        try
        {
            var psi = ProcessStartInfoFactory.Create(rule.FilePath!, rule.Arguments, rule.WorkingDirectory);
            using var p = Process.Start(psi);
            Logger.LogInformation("已启动进程 {File} (PID {Pid})", rule.FilePath, p?.Id);
            _backoff.NoteAttempt(rule.Name);
            return p is not null ? StartOutcome.Started : StartOutcome.Failed;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "拉起进程 {File} 失败", rule.FilePath);
            _backoff.NoteAttempt(rule.Name);
            return StartOutcome.Failed;
        }
    }

    /// <summary>退避基准：以规则检查间隔（分钟）为基准，至少 1 分钟。</summary>
    private static double BackoffBaseMinutes(ProcessRule rule) => Math.Max(1, rule.IntervalSeconds / 60.0);
}
