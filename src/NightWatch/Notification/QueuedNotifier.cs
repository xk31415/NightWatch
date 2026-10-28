using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NightWatch.Notification;

/// <summary>
/// 通知异步化装饰器：告警写入有界队列后立即返回，由后台循环串行投递。
/// 单个通道最长可阻塞 15 秒（SMTP 超时），内联 await 会把同一监控器下所有规则的检查一起拖住。
/// 队列满时丢弃新消息并记 Warning——告警是尽力而为，反过来阻塞监控会掩盖真正的故障。
/// 丢弃必须可观测：<see cref="DroppedCount"/> 是累计计数，另外每 <see cref="DefaultDropSummaryInterval"/>
/// 汇总一条 WRN，避免「只看到零散丢弃日志、不知道总共丢了多少」。
/// 调用方通过 <see cref="TryEnqueue"/> 得知是否真的入队，避免「没发出去却进冷却」。
/// </summary>
public sealed class QueuedNotifier : IQueuedNotifier, IHostedService, IDisposable
{
    public const int DefaultCapacity = 256;

    /// <summary>丢弃汇总日志的最小间隔：持续满队列时避免每条丢弃都刷一条汇总。</summary>
    public static readonly TimeSpan DefaultDropSummaryInterval = TimeSpan.FromSeconds(60);

    private readonly IEventNotifier _inner;
    private readonly ILogger<QueuedNotifier> _logger;
    private readonly Channel<AlertMessage> _queue;
    private readonly TimeSpan _dropSummaryInterval;
    private readonly CancellationTokenSource _stop = new();
    private Task? _drainTask;

    private long _droppedTotal;
    private int _droppedSinceSummary;
    private long _lastDropSummaryUtcTicks = DateTime.UtcNow.Ticks;

    public QueuedNotifier(IEventNotifier inner, ILogger<QueuedNotifier> logger, int capacity = DefaultCapacity,
        TimeSpan? dropSummaryInterval = null)
    {
        _inner = inner;
        _logger = logger;
        Capacity = Math.Max(1, capacity);
        _dropSummaryInterval = dropSummaryInterval ?? DefaultDropSummaryInterval;
        _queue = Channel.CreateBounded<AlertMessage>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public int Capacity { get; }

    /// <summary>累计丢弃的告警条数（队列满或队列已关闭）。</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedTotal);

    /// <summary>
    /// 尝试入队；false = 未接受（队列满/已关闭）。调用方应据此决定是否占用告警冷却。
    /// </summary>
    public bool TryEnqueue(AlertMessage alert)
    {
        if (_queue.Writer.TryWrite(alert))
            return true;

        var total = Interlocked.Increment(ref _droppedTotal);
        Interlocked.Increment(ref _droppedSinceSummary);
        _logger.LogWarning("通知队列已满（{Capacity}），丢弃：{Title}（累计丢弃 {Dropped} 条）",
            Capacity, alert.Title, total);
        LogDropSummaryIfDue();
        return false;
    }

    /// <summary>兼容旧接口：无法得知入队结果的调用方仍可用（丢弃已记录，但调用方不会重试）。</summary>
    public Task SendAsync(AlertMessage alert, CancellationToken ct = default)
    {
        TryEnqueue(alert);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 周期汇总：距上次汇总超过间隔且有新丢弃时，打一条聚合 WRN（含窗口内条数与累计条数）。
    /// 用 CAS 抢占汇总权，多线程重复调用最多只有一条汇总。
    /// </summary>
    private void LogDropSummaryIfDue()
    {
        var now = DateTime.UtcNow;
        var last = Volatile.Read(ref _lastDropSummaryUtcTicks);
        if (now.Ticks - last < _dropSummaryInterval.Ticks)
            return;
        if (Interlocked.CompareExchange(ref _lastDropSummaryUtcTicks, now.Ticks, last) != last)
            return;

        var window = Interlocked.Exchange(ref _droppedSinceSummary, 0);
        var seconds = Math.Max(0, (now.Ticks - last) / TimeSpan.TicksPerSecond);
        _logger.LogWarning(
            "通知队列持续满：最近 {Seconds} 秒丢弃 {WindowCount} 条，累计丢弃 {Total} 条。请检查通道延迟或调大队列容量",
            seconds, window, DroppedCount);
    }

    public Task StartAsync(CancellationToken ct)
    {
        // 投递循环用独立 token：宿主停止时先排空队列，超时才取消。
        _drainTask = Task.Run(() => DrainAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _queue.Writer.TryComplete();
        if (_drainTask is null)
            return;

        try
        {
            // 给排队中的告警最多 5 秒投递时间，避免停机时丢光最后一批异常通知。
            await _drainTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            _stop.Cancel();
            _logger.LogWarning("通知队列未在 5 秒内排空，未投递的消息将被丢弃（剩余约 {Remaining} 条，累计丢弃 {Dropped} 条）",
                _queue.Reader.Count, DroppedCount);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "通知投递循环结束时出现异常");
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _stop.Dispose();
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var alert in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _inner.SendAsync(alert, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 停机取消：不再投递后续消息
                    break;
                }
                catch (Exception ex)
                {
                    // 内层通知器本身已按通道吞异常；这里兜底，防止单条消息打断整个投递循环。
                    _logger.LogWarning(ex, "投递通知失败：{Title}", alert.Title);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停机时取消
        }
    }
}
