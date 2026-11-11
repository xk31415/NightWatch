using Microsoft.Extensions.Logging.Abstractions;
using NightWatch.Models;
using NightWatch.Monitoring;
using NightWatch.Notification;
using NightWatch.Platform;
using Xunit;

namespace NightWatch.Tests;

/// <summary>
/// 首次告警前的持续窗口：未满窗口的抖动不推送；窗口走完才进入原来的冷却与恢复逻辑。
/// </summary>
public class SustainWindowTests
{
    private static readonly DateTime BreachAt = new(2026, 11, 11, 0, 56, 58, DateTimeKind.Utc);
    private static readonly TimeSpan TwoMinutes = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task SustainZero_AlertsImmediately_AndCooldownStillApplies()
    {
        var notifier = new CollectingNotifier();
        var monitor = new ProbeMonitor(notifier, BreachAt);
        var cooldown = TimeSpan.FromMinutes(10);

        await monitor.Report("rule", MonitorResult.Unhealthy("down"), cooldown, TimeSpan.Zero);
        var alert = Assert.Single(notifier.Messages);
        Assert.True(alert.IsWarning);
        Assert.Equal("down", alert.Body);

        monitor.Advance(TimeSpan.FromMinutes(1));
        await monitor.Report("rule", MonitorResult.Unhealthy("down"), cooldown, TimeSpan.Zero);
        Assert.Single(notifier.Messages);

        await monitor.Report("rule", MonitorResult.Healthy("up"), cooldown, TimeSpan.Zero);
        Assert.Equal(2, notifier.Messages.Count);
        Assert.False(notifier.Messages[1].IsWarning);
        Assert.Contains("已恢复正常", notifier.Messages[1].Body);
    }

    [Fact]
    public async Task StaysUnhealthyPastWindow_ThenAlerts()
    {
        var notifier = new CollectingNotifier();
        var monitor = new ProbeMonitor(notifier, BreachAt);

        await monitor.Report("rule", MonitorResult.Unhealthy("memory 97.7%"), sustain: TwoMinutes);
        Assert.Empty(notifier.Messages);

        monitor.Advance(TwoMinutes - TimeSpan.FromSeconds(1));
        await monitor.Report("rule", MonitorResult.Unhealthy("memory 97.7%"), sustain: TwoMinutes);
        Assert.Empty(notifier.Messages);

        monitor.Advance(TimeSpan.FromSeconds(1));
        await monitor.Report("rule", MonitorResult.Unhealthy("memory 97.7%"), sustain: TwoMinutes);

        var alert = Assert.Single(notifier.Messages);
        Assert.True(alert.IsWarning);
        Assert.Contains("97.7%", alert.Body);
    }

    [Fact]
    public async Task RecoversInsideWindow_SendsNothing()
    {
        var notifier = new CollectingNotifier();
        var monitor = new ProbeMonitor(notifier, BreachAt);

        await monitor.Report("rule", MonitorResult.Unhealthy("memory 97.7%"), sustain: TwoMinutes);
        monitor.Advance(TimeSpan.FromMinutes(1));
        await monitor.Report("rule", MonitorResult.Healthy("memory 80%"), sustain: TwoMinutes);

        Assert.Empty(notifier.Messages);
    }

    [Fact]
    public async Task AfterInsideWindowRecovery_SustainedFailureStillAlerts()
    {
        var notifier = new CollectingNotifier();
        var monitor = new ProbeMonitor(notifier, BreachAt);

        await monitor.Report("rule", MonitorResult.Unhealthy("blip"), sustain: TwoMinutes);
        monitor.Advance(TimeSpan.FromMinutes(1));
        await monitor.Report("rule", MonitorResult.Healthy("recovered"), sustain: TwoMinutes);
        Assert.Empty(notifier.Messages);

        monitor.Advance(TimeSpan.FromMinutes(10));
        await monitor.Report("rule", MonitorResult.Unhealthy("real"), sustain: TwoMinutes);
        Assert.Empty(notifier.Messages);

        monitor.Advance(TwoMinutes);
        await monitor.Report("rule", MonitorResult.Unhealthy("real"), sustain: TwoMinutes);

        var alert = Assert.Single(notifier.Messages);
        Assert.True(alert.IsWarning);
        Assert.Equal("real", alert.Body);
    }

