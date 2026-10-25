using System.Diagnostics;

namespace NightWatch.Platform;

/// <summary>
/// Linux systemd unit 控制：经 <c>systemctl</c> 查询与启停。
/// 「暂停」在 systemd 里没有对应状态，Continue 等价于 Start。
/// </summary>
public sealed class SystemdServiceControl : IServiceControl
{
    public ServiceUnitStatus GetStatus(string serviceName)
    {
        var output = Run("is-active", serviceName, throwOnError: false);
        return Map(output.Trim());
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

    /// <summary>systemctl is-active 输出 → 统一状态。</summary>
    internal static ServiceUnitStatus Map(string isActiveOutput) => isActiveOutput switch
    {
        "active" => ServiceUnitStatus.Running,
        "inactive" or "failed" => ServiceUnitStatus.Stopped,
        "activating" => ServiceUnitStatus.StartPending,
        "deactivating" => ServiceUnitStatus.Other,
        _ => ServiceUnitStatus.Other
    };

    /// <summary>
    /// 调用 systemctl。is-active 用退出码表达状态（3=非 active 属正常查询结果，不抛），
    /// start/stop 等管理命令失败必须抛出，由调用方转为告警。
    /// </summary>
    private static string Run(string verb, string unit, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "systemctl",
            Arguments = $"{verb} {unit}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 systemctl");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (throwOnError && proc.ExitCode != 0)
            throw new InvalidOperationException($"systemctl {verb} {unit} 失败（退出码 {proc.ExitCode}）: {stderr.Trim()}");

        return stdout;
    }
}
