using Microsoft.Extensions.Logging.Abstractions;
using NightWatch.Heartbeat;
using NightWatch.Models;
using NightWatch.Notification;
using Xunit;

namespace NightWatch.Tests;

public class HttpHeartbeatMonitorTests
{
    [Fact]
    public async Task StaleEntry_AlertsTimeout()
    {
        var registry = new HeartbeatRegistry();
        registry.Seed("MyApp/userA", DateTime.UtcNow.AddMinutes(-10), "10.0.0.8", 3);
        var notifier = new CollectingNotifier();
        var monitor = new Probe(registry, new HttpHeartbeatOptions
        {
            StaleMinutes = 5,
            RetentionMinutes = 0,
            CooldownMinutes = 10
        }, notifier);

        await monitor.CheckOnce();

        var alert = Assert.Single(notifier.Messages);
        Assert.Contains("心跳超时", alert.Body);
        Assert.Contains("MyApp/userA", alert.Body);
        Assert.True(alert.IsWarning);
    }

    [Fact]
    public async Task FreshEntry_DoesNotAlert()
    {
        var registry = new HeartbeatRegistry();
        registry.Touch("MyApp", "userA", "127.0.0.1");
        var notifier = new CollectingNotifier();
        var monitor = new Probe(registry, new HttpHeartbeatOptions
        {
            StaleMinutes = 5,
            RetentionMinutes = 0
        }, notifier);

        await monitor.CheckOnce();

        Assert.Empty(notifier.Messages);
    }

    [Fact]
    public async Task ExpectedNeverReported_AlertsMissing()
    {
        var registry = new HeartbeatRegistry();
        var notifier = new CollectingNotifier();
        var monitor = new Probe(registry, new HttpHeartbeatOptions
        {
            StaleMinutes = 5,
            RetentionMinutes = 60,
            Expected = ["MyApp/userA"]
        }, notifier);

        await monitor.CheckOnce();

        var alert = Assert.Single(notifier.Messages);
        Assert.Contains("必达心跳缺失", alert.Body);
        Assert.Contains("从未上报过心跳", alert.Body);
        Assert.Contains("MyApp/userA", alert.Body);
    }

    /// <summary>
    /// 动态键被 RemoveStale 清理后必须同步从 _everSeen 淘汰，
    /// 否则常驻服务里「曾上报过」集合只增不删会无界增长。
    /// </summary>
    [Fact]
    public async Task DynamicKeys_AreEvictedFromEverSeen_SoMemoryStaysBounded()
    {
        var registry = new HeartbeatRegistry();
        var notifier = new CollectingNotifier();
        var monitor = new Probe(registry, new HttpHeartbeatOptions
        {
            StaleMinutes = 5,
            RetentionMinutes = 1,
            CooldownMinutes = 10
        }, notifier);

        var maxTracked = 0;
        for (var round = 0; round < 5; round++)
        {
            for (var i = 0; i < 20; i++)
                registry.Seed($"product{round}-{i}/user", DateTime.UtcNow, null, 1);
            await monitor.CheckOnce();
            maxTracked = Math.Max(maxTracked, monitor.TrackedKeyCount);

            // 让本轮键超过保留期：下一轮 RemoveStale 会清理它们
            for (var i = 0; i < 20; i++)
                registry.Seed($"product{round}-{i}/user", DateTime.UtcNow.AddMinutes(-10), null, 1);
            await monitor.CheckOnce();
        }

        Assert.Equal(0, monitor.TrackedKeyCount);
        Assert.True(maxTracked <= 20, $"曾同时记住 {maxTracked} 个键，超过本轮 20 个（疑似只增不删）");
    }

    /// <summary>
    /// Expected 键数量由配置决定（有界）：保留「曾上报过」标记，
    /// 才能把「从未上报」和「超过保留期未再上报」区分开，恢复原有诊断质量。
    /// </summary>
    [Fact]
    public async Task ExpectedKey_KeepsEverSeenMarker_ForRetentionMissDiagnostic()
    {
        var registry = new HeartbeatRegistry();
        var notifier = new CollectingNotifier();
        var monitor = new Probe(registry, new HttpHeartbeatOptions
        {
            StaleMinutes = 5,
            RetentionMinutes = 1,
            CooldownMinutes = 10,
            Expected = ["MyApp/userA"]
        }, notifier);

        registry.Seed("MyApp/userA", DateTime.UtcNow, "10.0.0.8", 1);
        await monitor.CheckOnce(); // 正常运行一次，标记「曾上报过」
        Assert.Equal(1, monitor.TrackedKeyCount);

        registry.Seed("MyApp/userA", DateTime.UtcNow.AddMinutes(-10), "10.0.0.8", 1);
        await monitor.CheckOnce(); // 超过保留期被清理，但 Expected 键保留标记

        var alert = Assert.Single(notifier.Messages);
        Assert.Contains("超过保留期未再上报", alert.Body, StringComparison.Ordinal);
        Assert.Equal(1, monitor.TrackedKeyCount);
    }

    private sealed class Probe : HttpHeartbeatMonitor
    {
        public Probe(HeartbeatRegistry registry, HttpHeartbeatOptions options, IEventNotifier notifier)
            : base(registry, options, new WatchDogOptions(), notifier, NullLogger<HttpHeartbeatMonitor>.Instance)
        {
        }

        public Task CheckOnce() => CheckAllAsync(CancellationToken.None);
    }

    private sealed class CollectingNotifier : IEventNotifier
    {
        public List<AlertMessage> Messages { get; } = new();

        public Task SendAsync(AlertMessage alert, CancellationToken ct = default)
        {
            Messages.Add(alert);
            return Task.CompletedTask;
        }
    }
}
