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
