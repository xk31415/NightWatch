namespace NightWatch.Monitoring;

/// <summary>单次检查结果。</summary>
public class MonitorResult
{
    public bool IsHealthy { get; init; }

    /// <summary>给日志/告警用的描述信息。</summary>
    public string Message { get; init; } = "";

    /// <summary>是否需要向外部渠道推送通知（false 时仅记日志）。</summary>
    public bool Notify { get; init; } = true;

    public static MonitorResult Healthy(string message = "OK") => new() { IsHealthy = true, Message = message };
    public static MonitorResult Unhealthy(string message, bool notify = true) => new() { IsHealthy = false, Message = message, Notify = notify };
}