    [Fact]
    public async Task RecoveryNotice_OnlyAfterAlertWasSent()
    {
        var notifier = new CollectingNotifier();
        var monitor = new ProbeMonitor(notifier, BreachAt);

        await monitor.Report("rule", MonitorResult.Unhealthy("blip"), sustain: TwoMinutes);
        monitor.Advance(TimeSpan.FromMinutes(1));
        await monitor.Report("rule", MonitorResult.Healthy("back"), sustain: TwoMinutes);
        Assert.Empty(notifier.Messages);

        await monitor.Report("rule", MonitorResult.Unhealthy("down"), sustain: TwoMinutes);
        Assert.Empty(notifier.Messages);
        monitor.Advance(TwoMinutes);
        await monitor.Report("rule", MonitorResult.Unhealthy("down"), sustain: TwoMinutes);
        Assert.Single(notifier.Messages);
        Assert.True(notifier.Messages[0].IsWarning);

        await monitor.Report("rule", MonitorResult.Healthy("up"), sustain: TwoMinutes);
        await monitor.Report("rule", MonitorResult.Healthy("up"), sustain: TwoMinutes);

        Assert.Equal(2, notifier.Messages.Count);
        Assert.False(notifier.Messages[1].IsWarning);
        Assert.Contains("已恢复正常", notifier.Messages[1].Body);
    }

    [Fact]
    public async Task AfterSentAlert_CooldownAppliesWithoutAnotherSustainWindow()
    {
        var notifier = new CollectingNotifier();
        var monitor = new ProbeMonitor(notifier, BreachAt);
        var sustain = TimeSpan.FromMinutes(5);
        var cooldown = TimeSpan.FromMinutes(1);

        await monitor.Report("rule", MonitorResult.Unhealthy("down"), cooldown, sustain);
        monitor.Advance(sustain);
        await monitor.Report("rule", MonitorResult.Unhealthy("down"), cooldown, sustain);
        Assert.Single(notifier.Messages);

        monitor.Advance(TimeSpan.FromSeconds(30));
        await monitor.Report("rule", MonitorResult.Unhealthy("down"), cooldown, sustain);
        Assert.Single(notifier.Messages);

        monitor.Advance(TimeSpan.FromSeconds(30));
        await monitor.Report("rule", MonitorResult.Unhealthy("still down"), cooldown, sustain);
        Assert.Equal(2, notifier.Messages.Count);
        Assert.Equal("still down", notifier.Messages[1].Body);

        await monitor.Report("rule", MonitorResult.Healthy("up"), cooldown, sustain);
        Assert.Equal(3, notifier.Messages.Count);
        Assert.Contains("已恢复正常", notifier.Messages[2].Body);
    }

    [Fact]
    public async Task SustainElapsed_EnqueueRejected_SendsNoOrphanRecovery_ThenRetriesOnNextEpisode()
    {
        var notifier = new RejectingQueuedNotifier();
        var monitor = new ProbeMonitor(notifier, BreachAt);

        await monitor.Report("rule", MonitorResult.Unhealthy("down"), sustain: TwoMinutes);
        Assert.Equal(0, notifier.EnqueueAttempts);

        monitor.Advance(TwoMinutes);
        await monitor.Report("rule", MonitorResult.Unhealthy("down"), sustain: TwoMinutes);
        Assert.Equal(1, notifier.EnqueueAttempts);
        Assert.Empty(notifier.Messages);

        await monitor.Report("rule", MonitorResult.Healthy("up"), sustain: TwoMinutes);
        Assert.Empty(notifier.Messages);

        notifier.Accept = true;
        await monitor.Report("rule", MonitorResult.Unhealthy("down"), sustain: TwoMinutes);
        Assert.Empty(notifier.Messages);

        monitor.Advance(TwoMinutes);
        await monitor.Report("rule", MonitorResult.Unhealthy("down"), sustain: TwoMinutes);
        var alert = Assert.Single(notifier.Messages);
        Assert.True(alert.IsWarning);
    }

    [Fact]
    public async Task NotifyFalseSamples_StillAgeTheWindow()
    {
        var notifier = new CollectingNotifier();
        var monitor = new ProbeMonitor(notifier, BreachAt);

        await monitor.Report("rule", MonitorResult.Unhealthy("backing off", notify: false), sustain: TwoMinutes);
        monitor.Advance(TwoMinutes);
        await monitor.Report("rule", MonitorResult.Unhealthy("backing off", notify: false), sustain: TwoMinutes);
        Assert.Empty(notifier.Messages);

        await monitor.Report("rule", MonitorResult.Unhealthy("down"), sustain: TwoMinutes);
        var alert = Assert.Single(notifier.Messages);
        Assert.Equal("down", alert.Body);
    }

    [Fact]
    public async Task OtherRule_IsNotHeldByAPendingWindow()
    {
        var notifier = new CollectingNotifier();
        var monitor = new ProbeMonitor(notifier, BreachAt);

        await monitor.Report("held", MonitorResult.Unhealthy("waiting"), sustain: TwoMinutes);
        await monitor.Report("now", MonitorResult.Unhealthy("immediate"), sustain: TimeSpan.Zero);

        var alert = Assert.Single(notifier.Messages);
        Assert.Contains("now", alert.Title);
        Assert.Equal("immediate", alert.Body);
    }

