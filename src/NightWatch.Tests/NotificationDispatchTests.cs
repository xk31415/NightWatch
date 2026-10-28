using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightWatch.Models;
using NightWatch.Notification;
using Xunit;

namespace NightWatch.Tests;

public class QueuedNotifierTests
{
    /// <summary>
    /// 回归：内层通道卡住（模拟 SMTP 15 秒超时）时，SendAsync 必须立即返回，
    /// 不能把监控检查循环一起拖住。
    /// </summary>
    [Fact]
    public async Task Send_DoesNotBlock_WhileChannelIsSlow_AndStillDelivers()
    {
        var inner = new GatedNotifier();
        using var notifier = new QueuedNotifier(inner, NullLogger<QueuedNotifier>.Instance);
        await notifier.StartAsync(CancellationToken.None);

        var sw = Stopwatch.StartNew();
        await notifier.SendAsync(new AlertMessage { Title = "first" });
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 200, $"SendAsync 阻塞了 {sw.ElapsedMilliseconds}ms");

        // 第一条已被后台取走并卡在内层，第二条进入队列
        await inner.WaitForCallsAsync(1);
        await notifier.SendAsync(new AlertMessage { Title = "second" });

        inner.Release(10);
        await notifier.StopAsync(CancellationToken.None);

        Assert.Equal(2, inner.Count);
    }

    [Fact]
    public async Task QueueFull_DropsNewMessages_WithoutThrowing()
    {
        var inner = new GatedNotifier();
        using var notifier = new QueuedNotifier(inner, NullLogger<QueuedNotifier>.Instance, capacity: 1);
        await notifier.StartAsync(CancellationToken.None);

        await notifier.SendAsync(new AlertMessage { Title = "first" });
        await inner.WaitForCallsAsync(1); // 队列已空，第一条正在投递

        await notifier.SendAsync(new AlertMessage { Title = "second" }); // 占满容量
        await notifier.SendAsync(new AlertMessage { Title = "third" });  // 被丢弃

        inner.Release(10);
        await notifier.StopAsync(CancellationToken.None);

        Assert.Equal(2, inner.Count);
    }

    [Fact]
    public async Task StopBeforeStart_DoesNotThrow()
    {
        using var notifier = new QueuedNotifier(new GatedNotifier(), NullLogger<QueuedNotifier>.Instance);
        await notifier.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// 入队语义：满队列时 TryEnqueue 返回 false（调用方据此不置冷却），并累计丢弃计数；
    /// 丢弃日志必须可观测（单条 WRN + 周期汇总 WRN）。
    /// </summary>
    [Fact]
    public async Task TryEnqueue_ReportsFullQueue_CountsDrops_AndLogsSummary()
    {
        var inner = new GatedNotifier();
        var logger = new ListLogger<QueuedNotifier>();
        using var notifier = new QueuedNotifier(inner, logger, capacity: 1, dropSummaryInterval: TimeSpan.Zero);
        await notifier.StartAsync(CancellationToken.None);

        Assert.True(notifier.TryEnqueue(new AlertMessage { Title = "first" }));
        await inner.WaitForCallsAsync(1); // 第一条已被后台取走，队列空

        Assert.True(notifier.TryEnqueue(new AlertMessage { Title = "second" })); // 占满容量
        Assert.False(notifier.TryEnqueue(new AlertMessage { Title = "third" }));  // 丢弃
        Assert.False(notifier.TryEnqueue(new AlertMessage { Title = "fourth" })); // 丢弃
        Assert.Equal(2, notifier.DroppedCount);

        var messages = logger.Messages;
        Assert.Contains(messages, m => m.Contains("累计丢弃", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("通知队列持续满", StringComparison.Ordinal));

        inner.Release(10);
        await notifier.StopAsync(CancellationToken.None);
    }

    private sealed class ListLogger<T> : ILogger<T>
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

    private sealed class GatedNotifier : IEventNotifier
    {
        private readonly SemaphoreSlim _gate = new(0);
        private readonly SemaphoreSlim _calls = new(0);
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public async Task SendAsync(AlertMessage alert, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            _calls.Release();
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }

        public Task WaitForCallsAsync(int expected) =>
            _calls.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully || Count < expected)
                    throw new TimeoutException($"等待 {expected} 次投递超时，实际 {Count}");
            });

        public void Release(int count) => _gate.Release(count);
    }
}

