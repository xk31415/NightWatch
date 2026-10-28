namespace NightWatch.Notification;

/// <summary>
/// 结构化告警消息：各通道据此自行排版（markdown / HTML）。
/// 用 <c>record</c> 是为了让通知器能用 <c>with</c> 补全来源信息（主机 / IP），
/// 不必逐字段手工拷贝；后续加字段也不会漏拷。
/// </summary>
public sealed record AlertMessage
{
    /// <summary>告警标题，如 "HTTP心跳 / MyApp/userA"。</summary>
    public string Title { get; init; } = "";

    /// <summary>正文详情。</summary>
    public string Body { get; init; } = "";

    /// <summary>true = 异常告警（橙/红色调）；false = 通知/测试（绿色调）。</summary>
    public bool IsWarning { get; init; } = true;

    public DateTime Time { get; init; } = DateTime.Now;

    /// <summary>告警来源主机名。未设置时由通知器填入 <see cref="Models.NotifyOptions.HostLabel"/> 或计算机名。</summary>
    public string? Host { get; init; }

    /// <summary>
    /// 告警来源 IPv4（正文里单独一行展示）。为空且 <see cref="Models.NotifyOptions.IncludeHostIp"/> 为 true 时
    /// 由通知器在发送时填入，与 <see cref="Host"/> 是否已设置无关。
    /// </summary>
    public string? HostIp { get; init; }
}

/// <summary>事件通知接口：异常发生时向外部渠道推送消息。</summary>
public interface IEventNotifier
{
    Task SendAsync(AlertMessage alert, CancellationToken ct = default);
}

/// <summary>
/// 能同步返回「是否已接受」的通知器（当前由 <see cref="QueuedNotifier"/> 实现）。
/// 队列满等拒绝场景下调用方不能把消息当作已推送：<see cref="Monitoring.PollingMonitorBase"/>
/// 只在这个方法返回 true 后才占用告警冷却、才认为本轮异常「已告警」，
/// 否则会出现「告警没发出去、却进入冷却期，恢复时还发一条孤立的已恢复正常」。
/// </summary>
public interface IQueuedNotifier : IEventNotifier
{
    /// <summary>
    /// 尝试把告警放入发送队列。
    /// 返回 false 表示未接受（队列已满或已关闭），调用方应视为「本轮未告警」并在下轮重试。
    /// </summary>
    bool TryEnqueue(AlertMessage alert);
}
