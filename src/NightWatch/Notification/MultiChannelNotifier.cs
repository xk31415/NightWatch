using NightWatch.Models;

namespace NightWatch.Notification;

/// <summary>一次分发的结果：参与分发的通道数与失败详情。</summary>
/// <param name="Total">
/// 参与分发的通道数，**包含**初始化阶段被跳过的通道（未知 Type / 配置不全 / 构造异常）。
/// </param>
/// <param name="Failures">
/// 失败通道的说明（通道名 + 异常消息）；初始化阶段被跳过的通道也记在这里——
/// 对 <c>--test-notify</c> 而言「这个通道根本不可用」和「发送失败」一样不能算通过。
/// </param>
public sealed record NotifyDispatchResult(int Total, IReadOnlyList<string> Failures)
{
    public int Failed => Failures.Count;

    public bool AllSucceeded => Total > 0 && Failed == 0;

    public static NotifyDispatchResult Empty { get; } = new(0, Array.Empty<string>());
}

/// <summary>
/// 多通道通知器：把消息并行分发到所有已配置通道。
/// 单个通道失败只记日志，不影响其他通道；等全部通道结束后再返回。
/// </summary>
public class MultiChannelNotifier : IEventNotifier
{
    private readonly List<IChannelSender> _senders;
    private readonly IReadOnlyList<string> _skipped;
    private readonly ILogger<MultiChannelNotifier> _logger;
    private readonly string _hostName;
    private readonly HostIpCache? _hostIpCache;

    public MultiChannelNotifier(NotifyOptions options, ILoggerFactory loggerFactory, ILogger<MultiChannelNotifier> logger)
    {
        _logger = logger;
        _hostName = HostIdentity.ResolveHostName(options.HostLabel);
        _hostIpCache = options.IncludeHostIp ? new HostIpCache() : null;
        (_senders, _skipped) = BuildSenders(options, loggerFactory, logger);

        logger.LogInformation("已启用 {Count} 个通知通道: {Channels}；告警主机 {Host}，IP {IpMode}",
            _senders.Count, string.Join(", ", _senders.Select(s => s.ChannelName)), _hostName,
            _hostIpCache is null ? "不附带" : "发送时解析");

        if (_skipped.Count > 0)
        {
            logger.LogWarning("有 {Count} 个通知通道未启用（--test-notify 会按失败处理）: {Skipped}",
                _skipped.Count, string.Join("; ", _skipped));
        }
    }

    /// <summary>测试用：直接注入通道实现。</summary>
    internal MultiChannelNotifier(IEnumerable<IChannelSender> senders, ILogger<MultiChannelNotifier> logger,
        string? hostName = null, HostIpCache? hostIpCache = null, IReadOnlyList<string>? skipped = null)
    {
        _logger = logger;
        _hostName = HostIdentity.ResolveHostName(hostName);
        _hostIpCache = hostIpCache;
        _senders = senders.ToList();
        _skipped = skipped ?? Array.Empty<string>();
    }

    public async Task SendAsync(AlertMessage alert, CancellationToken ct = default) =>
        await SendWithResultAsync(alert, ct).ConfigureAwait(false);

    /// <summary>
    /// 分发并汇报结果：<c>--test-notify</c> 需要据此决定退出码，
    /// 避免所有通道都失败（或被跳过）时仍然打印“已发送”。
    /// </summary>
    public async Task<NotifyDispatchResult> SendWithResultAsync(AlertMessage alert, CancellationToken ct = default)
    {
        if (_senders.Count == 0 && _skipped.Count == 0)
            return NotifyDispatchResult.Empty;

        // 初始化阶段被跳过的通道从未发过消息，但它们同样「不可用」，先计入失败明细。
        var failures = new List<string>(_skipped);

        if (_senders.Count > 0)
        {
            alert = EnrichHost(alert);

            // 并行投递：最坏等待等于最慢通道（邮件 15s / webhook 重试预算 15s），而不是各通道超时相加。
            // 仍 await 全部完成，服务停止时能随 ct 取消；失败只记日志。
            var results = await Task.WhenAll(_senders.Select(sender => SendOneAsync(sender, alert, ct)))
                .ConfigureAwait(false);
            failures.AddRange(results.Where(r => r is not null).Select(r => r!));
        }

        return new NotifyDispatchResult(_senders.Count + _skipped.Count, failures);
    }

    private async Task<string?> SendOneAsync(IChannelSender sender, AlertMessage alert, CancellationToken ct)
    {
        try
        {
            await sender.SendAsync(alert, ct).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 取消必须向上抛（AGENTS.md 约定），否则停机会被记成「通道失败」
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "通过通道 {Channel} 发送通知失败", sender.ChannelName);
            return $"{sender.ChannelName}: {ex.Message}";
        }
    }

    /// <summary>
    /// 构造通道发送器，并把「被跳过的坏通道」一并返回给调用方：
    /// 未知 Type、email 缺 SmtpHost、构造抛异常都算 skipped，--test-notify 据此返回非 0。
    /// </summary>
    internal static (List<IChannelSender> Senders, List<string> Skipped) BuildSenders(
        NotifyOptions options, ILoggerFactory loggerFactory, ILogger<MultiChannelNotifier> logger)
    {
        var senders = new List<IChannelSender>();
        var skipped = new List<string>();
        foreach (var ch in options.Channels)
        {
            var display = DescribeChannel(ch);
            if (string.Equals(ch.Type, "email", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(ch.SmtpHost))
            {
                logger.LogWarning("邮件通道 {Name} 未配置 SmtpHost，已跳过（--test-notify 将按失败处理）", display);
                skipped.Add($"{display}: 未配置 SmtpHost");
                continue;
            }

            try
            {
                senders.Add(ch.Type?.Trim().ToLowerInvariant() switch
                {
                    "wecom" => new WeComSender(ch, loggerFactory.CreateLogger<WeComSender>()),
                    "dingtalk" => new DingTalkSender(ch, loggerFactory.CreateLogger<DingTalkSender>()),
                    "email" => new EmailSender(ch, loggerFactory.CreateLogger<EmailSender>()),
                    _ => throw new ArgumentException($"未知的通知通道类型: {ch.Type}")
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "通知通道 {Type}({Name}) 初始化失败，已跳过（--test-notify 将按失败处理）", ch.Type, ch.Name);
                skipped.Add($"{display}: 初始化失败（{ex.Message}）");
            }
        }

        return (senders, skipped);
    }

    private static string DescribeChannel(NotifyChannel channel)
    {
        if (!string.IsNullOrWhiteSpace(channel.Name))
            return channel.Name;
        return string.IsNullOrWhiteSpace(channel.Type) ? "(未命名通道)" : channel.Type;
    }

    /// <summary>
    /// 补全来源主机与 IP：两个字段各自判断，调用方只设置了 Host 时仍会补 IP；
    /// IP 走 <see cref="HostIpCache"/>，长驻服务不会一直推构造时的旧地址。
    /// </summary>
    private AlertMessage EnrichHost(AlertMessage alert)
    {
        var host = string.IsNullOrWhiteSpace(alert.Host) ? _hostName : alert.Host;
        var ip = string.IsNullOrWhiteSpace(alert.HostIp) ? _hostIpCache?.Get() : alert.HostIp;

        if (string.Equals(host, alert.Host, StringComparison.Ordinal) &&
            string.Equals(ip, alert.HostIp, StringComparison.Ordinal))
        {
            return alert;
        }

        // record + with：只改两个字段，其余字段（含以后新增的）自动保留
        return alert with { Host = host, HostIp = ip };
    }
}
