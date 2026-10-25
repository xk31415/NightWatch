using NightWatch.Monitoring;
using NightWatch.Models;
using NightWatch.Notification;

namespace NightWatch.Heartbeat;

/// <summary>
/// HTTP 心跳监控器：遍历心跳注册表，检查每个 "产品/用户" 键的
/// 最近上报时间是否超过阈值；配置了 Expected 必达清单时，
/// 从未上报的键也会告警。支持按产品覆盖超时阈值。
/// </summary>
public class HttpHeartbeatMonitor : PollingMonitorBase
{
    private readonly HeartbeatRegistry _registry;
    private readonly HttpHeartbeatOptions _options;
    private readonly HashSet<string> _everSeen = new(StringComparer.OrdinalIgnoreCase);

    public HttpHeartbeatMonitor(HeartbeatRegistry registry, HttpHeartbeatOptions options,
        IEventNotifier notifier, ILogger<HttpHeartbeatMonitor> logger)
        : base(notifier, logger)
    {
        _registry = registry;
        _options = options;
    }

    protected override string MonitorName => "HTTP心跳";
    protected override int IntervalSeconds => Math.Max(10, _options.IntervalSeconds);

    protected override async Task CheckAllAsync(CancellationToken ct)
    {
        // 动态客户端长期不上报后退出监控，避免永久重复告警；Expected 必达键随后仍按缺失处理。
        foreach (var key in _registry.RemoveStale(TimeSpan.FromMinutes(_options.RetentionMinutes)))
        {
            ForgetRule(key);
            Logger.LogInformation("[{Monitor}] 心跳键 {Rule} 已超过 {Retention} 分钟保留期，停止监控",
                MonitorName, key, _options.RetentionMinutes);
        }

        var snapshot = _registry.Snapshot();
        var now = DateTime.UtcNow;

        // 1. 已上报过的键：检查是否超时
        foreach (var kv in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            _everSeen.Add(kv.Key);
            var staleMinutes = StaleFor(kv.Key);
            var ageMinutes = (now - kv.Value.LastSeenUtc).TotalMinutes;

            var result = ageMinutes > staleMinutes
                ? MonitorResult.Unhealthy($"心跳超时: {kv.Key} 已 {ageMinutes:F1} 分钟未上报（阈值 {staleMinutes} 分钟），最近来源 {kv.Value.LastIp ?? "未知"}")
                : MonitorResult.Healthy($"{kv.Key} 心跳正常（{ageMinutes * 60:F0} 秒前上报）");

            await ReportAsync(kv.Key, result, ct, Minutes(_options.CooldownMinutes));
        }

        // 2. 必达清单：从未上报过的键
        foreach (var key in _options.Expected)
        {
            ct.ThrowIfCancellationRequested();
            if (!_registry.IsKnown(key))
            {
                var result = MonitorResult.Unhealthy(_everSeen.Contains(key)
                    ? $"必达心跳缺失: {key} 超过保留期未再上报"
                    : $"必达心跳缺失: {key} 从未上报过心跳");
                await ReportAsync(key, result, ct, Minutes(_options.CooldownMinutes));
            }
        }
    }

    /// <summary>取某键的超时阈值：优先用产品级覆盖，否则用全局默认值。</summary>
    private double StaleFor(string key)
    {
        var product = key.Split('/')[0];
        return _options.Products
                   .FirstOrDefault(p => p.Name.Equals(product, StringComparison.OrdinalIgnoreCase))
                   ?.StaleMinutes
               ?? _options.StaleMinutes;
    }
}
