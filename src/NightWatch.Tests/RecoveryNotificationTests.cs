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

    /// <summary>
    /// 队列满拒绝入队时不能置告警冷却：拒绝后下一轮仍应重试投递；
    /// 且因为从未真正告警过，恢复时不能发「孤立的已恢复正常」。
    /// </summary>
    [Fact]
    public async Task EnqueueRejected_DoesNotConsumeCooldown_AndSendsNoOrphanRecovery()
    {
        var notifier = new RejectingQueuedNotifier();
        var monitor = new ProbeMonitor(notifier);

        await monitor.Report("rule", MonitorResult.Unhealthy("down"));  // 被拒
        await monitor.Report("rule", MonitorResult.Unhealthy("down"));  // 冷却未占用 → 再次尝试
        Assert.Equal(2, notifier.EnqueueAttempts);
        Assert.Empty(notifier.Messages);

        await monitor.Report("rule", MonitorResult.Healthy("up"));
        Assert.Empty(notifier.Messages); // 没有告警就没有恢复通知

        // 队列恢复后：告警能入队，冷却才生效；恢复时发一次恢复通知
        notifier.Accept = true;
        await monitor.Report("rule", MonitorResult.Unhealthy("down"));
        Assert.Single(notifier.Messages);
        await monitor.Report("rule", MonitorResult.Healthy("up"));
        Assert.Equal(2, notifier.Messages.Count);
        Assert.Contains("已恢复正常", notifier.Messages[1].Body);
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

    /// <summary>模拟队列满的 QueuedNotifier：TryEnqueue 返回 false 时不投递。</summary>
    private sealed class RejectingQueuedNotifier : IQueuedNotifier
    {
        private readonly List<AlertMessage> _messages = new();

        public bool Accept { get; set; }

        public int EnqueueAttempts { get; private set; }

        public IReadOnlyList<AlertMessage> Messages
        {
            get
            {
                lock (_messages) return _messages.ToList();
            }
        }

        public bool TryEnqueue(AlertMessage alert)
        {
            EnqueueAttempts++;
            if (!Accept)
                return false;

            lock (_messages) _messages.Add(alert);
            return true;
        }

        public Task SendAsync(AlertMessage alert, CancellationToken ct = default)
        {
            lock (_messages) _messages.Add(alert);
            return Task.CompletedTask;
        }
    }
}
