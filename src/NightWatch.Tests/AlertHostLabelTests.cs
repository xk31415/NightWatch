using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightWatch.Models;
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

        // 主机 / IP / 告警项在同一个引用块内，字段之间用空引用行分段：
        // 单个换行会被钉钉折叠成空格（曾渲染成「主机：xxxIP：yyy」），
        // 拆成多个引用块又会在字段间留出大段空白。
        Assert.Contains("**主机：XINGKONG-PC**\n>\n> **IP：192.168.2.127**\n>\n> **通知通道自测**", md);
        Assert.DoesNotContain("PCIP", md);
        Assert.DoesNotContain("**主机：XINGKONG-PC**\n\n> **IP", md);
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

    /// <summary>
    /// 超长正文不能整条被服务端拒收：发送前按 UTF-8 字节上限截断，
    /// 保留头部信息，并按行截断后追加「已截断 N 字节」标记。
    /// </summary>
    [Fact]
    public void WeComMarkdown_TruncatesLongBody_ToByteLimit_WithMarker()
    {
        var body = LongBody();

        var md = MarkdownFormatter.FormatWeCom(new AlertMessage
        {
            Host = "prod-db-01",
            HostIp = "10.0.0.5",
            Title = "资源监控 / system-resources",
            Body = body,
            IsWarning = true,
            Time = new DateTime(2026, 10, 26, 8, 58, 33)
        });

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(md) <= MarkdownLimit.WebhookMaxBytes,
            $"截断后仍有 {System.Text.Encoding.UTF8.GetByteCount(md)} 字节");
        Assert.Contains("已截断", md, StringComparison.Ordinal);
        Assert.EndsWith("字节）", md, StringComparison.Ordinal);
        Assert.Contains("主机：prod-db-01", md, StringComparison.Ordinal);
        Assert.DoesNotContain("第400行", md, StringComparison.Ordinal); // 尾部确实被截掉
    }

    [Fact]
    public void DingTalkMarkdown_TruncatesLongBody_ToByteLimit_WithMarker()
    {
        var md = MarkdownFormatter.FormatDingTalk(new AlertMessage
        {
            Host = "prod-db-01",
            Title = "资源监控 / system-resources",
            Body = LongBody(),
            IsWarning = true,
            Time = new DateTime(2026, 10, 26, 8, 58, 33)
        });

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(md) <= MarkdownLimit.WebhookMaxBytes);
        Assert.Contains("已截断", md, StringComparison.Ordinal);
        Assert.EndsWith("字节）", md, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownLimit_ShortContent_IsUnchanged()
    {
        const string shortContent = "#### 标题\n\n正文";

        Assert.Equal(shortContent, MarkdownLimit.Truncate(shortContent, MarkdownLimit.WebhookMaxBytes));
        Assert.DoesNotContain("已截断", MarkdownLimit.Truncate(shortContent, MarkdownLimit.WebhookMaxBytes));
    }

    /// <summary>
    /// Escape 完整性：反斜杠先自身转义，链接/强调/代码/表格/删除线语法都被转义，
    /// 避免正文里的 markdown 片段变成可点击链接或吞掉后续排版的代码块。
    /// </summary>
    [Fact]
    public void MarkdownEscape_CoversBackslashAndStructureCharacters()
    {
        var md = MarkdownFormatter.FormatDingTalk(new AlertMessage
        {
            Title = "转义验证",
            Body = @"反斜杠 \* 与 [x](url) 与 _x_ 与 `code` 与 a|b 与 ~x~",
            IsWarning = false
        });

        Assert.Contains(@"\\\*", md, StringComparison.Ordinal);   // \ 先转义，\* 不再变强调
        Assert.Contains(@"\[x\](url)", md, StringComparison.Ordinal);
        Assert.DoesNotContain("[x](url)", md, StringComparison.Ordinal); // 不能被渲染成链接
        Assert.Contains(@"\_x\_", md, StringComparison.Ordinal);
        Assert.DoesNotContain("_x_", md, StringComparison.Ordinal);
        Assert.Contains(@"\`code\`", md, StringComparison.Ordinal);
        Assert.Contains(@"a\|b", md, StringComparison.Ordinal);
        Assert.Contains(@"\~x\~", md, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownEscape_EscapesLineStartListMarkers()
    {
        var md = MarkdownFormatter.FormatDingTalk(new AlertMessage
        {
            Title = "列表转义",
            Body = "- 第一项\n1. 第二项\n+ 第三项\n      - 缩进项",
            IsWarning = true
        });

        Assert.Contains(@"\- 第一项", md, StringComparison.Ordinal);
        Assert.Contains(@"1\. 第二项", md, StringComparison.Ordinal);
        Assert.Contains(@"\+ 第三项", md, StringComparison.Ordinal);
        Assert.Contains(@"      \- 缩进项", md, StringComparison.Ordinal);
    }

    /// <summary>钉钉把单个换行折叠成空格：正文行之间必须插空行，3 行正文不能被连成一行。</summary>
    [Fact]
    public void DingTalkMarkdown_MultiLineBody_IsNotFoldedIntoOneLine()
    {
        var md = MarkdownFormatter.FormatDingTalk(new AlertMessage
        {
            Title = "多行正文",
            Body = "第一行\n第二行\n第三行",
            IsWarning = true
        });

        Assert.Contains("第一行\n\n第二行\n\n第三行", md, StringComparison.Ordinal);
        Assert.DoesNotContain("第一行\n第二行", md, StringComparison.Ordinal);
    }

    /// <summary>Title/Host/HostIp 里的换行不能把内容带出引用块（否则后续行变成顶层 markdown）。</summary>
    [Fact]
    public void HeaderFields_WithNewlines_StayInsideQuoteBlock()
    {
        var md = MarkdownFormatter.FormatDingTalk(new AlertMessage
        {
            Host = "host-a\n> 注入行",
            HostIp = "10.0.0.5\r\n第二行",
            Title = "标题\n- 注入列表",
            Body = "正文",
            IsWarning = true
        });

        var quoteLines = md.Split('\n')
            .Where(line => line.StartsWith("> ", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(3, quoteLines.Count); // 主机、IP、告警项各一行，换行没有跑出引用块
        Assert.Contains("注入行", quoteLines[0], StringComparison.Ordinal);
        Assert.Contains("第二行", quoteLines[1], StringComparison.Ordinal);
        Assert.Contains("注入列表", quoteLines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void IncludeHostIpEnabled_StartupLogMentionsIpAndHowToTurnOff()
    {
        var logger = new CapturingLogger<MultiChannelNotifier>();
        _ = new MultiChannelNotifier(
            new NotifyOptions { IncludeHostIp = true, Channels = [] },
            NullLoggerFactory.Instance, logger);

        var joined = string.Join("\n", logger.Messages);
        Assert.Contains("将附加本机 IP", joined, StringComparison.Ordinal);
        Assert.Contains("Notify.IncludeHostIp=false", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void IncludeHostIpDisabled_StartupLogSaysNotAttached()
    {
        var logger = new CapturingLogger<MultiChannelNotifier>();
        _ = new MultiChannelNotifier(
            new NotifyOptions { IncludeHostIp = false, Channels = [] },
            NullLoggerFactory.Instance, logger);

        var joined = string.Join("\n", logger.Messages);
        Assert.Contains("不附加本机 IP", joined, StringComparison.Ordinal);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = new();

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages) return _messages.ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages) _messages.Add(formatter(state, exception));
        }
    }

    private static string LongBody() =>
        string.Join("\n", Enumerable.Range(1, 400)
            .Select(i => $"第{i}行：内存使用率 95.6% 超过阈值 95%（中文多字节用于验证不切坏字符）"));

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
