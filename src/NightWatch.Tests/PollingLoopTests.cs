using Microsoft.Extensions.Logging.Abstractions;
using NightWatch.Monitoring;
using NightWatch.Notification;
using Xunit;

namespace NightWatch.Tests;

public class PollingLoopTests
{
    /// <summary>
    /// 回归：CheckAllAsync 抛异常时不能跳过 tick 等待。
    /// 修复前该循环会忙等——实测 500ms 内被调用 375,501 次，CPU 打满并刷爆日志。
    /// </summary>
    [Fact]
    public async Task ThrowingCheck_WaitsForNextTick_InsteadOfBusyLooping()
    {
        var monitor = new ThrowingMonitor();
        await monitor.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(400);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }

        // 间隔 1s：首轮立即执行，400ms 内应只执行 1 次（最多容忍 2 次调度误差）。
        Assert.InRange(monitor.Calls, 1, 2);
    }

    [Fact]
    public async Task HealthyCheck_StopsPromptly()
    {
        var monitor = new CountingMonitor();
        await monitor.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        var completed = monitor.StopAsync(CancellationToken.None);
        await completed.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(monitor.Calls >= 1);
    }

    private sealed class ThrowingMonitor : PollingMonitorBase
    {
        public int Calls;

        public ThrowingMonitor() : base(new NoopNotifier(), NullLogger<ThrowingMonitor>.Instance)
        {
        }

        protected override string MonitorName => "异常探针";
        protected override int IntervalSeconds => 1;

        protected override Task CheckAllAsync(CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class CountingMonitor : PollingMonitorBase
    {
        public int Calls;

        public CountingMonitor() : base(new NoopNotifier(), NullLogger<CountingMonitor>.Instance)
        {
        }

        protected override string MonitorName => "正常探针";
        protected override int IntervalSeconds => 1;

        protected override Task CheckAllAsync(CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class NoopNotifier : IEventNotifier
    {
        public Task SendAsync(AlertMessage alert, CancellationToken ct = default) => Task.CompletedTask;
    }
}
