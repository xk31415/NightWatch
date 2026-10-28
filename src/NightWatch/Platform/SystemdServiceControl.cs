namespace NightWatch.Platform;

/// <summary>
/// Linux systemd unit 控制：经 <c>systemctl</c> 查询与启停。
/// 「暂停」在 systemd 里没有对应状态，Continue 等价于 Start。
/// 命令执行交给 <see cref="ProcessRunner"/>（15 秒超时、并发读流、ArgumentList 传参）。
/// </summary>
public sealed class SystemdServiceControl : IServiceControl
{
    private readonly Func<IReadOnlyList<string>, ProcessRunner.CommandResult> _runSystemctl;

    public SystemdServiceControl()
        : this(static arguments => ProcessRunner.Run("systemctl", arguments))
    {
    }

    /// <summary>注入 systemctl 执行器，便于单测 NotFound 的兜底链路（无需真实 systemd）。</summary>
    internal SystemdServiceControl(Func<IReadOnlyList<string>, ProcessRunner.CommandResult> runSystemctl)
    {
        _runSystemctl = runSystemctl;
    }

    public ServiceUnitStatus GetStatus(string serviceName)
    {
        var (stdout, exitCode) = Run("is-active", serviceName, throwOnError: false);
        var status = Map(stdout, exitCode);

        // 现代 systemd（>= 253）对不存在的 unit 返回退出码 4，Map 已直接判为 NotFound。
        // 更老的版本（Ubuntu 20.04/22.04、Debian 11、RHEL 8/9 等）只给 3 + "inactive"，
        // 与「已停止」无法区分，必须再查 LoadState 才能把「unit 名写错」和「服务停了」分开。
        if (status == ServiceUnitStatus.NotFound || exitCode == 0)
            return status;

        // activating 已经证明 unit 文件存在（正在启动），不必再查 LoadState：
        // WaitForRunning 每 500ms 轮询一次，多起一个 systemctl 只会白白拖慢启动等待。
        if (status == ServiceUnitStatus.StartPending)
            return status;

        var (loadState, loadStateExitCode) = RunLoadState(serviceName);
        return ApplyLoadState(status, loadState, loadStateExitCode);
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
    /// is-active 的退出码：0=active，3=inactive/failed，4=单元不存在（仅 systemd >= 253；
    /// 更老版本对不存在的 unit 也返回 3，调用方需用 LoadState 兜底，见 <see cref="ApplyLoadState"/>）。
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
    /// 查询 unit 的 LoadState（unit 文件是否存在）。<c>--value</c> 只输出属性值，避免解析
    /// <c>LoadState=...</c> 前缀。查询本身失败时返回非 0 退出码，由 <see cref="ApplyLoadState"/> 忽略。
    /// </summary>
    private (string Stdout, int ExitCode) RunLoadState(string unit)
    {
        var result = _runSystemctl(["show", "-p", "LoadState", "--value", unit]);
        return (result.Stdout, result.ExitCode);
    }

    /// <summary>
    /// is-active 的结论 + LoadState 兜底：仅当 LoadState 查询成功且值为 <c>not-found</c> 时改判 NotFound，
    /// 其余情况保留 is-active 的结论（查询失败不能把「已停止」误判成「不存在」，也不能吞掉真实状态）。
    /// 抽成纯函数，便于覆盖老版本 systemd 与查询失败两条分支。
    /// </summary>
    internal static ServiceUnitStatus ApplyLoadState(
        ServiceUnitStatus statusFromIsActive, string loadStateOutput, int loadStateExitCode)
    {
        if (loadStateExitCode != 0)
            return statusFromIsActive;

        return IsNotFoundLoadState(loadStateOutput)
            ? ServiceUnitStatus.NotFound
            : statusFromIsActive;
    }

    /// <summary>LoadState 是否为 not-found（systemd 输出的值固定小写，结尾带换行）。</summary>
    internal static bool IsNotFoundLoadState(string? loadStateOutput) =>
        string.Equals((loadStateOutput ?? "").Trim(), "not-found", StringComparison.Ordinal);

    /// <summary>
    /// 调用 systemctl。is-active 用退出码表达状态（3=非 active 属正常查询结果，不抛），
    /// start/stop 等管理命令失败必须抛出，由调用方转为告警。
    /// </summary>
    private (string Stdout, int ExitCode) Run(string verb, string unit, bool throwOnError = true)
    {
        var result = _runSystemctl([verb, unit]);

        if (throwOnError && result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"systemctl {verb} {unit} 失败（退出码 {result.ExitCode}）: {result.Stderr.Trim()}");
        }

        return (result.Stdout, result.ExitCode);
    }
}