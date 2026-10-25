using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
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

    private sealed class FixedSender(string name, bool fail = false) : IChannelSender
    {
        public string ChannelName => name;

        public Task SendAsync(AlertMessage alert, CancellationToken ct = default) =>
            fail ? throw new InvalidOperationException("boom") : Task.CompletedTask;
    }
}