public class NotificationDispatchResultTests
{
    [Fact]
    public async Task SendWithResult_ReportsPerChannelFailures()
    {
        var notifier = new MultiChannelNotifier(
            [new FixedSender("ok"), new FixedSender("bad", fail: true)],
            NullLogger<MultiChannelNotifier>.Instance);

        var result = await notifier.SendWithResultAsync(new AlertMessage { Title = "t" });

        Assert.Equal(2, result.Total);
        Assert.Equal(1, result.Failed);
        Assert.False(result.AllSucceeded);
        Assert.Contains("bad", result.Failures[0]);
    }

    [Fact]
    public async Task SendWithResult_AllSucceed()
    {
        var notifier = new MultiChannelNotifier(
            [new FixedSender("a"), new FixedSender("b")],
            NullLogger<MultiChannelNotifier>.Instance);

        var result = await notifier.SendWithResultAsync(new AlertMessage());

        Assert.Equal(2, result.Total);
        Assert.True(result.AllSucceeded);
    }

    [Fact]
    public async Task SendWithResult_NoChannels_IsNotSuccess()
    {
        var notifier = new MultiChannelNotifier([], NullLogger<MultiChannelNotifier>.Instance);

        var result = await notifier.SendWithResultAsync(new AlertMessage());

        Assert.Equal(0, result.Total);
        Assert.False(result.AllSucceeded);
    }

    [Fact]
    public async Task SendAsync_SwallowsChannelFailures()
    {
        var notifier = new MultiChannelNotifier(
            [new FixedSender("bad", fail: true)],
            NullLogger<MultiChannelNotifier>.Instance);

        await notifier.SendAsync(new AlertMessage()); // 不抛
    }

    [Fact]
    public async Task SendWithResult_SkippedChannels_MakeResultFailed()
    {
        // skipped 通道（初始化被跳过 / 未知 Type）必须进结果，Program 才能据此让 --test-notify 退出 1。
        var notifier = new MultiChannelNotifier(
            [new FixedSender("ok")],
            NullLogger<MultiChannelNotifier>.Instance,
            skipped: ["邮件: 未配置 SmtpHost"]);

        var result = await notifier.SendWithResultAsync(new AlertMessage { Title = "t" });

        Assert.Equal(2, result.Total); // 1 个可用 + 1 个 skipped
        Assert.Equal(1, result.Failed);
        Assert.False(result.AllSucceeded);
        Assert.Contains("SmtpHost", result.Failures[0]);
    }

    [Fact]
    public void BuildSenders_CollectsSkippedChannels_InsteadOfSilentlyDropping()
    {
        var options = new NotifyOptions
        {
            Channels =
            [
                new NotifyChannel { Type = "wecom", Name = "正常企微", WebhookUrl = "https://example.invalid/hook" },
                new NotifyChannel { Type = "bogus", Name = "坏通道" },
                new NotifyChannel { Type = "email", Name = "邮件" } // 缺 SmtpHost
            ]
        };

        var (senders, skipped) = MultiChannelNotifier.BuildSenders(
            options, NullLoggerFactory.Instance, NullLogger<MultiChannelNotifier>.Instance);

        Assert.Single(senders);
        Assert.Equal(2, skipped.Count);
        Assert.Contains(skipped, s => s.Contains("坏通道", StringComparison.Ordinal)
                                      && s.Contains("未知的通知通道类型", StringComparison.Ordinal));
        Assert.Contains(skipped, s => s.Contains("邮件", StringComparison.Ordinal)
                                      && s.Contains("SmtpHost", StringComparison.Ordinal));
    }

