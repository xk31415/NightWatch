namespace NightWatch.Notification;

/// <summary>结构化告警消息：各通道据此自行排版（markdown / HTML）。</summary>
public class AlertMessage
{
    /// <summary>告警标题，如 "HTTP心跳 / MyApp/userA"。</summary>
    public string Title { get; init; } = "";

    /// <summary>正文详情。</summary>
    public string Body { get; init; } = "";

    /// <summary>true = 异常告警（橙/红色调）；false = 通知/测试（绿色调）。</summary>
    public bool IsWarning { get; init; } = true;

    public DateTime Time { get; init; } = DateTime.Now;
}

/// <summary>事件通知接口：异常发生时向外部渠道推送消息。</summary>
public interface IEventNotifier
{
    Task SendAsync(AlertMessage alert, CancellationToken ct = default);
}
