namespace NightWatch.Platform;

/// <summary>
/// 跨平台服务单元控制：Windows 服务 / Linux systemd unit 统一抽象。
/// 由 <see cref="WindowsServiceControl"/> 或 <see cref="SystemdServiceControl"/> 按平台提供实现。
/// </summary>
public interface IServiceControl
{
    /// <summary>查询服务单元状态。找不到单元时抛出异常，由调用方转为 Unhealthy 告警。</summary>
    ServiceUnitStatus GetStatus(string serviceName);

    /// <summary>启动已停止的服务单元。</summary>
    void Start(string serviceName);

    /// <summary>恢复已暂停的服务单元（systemd 无暂停态，等价于 Start）。</summary>
    void Continue(string serviceName);

    /// <summary>等待服务单元进入 Running，超时抛出 TimeoutException。</summary>
    void WaitForRunning(string serviceName, TimeSpan timeout);
}