    private sealed class FixedSender(string name, bool fail = false) : IChannelSender
    {
        public string ChannelName => name;

        public Task SendAsync(AlertMessage alert, CancellationToken ct = default) =>
            fail ? throw new InvalidOperationException("boom") : Task.CompletedTask;
    }
}

/// <summary>
/// webhook 重试策略：可重试失败（超时/5xx/限流 errcode）重试 2 次（约 1s、3s），
/// 4xx/业务错误不重试，且受单通道超时预算约束。
/// </summary>
public class WebhookRetryTests
{
    private static readonly IReadOnlyList<TimeSpan> NoDelay = [TimeSpan.Zero, TimeSpan.Zero];

    [Fact]
    public async Task RetryableFailure_IsRetriedTwice_ThenSucceeds()
    {
        var attempts = 0;
        await WebhookRetry.ExecuteAsync("测试通道", _ =>
        {
            attempts++;
            return attempts < 3
                ? Task.FromException(new TransientNotificationException("临时失败"))
                : Task.CompletedTask;
        }, NullLogger.Instance, CancellationToken.None, NoDelay);

        Assert.Equal(3, attempts); // 首次 + 2 次重试
    }

    [Fact]
    public async Task RetryableFailure_ExhaustsTwoRetries_ThenThrows()
    {
        var attempts = 0;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WebhookRetry.ExecuteAsync(
            "测试通道", _ => { attempts++; return Task.FromException(new TransientNotificationException("限流")); },
            NullLogger.Instance, CancellationToken.None, NoDelay));

        Assert.Equal(3, attempts);
        Assert.Contains("已尝试 3 次", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PermanentFailure_IsNotRetried()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => WebhookRetry.ExecuteAsync(
            "测试通道", _ => { attempts++; return Task.FromException(new InvalidOperationException("业务错误 4xx")); },
            NullLogger.Instance, CancellationToken.None, NoDelay));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task AttemptTimeout_CountsAsRetryable_AndStopsAtBudget()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => WebhookRetry.ExecuteAsync(
            "测试通道",
            async token =>
            {
                attempts++;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            NullLogger.Instance, CancellationToken.None, NoDelay,
            attemptTimeout: TimeSpan.FromMilliseconds(20),
            totalBudget: TimeSpan.FromMilliseconds(200)));

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task BudgetExhausted_StopsBeforeMaxAttempts()
    {
        // 首次尝试超时后只剩不够再等一次的预算：不应再发起重试（也不会把 1 次说成 3 次）
        var attempts = 0;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WebhookRetry.ExecuteAsync(
            "测试通道",
            async token =>
            {
                attempts++;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            NullLogger.Instance, CancellationToken.None,
            delays: [TimeSpan.FromSeconds(10), TimeSpan.Zero],
            attemptTimeout: TimeSpan.FromMilliseconds(20),
            totalBudget: TimeSpan.FromMilliseconds(100)));

        Assert.Equal(1, attempts);
        Assert.Contains("已尝试 1 次", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultPolicy_IsTwoRetriesWithOneAndThreeSecondDelays()
    {
        Assert.Equal(3, WebhookRetry.MaxAttempts);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) }, WebhookRetry.DefaultDelays);
        Assert.Equal(TimeSpan.FromSeconds(15), WebhookRetry.DefaultTotalBudget);
    }

    [Fact]
    public void RetryableErrCodes_CoverWeComAndDingTalkRateLimits()
    {
        Assert.True(WebhookResponse.IsRetryableErrcode(45009));  // 企微 api freq out of limit
        Assert.True(WebhookResponse.IsRetryableErrcode(130101)); // 钉钉发送过快
        Assert.False(WebhookResponse.IsRetryableErrcode(40001));
    }
}
