using System.Diagnostics;

namespace NightWatch.Platform;

/// <summary>
/// Linux systemd unit 控制：经 <c>systemctl</c> 查询与启停。
/// 「暂停」在 systemd 里没有对应状态，Continue 等价于 Start。
/// </summary>
public sealed class SystemdServiceControl : IServiceControl
{
    /// <summary>单次 systemctl 调用的超时；卡住时不能把监控线程一起拖死。</summary>
    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    public ServiceUnitStatus GetStatus(string serviceName)
    {
        var result = Run("is-active", serviceName, throwOnError: false);
        return Map(result.Stdout, result.ExitCode);
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
    /// stdout/stderr 并发读取，避免 stderr 写满管道造成的死锁。
    /// </summary>
    private static (string Stdout, int ExitCode) Run(string verb, string unit, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "systemctl",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // 用 ArgumentList 而不是拼字符串：unit 名来自配置，拼接会被空格/引号破坏
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(unit);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 systemctl");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)CommandTimeout.TotalMilliseconds))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已自行退出
            }

            throw new TimeoutException(
                $"systemctl {verb} {unit} 超过 {CommandTimeout.TotalSeconds:F0} 秒未返回");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (throwOnError && proc.ExitCode != 0)
            throw new InvalidOperationException($"systemctl {verb} {unit} 失败（退出码 {proc.ExitCode}）: {stderr.Trim()}");

        return (stdout, proc.ExitCode);
    }
}
