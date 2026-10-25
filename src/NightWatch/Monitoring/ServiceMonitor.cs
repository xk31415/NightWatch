using NightWatch.Notification;
using NightWatch.Platform;
using NightWatch.Models;

namespace NightWatch.Monitoring;

/// <summary>
/// 服务守护：目标服务停止时自动启动。Windows 管理 Windows 服务，Linux 管理 systemd unit，
/// 经 <see cref="IServiceControl"/> 抽象切换（需 root / 管理员权限）。
/// </summary>
public class ServiceMonitor : PollingMonitorBase
{
    private readonly List<ServiceRule> _rules;
    private readonly ScheduleRule? _defaultSchedule;
    private readonly IServiceControl _serviceControl;

    public ServiceMonitor(WatchDogOptions options, IEventNotifier notifier, IServiceControl serviceControl,
        ILogger<ServiceMonitor> logger)
        : base(notifier, logger)
    {
        _rules = options.Services;
        _defaultSchedule = options.Schedule;
        _serviceControl = serviceControl;
    }

    protected override string MonitorName => "服务监控";
    protected override int IntervalSeconds =>
        _rules.Count > 0 ? _rules.Min(r => Math.Max(5, r.IntervalSeconds)) : 60;

    protected override async Task CheckAllAsync(CancellationToken ct)
    {
        foreach (var rule in _rules)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryEnterSchedule(rule.Name, Schedule.Resolve(rule.Schedule, _defaultSchedule), out var suppress))
                continue;
            if (!IsRuleDue(rule.Name, rule.IntervalSeconds))
                continue;

            var result = await Task.Run(() => CheckRule(rule), ct);
            await ReportAsync(rule.Name, result, ct, Minutes(rule.CooldownMinutes), suppress);
        }
    }

    private MonitorResult CheckRule(ServiceRule rule)
    {
        try
        {
            var status = _serviceControl.GetStatus(rule.ServiceName);

            if (status == ServiceUnitStatus.Running)
                return MonitorResult.Healthy($"服务 {rule.ServiceName} 运行中");

            // 单元不存在是配置问题，不能落到“状态异常”里误导成 AutoRestart 开关问题，也不该反复尝试启动
            if (status == ServiceUnitStatus.NotFound)
                return MonitorResult.Unhealthy(
                    $"服务 {rule.ServiceName} 不存在：Windows 填 sc query 的服务名，Linux 填 systemd unit 名（如 myapp.service）");

            if (CanAttemptRestart(rule, status))
            {
                Logger.LogInformation("尝试启动服务 {Service}（当前状态 {Status}）", rule.ServiceName, status);
                if (status == ServiceUnitStatus.Paused)
                    _serviceControl.Continue(rule.ServiceName);
                else
                    _serviceControl.Start(rule.ServiceName);
                _serviceControl.WaitForRunning(rule.ServiceName, TimeSpan.FromSeconds(30));
                return MonitorResult.Unhealthy($"服务 {rule.ServiceName} 曾处于 {status}，已自动启动");
            }

            if (rule.AutoRestart && status is ServiceUnitStatus.Stopped or ServiceUnitStatus.Paused)
                return MonitorResult.Unhealthy($"服务 {rule.ServiceName} 处于 {status}（安全自检已禁用自动拉起，仅告警）");

            return MonitorResult.Unhealthy($"服务 {rule.ServiceName} 状态异常: {status}（AutoRestart=false 或状态不支持自动启动）");
        }
        catch (TimeoutException)
        {
            return MonitorResult.Unhealthy($"服务 {rule.ServiceName} 在 30 秒内未进入运行状态");
        }
        catch (Exception ex)
        {
            return MonitorResult.Unhealthy($"检查服务 {rule.ServiceName} 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 与进程守护、文件心跳相同：AutoRestart 之外还要过 AllowLaunch。
    /// 安全自检关闭拉起时只告警，不执行 Start / Continue。
    /// </summary>
    internal static bool CanAttemptRestart(ServiceRule rule, ServiceUnitStatus status) =>
        rule.AutoRestart
        && rule.AllowLaunch
        && status is ServiceUnitStatus.Stopped or ServiceUnitStatus.Paused;
}
