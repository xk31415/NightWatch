using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightWatch.Models;
using NightWatch.Monitoring;
using NightWatch.Notification;
using Xunit;

namespace NightWatch.Tests;

public class ScheduleGatingTests
{
    private static readonly DateTime MondayMorning = new(2026, 1, 5, 10, 0, 0); // 周一 10:00
    private static readonly DateTime MondayNoon = new(2026, 1, 5, 12, 0, 0);

    private static ScheduleRule Window(string start, string end, ScheduleMode mode = ScheduleMode.NoAlert) => new()
    {
        Mode = mode,
        Windows = [new TimeWindow { Start = start, End = end }]
    };

    [Fact]
    public void NoSchedule_ChecksWithoutSuppression()
    {
        var monitor = new ProbeMonitor(new CollectingNotifier());

        Assert.True(monitor.Enter("rule", null, MondayMorning, out var suppress));
        Assert.False(suppress);
    }

    [Fact]
    public void InsideWindow_ChecksWithoutSuppression()
    {
        var monitor = new ProbeMonitor(new CollectingNotifier());

        Assert.True(monitor.Enter("rule", Window("09:00", "18:00"), MondayMorning, out var suppress));
        Assert.False(suppress);
    }

    [Fact]
    public void OutsideWindow_NoAlert_ChecksButSuppressesNotifications()
    {
        var monitor = new ProbeMonitor(new CollectingNotifier());

        Assert.True(monitor.Enter("rule", Window("09:00", "10:00"), MondayNoon, out var suppress));
        Assert.True(suppress);
    }

    [Fact]
    public void OutsideWindow_Off_SkipsRuleEntirely()
    {
        var monitor = new ProbeMonitor(new CollectingNotifier());

        Assert.False(monitor.Enter("rule", Window("09:00", "10:00", ScheduleMode.Off), MondayNoon, out _));
    }

    /// <summary>时段外只记日志；时段恢复后如果仍异常，必须立刻推送（未被冷却吞掉）。</summary>
    [Fact]
    public async Task SuppressedAlert_DoesNotNotify_AndDoesNotConsumeCooldown()
    {
        var notifier = new CollectingNotifier();
        var monitor = new ProbeMonitor(notifier);

        await monitor.Report("rule", MonitorResult.Unhealthy("down"), suppress: true);
        Assert.Empty(notifier.Messages);

        await monitor.Report("rule", MonitorResult.Unhealthy("down"), suppress: false);
        var alert = Assert.Single(notifier.Messages);
        Assert.True(alert.IsWarning);
    }

    [Fact]
    public async Task SuppressedRecovery_DoesNotNotify()
    {
        var notifier = new CollectingNotifier();
        var monitor = new ProbeMonitor(notifier);

        await monitor.Report("rule", MonitorResult.Unhealthy("down"), suppress: false);
        Assert.Single(notifier.Messages);

        await monitor.Report("rule", MonitorResult.Healthy("up"), suppress: true);
        Assert.Single(notifier.Messages);
    }

    [Fact]
    public async Task AlertAndRecoveryInsideWindow_NotifyTwice()
    {
        var notifier = new CollectingNotifier();
        var monitor = new ProbeMonitor(notifier);

        await monitor.Report("rule", MonitorResult.Unhealthy("down"), suppress: false);
        await monitor.Report("rule", MonitorResult.Healthy("up"), suppress: false);

        Assert.Equal(2, notifier.Messages.Count);
        Assert.False(notifier.Messages[1].IsWarning);
    }

    /// <summary>生效时段内行为不变：门禁不能把正常告警也吞掉。</summary>
    [Fact]
    public async Task ActiveAllDaySchedule_StillAlerts()
    {
        var options = new WatchDogOptions
        {
            Processes =
            [
                new ProcessRule
                {
                    Name = "探针进程",
                    ProcessName = "nw_schedule_probe_missing",
                    IntervalSeconds = 3600,
                    Schedule = new ScheduleRule
                    {
                        // Start == End => 整天生效
                        Windows = [new TimeWindow { Start = "00:00", End = "00:00" }]
                    }
                }
            ]
        };
        var notifier = new CollectingNotifier();
        var monitor = new TestProcessMonitor(options, new ProcessLaunchCoordinator(), notifier,
            NullLogger<ProcessMonitor>.Instance);

        await monitor.RunOnceAsync();

        var alert = Assert.Single(notifier.Messages);
        Assert.Contains("nw_schedule_probe_missing", alert.Body);
    }

    /// <summary>时段外 + Mode=Off：整条规则跳过，不产生任何通知。</summary>
    [Fact]
    public async Task InactiveOffSchedule_SkipsCheckEntirely()
    {
        var start = DateTime.Now.AddHours(2);
        var options = new WatchDogOptions
        {
            Processes =
            [
                new ProcessRule
                {
                    Name = "探针进程",
                    ProcessName = "nw_schedule_probe_missing",
                    IntervalSeconds = 3600,
                    Schedule = new ScheduleRule
                    {
                        Mode = ScheduleMode.Off,
                        Windows = [new TimeWindow { Start = start.ToString("HH:mm"), End = start.AddHours(1).ToString("HH:mm") }]
                    }
                }
            ]
        };
        var notifier = new CollectingNotifier();
        var monitor = new TestProcessMonitor(options, new ProcessLaunchCoordinator(), notifier,
            NullLogger<ProcessMonitor>.Instance);

        await monitor.RunOnceAsync();

        Assert.Empty(notifier.Messages);
    }

    private sealed class ProbeMonitor : PollingMonitorBase
    {
        public ProbeMonitor(IEventNotifier notifier) : base(notifier, NullLogger<ProbeMonitor>.Instance)
        {
        }

        protected override string MonitorName => "探针";
        protected override int IntervalSeconds => 3600;

        protected override Task CheckAllAsync(CancellationToken ct) => Task.CompletedTask;

        public bool Enter(string key, ScheduleRule? schedule, DateTime now, out bool suppress) =>
            TryEnterScheduleAt(key, schedule, now, out suppress);

        public Task Report(string key, MonitorResult result, bool suppress) =>
            ReportAsync(key, result, CancellationToken.None, TimeSpan.FromMinutes(10), suppress);
    }

    private sealed class TestProcessMonitor : ProcessMonitor
    {
        public TestProcessMonitor(WatchDogOptions options, ProcessLaunchCoordinator guard,
            IEventNotifier notifier, ILogger<ProcessMonitor> logger)
            : base(options, guard, notifier, logger)
        {
        }

        public Task RunOnceAsync() => CheckAllAsync(CancellationToken.None);
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
