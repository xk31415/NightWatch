using Microsoft.Extensions.Logging.Abstractions;
using NightWatch.Monitoring;
using NightWatch.Notification;
using Xunit;

namespace NightWatch.Tests;

public class RecoveryNotificationTests
{
    [Fact]
    public async Task AlertThenHealthy_SendsExactlyOneRecovery()
    {
        var notifier = new CollectingNotifier();
        var monitor = new ProbeMonitor(notifier);

        await monitor.Report("rule", MonitorResult.Unhealthy("down"));
        await monitor.Report("rule", MonitorResult.Healthy("up"));
        await monitor.Report("rule", MonitorResult.Healthy("up"));

        Assert.Equal(2, notifier.Messages.Count);
        Assert.True(notifier.Messages[0].IsWarning);
        Assert.False(notifier.Messages[1].IsWarning);
        Assert.Contains("已恢复正常", notifier.Messages[1].Body);
    }

    /// <summary>
    /// 本轮异常从未推送过告警（去重/退避抑制）时，不应在恢复时打扰运维。
    /// 但状态仍要记录，避免抑制分支吞掉后续的恢复通知。
    /// </summary>
    [Fact]
    public async Task SuppressedOnlyEpisode_SendsNothing()
    {
        var notifier = new CollectingNotifier();
        var monitor = new ProbeMonitor(notifier);

        await monitor.Report("rule", MonitorResult.Unhealthy("down", notify: false));
        await monitor.Report("rule", MonitorResult.Healthy("up"));

        Assert.Empty(notifier.Messages);
    }

    [Fact]
    public async Task SuppressedChecksInsideAlertedEpisode_StillAnnounceRecoveryOnce()
    {
        var notifier = new CollectingNotifier();
        var monitor = new ProbeMonitor(notifier);

        await monitor.Report("rule", MonitorResult.Unhealthy("down"));                 // 告警
        await monitor.Report("rule", MonitorResult.Unhealthy("still down", notify: false)); // 被抑制
        await monitor.Report("rule", MonitorResult.Healthy("up"));                      // 恢复

        Assert.Equal(2, notifier.Messages.Count);
        Assert.Contains("已恢复正常", notifier.Messages[1].Body);
    }

    [Fact]
    public async Task Cooldown_SuppressesRepeatAlert_ButKeepsSingleRecovery()
    {
        var notifier = new CollectingNotifier();
        var monitor = new ProbeMonitor(notifier);
        var cooldown = TimeSpan.FromMinutes(10);

        await monitor.Report("rule", MonitorResult.Unhealthy("down"), cooldown);
        await monitor.Report("rule", MonitorResult.Unhealthy("down"), cooldown);
        await monitor.Report("rule", MonitorResult.Healthy("up"), cooldown);

        Assert.Equal(2, notifier.Messages.Count);
        Assert.True(notifier.Messages[0].IsWarning);
        Assert.False(notifier.Messages[1].IsWarning);
    }

    private sealed class ProbeMonitor : PollingMonitorBase
    {
        public ProbeMonitor(IEventNotifier notifier)
            : base(notifier, NullLogger<ProbeMonitor>.Instance)
        {
        }

        protected override string MonitorName => "探针";
        protected override int IntervalSeconds => 3600;

        protected override Task CheckAllAsync(CancellationToken ct) => Task.CompletedTask;

        public Task Report(string key, MonitorResult result, TimeSpan? cooldown = null) =>
            ReportAsync(key, result, CancellationToken.None, cooldown);
    }

    private sealed class CollectingNotifier : IEventNotifier
    {
        private readonly List<AlertMessage> _messages = new();

        public IReadOnlyList<AlertMessage> Messages
        {
            get
            {
                lock (_messages) return _messages.ToList();
            }
        }

        public Task SendAsync(AlertMessage alert, CancellationToken ct = default)
        {
            lock (_messages) _messages.Add(alert);
            return Task.CompletedTask;
        }
    }
}
