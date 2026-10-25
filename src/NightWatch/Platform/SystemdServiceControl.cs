namespace NightWatch.Platform;

/// <summary>
/// Linux systemd unit 控制：经 <c>systemctl</c> 查询与启停。
/// 「暂停」在 systemd 里没有对应状态，Continue 等价于 Start。
/// 命令执行交给 <see cref="ProcessRunner"/>（15 秒超时、并发读流、ArgumentList 传参）。
/// </summary>
public sealed class SystemdServiceControl : IServiceControl
{
    public ServiceUnitStatus GetStatus(string serviceName)
    {
        var (stdout, exitCode) = Run("is-active", serviceName, throwOnError: false);
        return Map(stdout, exitCode);
    }

    public void Start(string serviceName) => Run("start", serviceName);

    public void Continue(string serviceName) => Run("start", serviceName);

    public void WaitForRunning(string serviceName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (GetStatus(serviceName) == ServiceUnitStatus.Running)
                return;
            Thread.Sleep(500);
        }

        throw new TimeoutException($"服务单元 {serviceName} 在 {timeout.TotalSeconds:F0} 秒内未进入 active");
    }

    /// <summary>
    /// systemctl is-active 输出 + 退出码 → 统一状态。
    /// is-active 的退出码：0=active，3=inactive/failed，4=单元不存在。
    /// </summary>
    internal static ServiceUnitStatus Map(string isActiveOutput, int exitCode = 0)
    {
        var state = (isActiveOutput ?? "").Trim();

        if (exitCode == 4 || state == "unknown")
            return ServiceUnitStatus.NotFound;

        return state switch
        {
            "active" or "reloading" => ServiceUnitStatus.Running,
            "inactive" or "failed" => ServiceUnitStatus.Stopped,
            "activating" => ServiceUnitStatus.StartPending,
            "deactivating" => ServiceUnitStatus.Other,
            _ => ServiceUnitStatus.Other
        };
    }

    /// <summary>
    /// 调用 systemctl。is-active 用退出码表达状态（3=非 active 属正常查询结果，不抛），
    /// start/stop 等管理命令失败必须抛出，由调用方转为告警。
    /// </summary>
    private static (string Stdout, int ExitCode) Run(string verb, string unit, bool throwOnError = true)
    {
        var result = ProcessRunner.Run("systemctl", [verb, unit]);

        if (throwOnError && result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"systemctl {verb} {unit} 失败（退出码 {result.ExitCode}）: {result.Stderr.Trim()}");
        }

        return (result.Stdout, result.ExitCode);
    }
}