    [Fact]
    public async Task QuietHours_CountTowardSustain_ButDoNotNotify()
    {
        var notifier = new CollectingNotifier();
        var monitor = new ProbeMonitor(notifier, BreachAt);

        await monitor.Report("rule", MonitorResult.Unhealthy("down"), sustain: TwoMinutes, suppress: true);
        Assert.Empty(notifier.Messages);

        monitor.Advance(TimeSpan.FromSeconds(30));
        await monitor.Report("rule", MonitorResult.Unhealthy("down"), sustain: TwoMinutes, suppress: false);
        Assert.Empty(notifier.Messages);

        monitor.Advance(TwoMinutes - TimeSpan.FromSeconds(30));
        await monitor.Report("rule", MonitorResult.Unhealthy("down"), sustain: TwoMinutes, suppress: false);
        Assert.Single(notifier.Messages);
    }

    [Fact]
    public async Task ResourceMonitor_DefaultSustain_AlertsOnFirstBreach()
    {
        var notifier = new CollectingNotifier();
        var metrics = new FakeMetrics { MemoryPercent = 97.7 };
        var monitor = new ClockedResourceMonitor(ResourceOptions(sustainMinutes: 0), notifier, metrics, BreachAt);

        await monitor.RunOnce();

        var alert = Assert.Single(notifier.Messages);
        Assert.True(alert.IsWarning);
        Assert.Contains("97.7%", alert.Body);
    }

    [Fact]
    public async Task ResourceMonitor_SustainTwoMinutes_HoldsThenAlertsThenRecovers()
    {
        var notifier = new CollectingNotifier();
        var metrics = new FakeMetrics { MemoryPercent = 97.7 };
        var monitor = new ClockedResourceMonitor(ResourceOptions(sustainMinutes: 2), notifier, metrics, BreachAt);

        await monitor.RunOnce();
        monitor.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunOnce();
        Assert.Empty(notifier.Messages);

        monitor.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunOnce();
        var alert = Assert.Single(notifier.Messages);
        Assert.Contains("内存使用率 97.7%", alert.Body);
        Assert.Contains("95%", alert.Body);

        monitor.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunOnce();
        Assert.Single(notifier.Messages);

        metrics.MemoryPercent = 40;
        await monitor.RunOnce();
        Assert.Equal(2, notifier.Messages.Count);
        Assert.Contains("已恢复正常", notifier.Messages[1].Body);
    }

    private static WatchDogOptions ResourceOptions(int sustainMinutes) => new()
    {
        Resources = new ResourceRule
        {
            IntervalSeconds = 60,
            CooldownMinutes = 30,
            SustainMinutes = sustainMinutes,
            CpuPercent = 0,
            MemoryPercent = 95
        }
    };

    private sealed class ProbeMonitor : PollingMonitorBase
    {
        private DateTime _now;

        public ProbeMonitor(IEventNotifier notifier, DateTime now)
            : base(notifier, NullLogger<ProbeMonitor>.Instance)
        {
            _now = now;
            UseUtcNow(() => _now);
        }

        public void Advance(TimeSpan by) => _now += by;

        protected override string MonitorName => "探针";
        protected override int IntervalSeconds => 3600;
        protected override Task CheckAllAsync(CancellationToken ct) => Task.CompletedTask;

        public Task Report(string key, MonitorResult result, TimeSpan? cooldown = null, TimeSpan? sustain = null,
            bool suppress = false) =>
            ReportAsync(key, result, CancellationToken.None, cooldown, suppress, sustain);
    }

    private sealed class ClockedResourceMonitor : ResourceMonitor
    {
        private DateTime _now;

        public ClockedResourceMonitor(WatchDogOptions options, IEventNotifier notifier, ISystemMetrics metrics, DateTime now)
            : base(options, notifier, metrics, NullLogger<ResourceMonitor>.Instance)
        {
            _now = now;
            UseUtcNow(() => _now);
        }

        public void Advance(TimeSpan by) => _now += by;

        public Task RunOnce() => CheckAllAsync(CancellationToken.None);
    }

    private sealed class FakeMetrics : ISystemMetrics
    {
        public double MemoryPercent { get; set; }

        public Task<double?> GetCpuPercentAsync(CancellationToken ct) => Task.FromResult<double?>(null);

        public double GetMemoryUsedPercent() => MemoryPercent;

        public void Dispose()
        {
        }
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
