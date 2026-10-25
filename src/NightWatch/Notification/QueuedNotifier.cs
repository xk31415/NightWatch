using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NightWatch.Notification;

/// <summary>
/// 通知异步化装饰器：告警写入有界队列后立即返回，由后台循环串行投递。
/// 单个通道最长可阻塞 15 秒（SMTP 超时），内联 await 会把同一监控器下所有规则的检查一起拖住。
/// 队列满时丢弃新消息并记 Warning——告警是尽力而为，反过来阻塞监控会掩盖真正的故障。
/// </summary>
public sealed class QueuedNotifier : IEventNotifier, IHostedService, IDisposable
{
    public const int DefaultCapacity = 256;

    private readonly IEventNotifier _inner;
    private readonly ILogger<QueuedNotifier> _logger;
    private readonly Channel<AlertMessage> _queue;
    private readonly CancellationTokenSource _stop = new();
    private Task? _drainTask;

    public QueuedNotifier(IEventNotifier inner, ILogger<QueuedNotifier> logger, int capacity = DefaultCapacity)
    {
        _inner = inner;
        _logger = logger;
        Capacity = Math.Max(1, capacity);
        _queue = Channel.CreateBounded<AlertMessage>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public int Capacity { get; }

    public Task SendAsync(AlertMessage alert, CancellationToken ct = default)
    {
        if (!_queue.Writer.TryWrite(alert))
            _logger.LogWarning("通知队列已满（{Capacity}），丢弃：{Title}", Capacity, alert.Title);

        return Task.CompletedTask;
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
            _logger.LogWarning("通知队列未在 5 秒内排空，未投递的消息将被丢弃");
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
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
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
