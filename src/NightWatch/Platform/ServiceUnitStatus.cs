namespace NightWatch.Platform;

/// <summary>
/// 跨平台服务单元状态。Windows 映射 ServiceControllerStatus，
/// Linux 映射 systemctl is-active 的状态。
/// </summary>
public enum ServiceUnitStatus
{
    /// <summary>正在运行（Windows: Running；systemd: active）。</summary>
    Running,

    /// <summary>已停止（Windows: Stopped；systemd: inactive / failed）。</summary>
    Stopped,

    /// <summary>已暂停（Windows: Paused；systemd 无对应状态，恒不出现）。</summary>
    Paused,

    /// <summary>启动中（Windows: StartPending；systemd: activating）。</summary>
    StartPending,

    /// <summary>单元不存在（systemd: is-active 退出码 4 / unknown）。Windows 侧由 ServiceController 抛异常表达。</summary>
    NotFound,

    /// <summary>其他/未知状态。</summary>
    Other
}
