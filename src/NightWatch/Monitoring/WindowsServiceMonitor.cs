using NightWatch.Notification;
using System.ServiceProcess;
using NightWatch.Models;

namespace NightWatch.Monitoring;

/// <summary>
/// Windows 服务监控器：目标服务停止时自动启动（需管理员权限）。
/// </summary>
public class WindowsServiceMonitor : PollingMonitorBase
{
    private readonly List<ServiceRule> _rules;

    public WindowsServiceMonitor(WatchDogOptions options, IEventNotifier notifier, ILogger<WindowsServiceMonitor> logger)
        : base(notifier, logger)
    {
        _rules = options.Services;
    }

    protected override string MonitorName => "服务监控";
    protected override int IntervalSeconds =>
        _rules.Count > 0 ? _rules.Min(r => Math.Max(5, r.IntervalSeconds)) : 60;

    protected override async Task CheckAllAsync(CancellationToken ct)
    {
        foreach (var rule in _rules)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsRuleDue(rule.Name, rule.IntervalSeconds))
                continue;

            var result = await Task.Run(() => CheckRule(rule), ct);
            await ReportAsync(rule.Name, result, ct, Minutes(rule.CooldownMinutes));
        }
    }

    private MonitorResult CheckRule(ServiceRule rule)
    {
        try
        {
            using var sc = new ServiceController(rule.ServiceName);
            var status = sc.Status;

            if (status == ServiceControllerStatus.Running)
                return MonitorResult.Healthy($"服务 {rule.ServiceName} 运行中");

            if (rule.AutoRestart && status is ServiceControllerStatus.Stopped or ServiceControllerStatus.Paused)
            {
                Logger.LogInformation("尝试启动服务 {Service}（当前状态 {Status}）", rule.ServiceName, status);
                if (status == ServiceControllerStatus.Paused)
                    sc.Continue();
                else
                    sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                return MonitorResult.Unhealthy($"服务 {rule.ServiceName} 曾处于 {status}，已自动启动");
            }

            return MonitorResult.Unhealthy($"服务 {rule.ServiceName} 状态异常: {status}（AutoRestart=false 或状态不支持自动启动）");
        }
        catch (Exception ex) when (ex is System.TimeoutException or System.ServiceProcess.TimeoutException)
        {
            return MonitorResult.Unhealthy($"服务 {rule.ServiceName} 在 30 秒内未进入 Running");
        }
        catch (Exception ex)
        {
            return MonitorResult.Unhealthy($"检查服务 {rule.ServiceName} 失败: {ex.Message}");
        }
    }
}
