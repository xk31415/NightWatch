using NightWatch.Models;

namespace NightWatch.Notification;

/// <summary>
/// 多通道通知器：把消息并行分发到所有已配置通道。
/// 单个通道失败只记日志，不影响其他通道；等全部通道结束后再返回。
/// </summary>
public class MultiChannelNotifier : IEventNotifier
{
    private readonly List<IChannelSender> _senders;
    private readonly ILogger<MultiChannelNotifier> _logger;

    public MultiChannelNotifier(NotifyOptions options, ILoggerFactory loggerFactory, ILogger<MultiChannelNotifier> logger)
    {
        _logger = logger;
        _senders = new List<IChannelSender>();

        foreach (var ch in options.Channels)
        {
            if (string.Equals(ch.Type, "email", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(ch.SmtpHost))
            {
                logger.LogInformation("邮件通道 {Name} 未配置 SmtpHost，已跳过", string.IsNullOrWhiteSpace(ch.Name) ? "(未命名)" : ch.Name);
                continue;
            }

            try
            {
                _senders.Add(ch.Type?.Trim().ToLowerInvariant() switch
                {
                    "wecom" => new WeComSender(ch),
                    "dingtalk" => new DingTalkSender(ch),
                    "email" => new EmailSender(ch, loggerFactory.CreateLogger<EmailSender>()),
                    _ => throw new ArgumentException($"未知的通知通道类型: {ch.Type}")
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "通知通道 {Type}({Name}) 初始化失败，已跳过", ch.Type, ch.Name);
            }
        }

        logger.LogInformation("已启用 {Count} 个通知通道: {Channels}",
            _senders.Count, string.Join(", ", _senders.Select(s => s.ChannelName)));
    }

    public async Task SendAsync(AlertMessage alert, CancellationToken ct = default)
    {
        if (_senders.Count == 0)
            return;

        // 并行投递：最坏等待等于最慢通道（邮件 15s），而不是各通道超时相加。
        // 仍 await 全部完成，服务停止时能随 ct 取消；失败只记日志。
        await Task.WhenAll(_senders.Select(sender => SendOneAsync(sender, alert, ct)));
    }

    private async Task SendOneAsync(IChannelSender sender, AlertMessage alert, CancellationToken ct)
    {
        try
        {
            await sender.SendAsync(alert, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "通过通道 {Channel} 发送通知失败", sender.ChannelName);
        }
    }
}
