using NightWatch.Models;
using NightWatch.Notification;

namespace NightWatch.Monitoring;

/// <summary>
/// 轮询型监控器基类：按固定间隔循环执行 CheckAsync，
/// 异常结果记日志并推送通知，且按规则键做冷却节流防止告警轰炸。
/// 支持按规则的监控/告警时段：时段外可只静默告警（继续监控与拉起），或完全停止检查。
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

    /// <summary>每条规则上一次的时段状态，用于只在切换时写一条日志。</summary>
    private readonly Dictionary<string, bool> _windowActive = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// 判断规则当前是否应执行检查。
    /// 返回 false 表示时段外且 Mode=Off，调用方应整条跳过（不检查、不告警、不拉起）。
    /// 返回 true 且 <paramref name="suppressNotifications"/> = true 表示时段外但保留监控：
    /// 照常检查与自动拉起，只是不推送外部通知。
    /// </summary>
    protected bool TryEnterSchedule(string ruleKey, ScheduleRule? schedule, out bool suppressNotifications) =>
        TryEnterScheduleAt(ruleKey, schedule, DateTime.Now, out suppressNotifications);

    /// <summary>可注入当前时间，供测试固定时刻。</summary>
    internal bool TryEnterScheduleAt(string ruleKey, ScheduleRule? schedule, DateTime now, out bool suppressNotifications)
    {
        suppressNotifications = false;
        if (schedule is null || schedule.Windows.Count == 0)
        {
            _windowActive.Remove(ruleKey);
            return true;
        }

        var active = Schedule.IsActive(schedule, now, out var reason);
        var first = !_windowActive.TryGetValue(ruleKey, out var wasActive);
        if (first || wasActive != active)
        {
            _windowActive[ruleKey] = active;
            Logger.LogInformation("[{Monitor}] {Rule} 进入{Phase}：{Window}（{Reason}）",
                MonitorName, ruleKey, active ? "监控时段" : "非监控时段", Schedule.Describe(schedule), reason);
        }

        if (active)
            return true;

        // 时段外：Off 完全跳过；NoAlert 继续检查与拉起，只静默通知
        if (schedule.Mode == ScheduleMode.Off)
            return false;

        suppressNotifications = true;
        return true;
    }

    /// <summary>移除动态规则的全部状态（例如 HTTP 心跳键超过保留期被清理）。</summary>
    protected void ForgetRule(string ruleKey)
    {
        _lastAlertUtc.Remove(ruleKey);
        _unhealthyRules.Remove(ruleKey);
        _alertedRules.Remove(ruleKey);
        _nextCheckUtc.Remove(ruleKey);
        _windowActive.Remove(ruleKey);
    }

    /// <summary>
    /// 上报某条规则的检查结果：异常按冷却期推送；从异常恢复到健康时推送一次恢复通知。
    /// 初次启动即为健康的规则不会推送。
    /// 恢复通知只在“本轮异常期间确实推送过告警”时发送：被去重/退避抑制、从未告警过的
    /// 短暂异常不打扰运维，但状态仍然记录，避免抑制分支把恢复通知吞掉。
    /// 注意：告警只有在通知器「确实接受」后才占用冷却 / 记为已告警（队列满被拒时下轮重试），
    /// 否则会出现「漏告警 + 恢复时发一条孤立的已恢复正常」。
    /// </summary>
    /// <param name="cooldown">该规则的告警冷却时间；不传则用监控器默认值。</param>
    /// <param name="suppressNotifications">
    /// true 时只记日志、不推送（时段外 NoAlert）。此时也不占用告警冷却：时段恢复后若仍异常会立即推送。
    /// </param>
    protected async Task ReportAsync(string ruleKey, MonitorResult result, CancellationToken ct,
        TimeSpan? cooldown = null, bool suppressNotifications = false)
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
            if (suppressNotifications)
            {
                Logger.LogInformation("[{Monitor}] {Rule}: 时段外，恢复通知已抑制", MonitorName, ruleKey);
                return;
            }

            if (!await TryNotifyAsync(new AlertMessage
            {
                Title = $"{MonitorName} / {ruleKey}",
                Body = $"已恢复正常。{result.Message}",
                IsWarning = false
            }, ct))
            {
                Logger.LogWarning("[{Monitor}] {Rule}: 通知队列已满，恢复通知未投递（告警本身不受影响）",
                    MonitorName, ruleKey);
            }

            return;
        }

        Logger.LogWarning("[{Monitor}] {Rule}: {Message}", MonitorName, ruleKey, result.Message);

        // 先记状态，再看是否推送：否则 Notify=false 的分支会让后续恢复无处可比。
        _unhealthyRules.Add(ruleKey);

        if (!result.Notify)
            return;

        if (suppressNotifications)
        {
            Logger.LogDebug("[{Monitor}] {Rule}: 时段外，告警通知已抑制（日志已记录）", MonitorName, ruleKey);
            return;
        }

        var now = DateTime.UtcNow;
        if (_lastAlertUtc.TryGetValue(ruleKey, out var last) && now - last < alertCooldown)
        {
            Logger.LogDebug("[{Monitor}] {Rule} 处于告警冷却期，跳过外部通知", MonitorName, ruleKey);
            return;
        }

        // 先投递、后记状态：队列满（TryNotifyAsync 返回 false）时本轮不占用冷却，
        // 下一轮检查会重试，避免「告警没发出去、规则却静默 10 分钟」。
        if (!await TryNotifyAsync(new AlertMessage
        {
            Title = $"{MonitorName} / {ruleKey}",
            Body = result.Message,
            IsWarning = true
        }, ct))
        {
            Logger.LogWarning("[{Monitor}] {Rule}: 通知队列已满，本轮告警未投递，不占用告警冷却（下轮重试）",
                MonitorName, ruleKey);
            return;
        }

        _lastAlertUtc[ruleKey] = now;
        _alertedRules.Add(ruleKey);
    }

    /// <summary>
    /// 投递并返回「是否已被接受」：队列实现通过 <see cref="IQueuedNotifier.TryEnqueue"/>
    /// 同步告知入队结果，其它实现（直发 / 测试替身）按已接受处理。
    /// </summary>
    private async Task<bool> TryNotifyAsync(AlertMessage alert, CancellationToken ct)
    {
        if (_notifier is IQueuedNotifier queued)
            return queued.TryEnqueue(alert);

        await _notifier.SendAsync(alert, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>默认冷却时间，可在监控器构造时覆盖。</summary>
    protected virtual TimeSpan AlertCooldown { get; } = TimeSpan.FromMinutes(10);

    protected static TimeSpan Minutes(int m) => TimeSpan.FromMinutes(Math.Max(1, m));
}
