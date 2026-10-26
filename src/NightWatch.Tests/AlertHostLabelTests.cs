using Microsoft.Extensions.Logging.Abstractions;
using NightWatch.Notification;
using Xunit;

namespace NightWatch.Tests;

public class AlertHostLabelTests
{
    [Fact]
    public void ResolveHostName_DefaultsToMachineName() =>
        Assert.Equal(Environment.MachineName, HostIdentity.ResolveHostName(null));

    [Fact]
    public void ResolveHostName_TrimsConfigured() =>
        Assert.Equal("srv-1", HostIdentity.ResolveHostName("  srv-1  "));

    [Fact]
    public void DingTalkMarkdown_UsesH4Headline_AndSeparateHostIpLines()
    {
        var md = MarkdownFormatter.FormatDingTalk(new AlertMessage
        {
            Host = "XINGKONG-PC",
            HostIp = "192.168.2.127",
            Title = "通知通道自测",
            Body = "排版验证",
            IsWarning = false,
            Time = new DateTime(2026, 10, 26, 9, 2, 37)
        });

        Assert.StartsWith($"#### [{NotificationConstants.Tag}]", md);
        Assert.DoesNotContain("\n### ", md);
        Assert.Contains("**主机：XINGKONG-PC**", md);
        Assert.Contains("**IP：192.168.2.127**", md);
        Assert.True(md.IndexOf("主机：", StringComparison.Ordinal) < md.IndexOf("IP：", StringComparison.Ordinal));

        // 钉钉引用块内单个换行会折叠成空格；这里用空行拆成两个独立引用块，
        // 既不会渲染成「主机：xxxIP：yyy」，也不会共用一个竖条看起来粘连。
        Assert.Contains("**主机：XINGKONG-PC**\n\n> **IP：192.168.2.127**", md);
        Assert.DoesNotContain("PCIP", md);
        Assert.DoesNotContain("**\n>\n> **", md);
    }

    [Fact]
    public void WeComMarkdown_IncludesHostLines()
    {
        var md = MarkdownFormatter.FormatWeCom(new AlertMessage
        {
            Host = "prod-db-01",
            HostIp = "10.0.0.5",
            Title = "资源监控 / system-resources",
            Body = "内存使用率 95.6% 超过阈值 95%",
            IsWarning = true,
            Time = new DateTime(2026, 10, 26, 8, 58, 33)
        });

        Assert.Contains("主机：prod-db-01", md);
        Assert.Contains("IP：10.0.0.5", md);
        Assert.Contains("资源监控 / system-resources", md);
        Assert.DoesNotContain("###", md);
        Assert.Contains($"**[{NotificationConstants.Tag}]", md);
    }

    [Fact]
    public void HostLines_ComeBeforeTitle()
    {
        var md = MarkdownFormatter.FormatDingTalk(new AlertMessage
        {
            Host = "srv-1",
            HostIp = "10.0.0.5",
            Title = "进程监控 / myapp",
            Body = "进程退出",
            IsWarning = true
        });

        Assert.True(md.IndexOf("主机：", StringComparison.Ordinal) < md.IndexOf("进程监控 / myapp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MultiChannelNotifier_EnrichesHostBeforeSend()
    {
        var captured = new List<AlertMessage>();
        var notifier = new MultiChannelNotifier(
            [new CapturingSender(captured)],
            NullLogger<MultiChannelNotifier>.Instance,
            "edge-01");

        await notifier.SendAsync(new AlertMessage
        {
            Title = "t",
            Body = "b",
            IsWarning = true
        });

        var msg = Assert.Single(captured);
        Assert.Equal("edge-01", msg.Host);
        Assert.Null(msg.HostIp);
    }

    [Fact]
    public async Task MultiChannelNotifier_FillsIp_EvenWhenHostIsExplicit()
    {
        var captured = new List<AlertMessage>();
        var notifier = new MultiChannelNotifier(
            [new CapturingSender(captured)],
            NullLogger<MultiChannelNotifier>.Instance,
            "edge-01",
            new HostIpCache(() => "10.1.2.3"));

        await notifier.SendAsync(new AlertMessage
        {
            Title = "t",
            Body = "b",
            Host = "explicit-host"
        });

        var msg = Assert.Single(captured);
        Assert.Equal("explicit-host", msg.Host);
        Assert.Equal("10.1.2.3", msg.HostIp);
    }

    [Fact]
    public async Task MultiChannelNotifier_DoesNotOverrideExplicitIp()
    {
        var captured = new List<AlertMessage>();
        var notifier = new MultiChannelNotifier(
            [new CapturingSender(captured)],
            NullLogger<MultiChannelNotifier>.Instance,
            "edge-01",
            new HostIpCache(() => "10.1.2.3"));

        await notifier.SendAsync(new AlertMessage
        {
            Title = "t",
            Body = "b",
            HostIp = "203.0.113.9"
        });

        var msg = Assert.Single(captured);
        Assert.Equal("edge-01", msg.Host);
        Assert.Equal("203.0.113.9", msg.HostIp);
    }

    [Fact]
    public void HostIpCache_ReusesWithinTtl_AndRefreshesAfter()
    {
        var now = new DateTime(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);
        var probes = 0;
        var cache = new HostIpCache(
            () => { probes++; return $"10.0.0.{probes}"; },
            TimeSpan.FromSeconds(30),
            () => now);

        Assert.Equal("10.0.0.1", cache.Get());
        Assert.Equal("10.0.0.1", cache.Get()); // TTL 内复用，不再探测
        Assert.Equal(1, probes);

        now = now.AddSeconds(31);
        Assert.Equal("10.0.0.2", cache.Get()); // 过期后重新探测（DHCP 换 IP 能被带出来）
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task MultiChannelNotifier_ResolvesIpPerSend_NotOnlyAtConstruction()
    {
        var captured = new List<AlertMessage>();
        var now = new DateTime(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);
        var probes = 0;
        var cache = new HostIpCache(
            () => { probes++; return $"10.9.9.{probes}"; },
            TimeSpan.FromSeconds(30),
            () => now);
        var notifier = new MultiChannelNotifier(
            [new CapturingSender(captured)],
            NullLogger<MultiChannelNotifier>.Instance,
            "edge-01",
            cache);

        await notifier.SendAsync(new AlertMessage { Title = "t1", Body = "b" });
        now = now.AddMinutes(2); // 超过 TTL
        await notifier.SendAsync(new AlertMessage { Title = "t2", Body = "b" });

        Assert.Equal(2, captured.Count);
        Assert.Equal("10.9.9.1", captured[0].HostIp);
        Assert.Equal("10.9.9.2", captured[1].HostIp);
    }

    private sealed class CapturingSender : IChannelSender
    {
        private readonly List<AlertMessage> _sink;

        public CapturingSender(List<AlertMessage> sink) => _sink = sink;

        public string ChannelName => "capture";

        public Task SendAsync(AlertMessage alert, CancellationToken ct = default)
        {
            _sink.Add(alert);
            return Task.CompletedTask;
        }
    }
}
