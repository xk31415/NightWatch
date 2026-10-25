using NightWatch.Notification;

namespace NightWatch.Monitoring;

/// <summary>
/// 轮询型监控器基类：按固定间隔循环执行 CheckAsync，
/// 异常结果记日志并推送通知，且按规则键做冷却节流防止告警轰炸。
/// </summary>
public abstract class PollingMonitorBase : BackgroundService
{
    private readonly IEventNotifier _notifier;
    private readonly Dictionary<string, DateTime> _lastAlertUtc = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前处于异常状态的规则（与是否推送无关，用于状态机判定）。</summary>
    private readonly HashSet<string> _unhealthyRules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本轮到异常期间真正推送过告警的规则：只有它们恢复时才发恢复通知。</summary>
    private readonly HashSet<string> _alertedRules = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, DateTime> _nextCheckUtc = new(StringComparer.OrdinalIgnoreCase);

    protected ILogger Logger { get; }

    protected PollingMonitorBase(IEventNotifier notifier, ILogger logger)
    {
        _notifier = notifier;
        Logger = logger;
    }

    /// <summary>监控器名称（用于日志前缀）。</summary>
    protected abstract string MonitorName { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("[{Monitor}] 启动，间隔 {Interval}s", MonitorName, IntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(IntervalSeconds));

        // 先立即跑一轮，再进入周期循环。等待定时器也放在取消异常处理范围内，
        // 确保 Windows 服务停止时能够正常退出，而不是报“服务意外停止”。
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[{Monitor}] 检查循环发生未处理异常", MonitorName);
            }

            // 等待必须在本轮检查之外：若和 CheckAllAsync 放在同一个 try 里，检查抛异常会跳过等待，
            // while 立即重跑形成忙等（CPU 打满 + 日志刷屏）。
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Logger.LogInformation("[{Monitor}] 已停止", MonitorName);
    }

    /// <summary>监控器主检查间隔（秒）。</summary>
    protected abstract int IntervalSeconds { get; }

    /// <summary>执行一轮检查，内部自行遍历规则并调用 ReportAsync 上报。</summary>
    protected abstract Task CheckAllAsync(CancellationToken ct);

    /// <summary>判断规则是否到达自己的检查时间；首次检查立即执行。</summary>
    protected bool IsRuleDue(string ruleKey, int intervalSeconds)
    {
        var now = DateTime.UtcNow;
        // PeriodicTimer 可能比理论 tick 早极短时间返回，留 100ms 容差避免漏掉本轮。
        if (_nextCheckUtc.TryGetValue(ruleKey, out var next) && next - now > TimeSpan.FromMilliseconds(100))
            return false;

        _nextCheckUtc[ruleKey] = now.AddSeconds(Math.Max(5, intervalSeconds));
        return true;
    }

    /// <summary>移除动态规则的全部状态（例如 HTTP 心跳键超过保留期被清理）。</summary>
    protected void ForgetRule(string ruleKey)
    {
        _lastAlertUtc.Remove(ruleKey);
        _unhealthyRules.Remove(ruleKey);
        _alertedRules.Remove(ruleKey);
        _nextCheckUtc.Remove(ruleKey);
    }

    /// <summary>
    /// 上报某条规则的检查结果：异常按冷却期推送；从异常恢复到健康时推送一次恢复通知。
    /// 初次启动即为健康的规则不会推送。
    /// 恢复通知只在“本轮异常期间确实推送过告警”时发送：被去重/退避抑制、从未告警过的
    /// 短暂异常不打扰运维，但状态仍然记录，避免抑制分支把恢复通知吞掉。
    /// </summary>
    /// <param name="cooldown">该规则的告警冷却时间；不传则用监控器默认值。</param>
    protected async Task ReportAsync(string ruleKey, MonitorResult result, CancellationToken ct, TimeSpan? cooldown = null)
    {
        var alertCooldown = cooldown ?? AlertCooldown;
        if (result.IsHealthy)
        {
            // 持续健康只记 Debug，避免生产日志被每轮 OK 刷屏。
            // 从异常恢复仍记 Information，并在下方推送一次恢复通知。
            Logger.LogDebug("[{Monitor}] {Rule}: {Message}", MonitorName, ruleKey, result.Message);
            _lastAlertUtc.Remove(ruleKey);

            var wasUnhealthy = _unhealthyRules.Remove(ruleKey);
            var wasAlerted = _alertedRules.Remove(ruleKey);
            if (!wasUnhealthy || !wasAlerted || !result.Notify)
                return;

            Logger.LogInformation("[{Monitor}] {Rule}: 已恢复正常。{Message}", MonitorName, ruleKey, result.Message);
            await _notifier.SendAsync(new AlertMessage
            {
                Title = $"{MonitorName} / {ruleKey}",
                Body = $"已恢复正常。{result.Message}",
                IsWarning = false
            }, ct);
            return;
        }

        Logger.LogWarning("[{Monitor}] {Rule}: {Message}", MonitorName, ruleKey, result.Message);

        // 先记状态，再看是否推送：否则 Notify=false 的分支会让后续恢复无处可比。
        _unhealthyRules.Add(ruleKey);

        if (!result.Notify)
            return;

        var now = DateTime.UtcNow;
        if (_lastAlertUtc.TryGetValue(ruleKey, out var last) && now - last < alertCooldown)
        {
            Logger.LogDebug("[{Monitor}] {Rule} 处于告警冷却期，跳过外部通知", MonitorName, ruleKey);
            return;
        }

        _lastAlertUtc[ruleKey] = now;
        _alertedRules.Add(ruleKey);
        await _notifier.SendAsync(new AlertMessage
        {
            Title = $"{MonitorName} / {ruleKey}",
            Body = result.Message,
            IsWarning = true
        }, ct);
    }

    /// <summary>默认冷却时间，可在监控器构造时覆盖。</summary>
    protected virtual TimeSpan AlertCooldown { get; } = TimeSpan.FromMinutes(10);

    protected static TimeSpan Minutes(int m) => TimeSpan.FromMinutes(Math.Max(1, m));
}
