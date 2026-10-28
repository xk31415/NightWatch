using NightWatch.Platform;
using Xunit;

namespace NightWatch.Tests;

/// <summary>
/// Linux 平台实现的纯逻辑测试（/proc 解析、权限位判定、systemd 状态映射）。
/// 不触碰真实 /proc 或 systemctl，任意 OS 上都可跑。
/// </summary>
public class LinuxPlatformLogicTests
{
    // ---- /proc/stat 解析 ----

    [Fact]
    public void ParseCpuLine_ComputesIdleAndTotal()
    {
        // user nice system idle iowait irq softirq steal
        var line = "cpu  100 0 50 800 50 0 0 0";
        var (idle, total) = LinuxSystemMetrics.ParseCpuLine(line);

        Assert.Equal(850, idle);   // idle + iowait
        Assert.Equal(1000, total); // 所有计数之和
    }

    [Fact]
    public void ParseCpuLine_ToleratesMissingTrailingFields()
    {
        // 旧内核可能没有 steal 等字段
        var line = "cpu  10 0 5 85";
        var (idle, total) = LinuxSystemMetrics.ParseCpuLine(line);

        Assert.Equal(85, idle);
        Assert.Equal(100, total);
    }

    [Fact]
    public void ParseCpuLine_InvalidFormat_Throws()
    {
        Assert.ThrowsAny<Exception>(() => LinuxSystemMetrics.ParseCpuLine("cpu0 1 2 3 4"));
        Assert.ThrowsAny<Exception>(() => LinuxSystemMetrics.ParseCpuLine("bogus"));
    }

    /// <summary>
    /// 内核把 guest / guest_nice 也算进 user / nice，重复相加会让 total 偏大、CPU% 偏低。
    /// </summary>
    [Fact]
    public void ParseCpuLine_ExcludesGuestFromTotal()
    {
        // user nice system idle iowait irq softirq steal guest guest_nice
        var line = "cpu  100 0 50 800 50 0 0 0 30 10";
        var (idle, total) = LinuxSystemMetrics.ParseCpuLine(line);

        Assert.Equal(850, idle);   // idle + iowait
        Assert.Equal(1000, total); // 1040 - guest 30 - guest_nice 10
    }

    // ---- /proc/meminfo 解析 ----

    [Fact]
    public void ParseMemInfo_ReadsKbValues()
    {
        const string content = """
            MemTotal:       16333728 kB
            MemFree:         1234567 kB
            MemAvailable:    8000000 kB
            Buffers:          200000 kB
            Cached:          3000000 kB
            """;

        var map = LinuxSystemMetrics.ParseMemInfo(content);

        Assert.Equal(16333728, map["MemTotal"]);
        Assert.Equal(8000000, map["MemAvailable"]);
        Assert.Equal(1234567, map["MemFree"]);
    }

    // ---- cgroup v2 内存 ----

    [Theory]
    [InlineData("500", "1000", 50.0)]
    [InlineData("1000", "1000", 100.0)]
    [InlineData("2000", "1000", 100.0)] // 超过限额时夹到 100
    public void CgroupMemory_ComputesPercent(string current, string max, double expected)
    {
        var actual = LinuxSystemMetrics.ComputeCgroupMemoryPercent(current, max);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value, 3);
    }

    [Theory]
    [InlineData("500", "max")]
    [InlineData("500", "")]
    [InlineData("500", "0")]
    [InlineData("oops", "1000")]
    public void CgroupMemory_UnlimitedOrInvalid_ReturnsNull(string current, string max) =>
        Assert.Null(LinuxSystemMetrics.ComputeCgroupMemoryPercent(current, max));

    // ---- Linux 权限判定 ----

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute, 0, false)]   // root 755：可信
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute, 0, false)] // root 775：组可写但属主特权，放行
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute, 0, true)] // 777：other 可写
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute, 1000, true)] // 普通用户属主
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute, 33, true)]  // www-data 等系统账户：非 root 即不可信
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute, 1, true)]   // daemon：uid<1000 也不是特权账户
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute, 65534, true)] // nobody
    public void LinuxPermission_JudgesOwnerAndWorldWrite(UnixFileMode mode, int uid, bool expected)
    {
        Assert.Equal(expected, LinuxPermissionAuditor.IsUntrusted(mode, uid));
    }

    // ---- systemd 状态映射 ----

    [Theory]
    [InlineData("active", ServiceUnitStatus.Running)]
    [InlineData("reloading", ServiceUnitStatus.Running)]
    [InlineData("refreshing", ServiceUnitStatus.Running)]              // 运行中的过渡态，不能报异常
    [InlineData("inactive", ServiceUnitStatus.Stopped)]
    [InlineData("failed", ServiceUnitStatus.Stopped)]
    [InlineData("activating", ServiceUnitStatus.StartPending)]         // 启动中：等待，不当异常
    [InlineData("deactivating", ServiceUnitStatus.Stopped)]            // 正在停止：按已停止处理，可被自动拉起
    [InlineData("maintenance", ServiceUnitStatus.Other)]
    [InlineData("", ServiceUnitStatus.Other)]
    public void SystemdStatus_MapsToUnifiedEnum(string output, ServiceUnitStatus expected)
    {
        Assert.Equal(expected, SystemdServiceControl.Map(output));
    }

    /// <summary>单元不存在必须与“已停止”区分开，否则 unit 名写错会被当成状态异常反复尝试启动。</summary>
    [Fact]
    public void SystemdStatus_UnknownUnit_MapsToNotFound()
    {
        // is-active 对不存在的单元返回退出码 4
        Assert.Equal(ServiceUnitStatus.NotFound, SystemdServiceControl.Map("inactive", 4));
        Assert.Equal(ServiceUnitStatus.NotFound, SystemdServiceControl.Map("unknown", 0));
        // 正常查询（退出码 3 = 未运行）仍是 Stopped
        Assert.Equal(ServiceUnitStatus.Stopped, SystemdServiceControl.Map("inactive", 3));
    }

    // ---- systemd LoadState 兜底（systemd < 253 对不存在的 unit 只返回退出码 3） ----

    [Theory]
    [InlineData("not-found", true)]
    [InlineData("not-found\n", true)]
    [InlineData("  not-found  ", true)]
    [InlineData("loaded", false)]
    [InlineData("masked", false)]
    [InlineData("bad-setting", false)]
    [InlineData("", false)]
    public void SystemdLoadState_NotFoundDetection(string output, bool expected) =>
        Assert.Equal(expected, SystemdServiceControl.IsNotFoundLoadState(output));

    /// <summary>
    /// 老版本 systemd（&lt; 253）对不存在的 unit 只给 3 + "inactive"，与「已停止」无法区分；
    /// 必须靠 LoadState=not-found 兜底改判，否则 unit 名写错会被当成 Stopped 而每轮尝试 systemctl start。
    /// </summary>
    [Theory]
    [InlineData("not-found", 0, ServiceUnitStatus.NotFound)]
    [InlineData("not-found\n", 0, ServiceUnitStatus.NotFound)]
    [InlineData("loaded", 0, ServiceUnitStatus.Stopped)]
    [InlineData("masked", 0, ServiceUnitStatus.Stopped)]
    [InlineData("not-found", 1, ServiceUnitStatus.Stopped)] // LoadState 查询失败：保留 is-active 的结论
    [InlineData("", 1, ServiceUnitStatus.Stopped)]
    public void SystemdStatus_OldVersion_UsesLoadStateFallback(
        string loadState, int loadStateExitCode, ServiceUnitStatus expected)
    {
        var fromIsActive = SystemdServiceControl.Map("inactive", 3);
        Assert.Equal(ServiceUnitStatus.Stopped, fromIsActive); // 老版本对不存在/已停止都是 3

        Assert.Equal(expected, SystemdServiceControl.ApplyLoadState(fromIsActive, loadState, loadStateExitCode));
    }

    // ---- systemd GetStatus 的 NotFound 兜底链路（注入 systemctl 执行器，不依赖真实 systemd） ----

    private static SystemdServiceControl Control(
        (string Stdout, int ExitCode) isActive,
        (string Stdout, int ExitCode) loadState,
        List<IReadOnlyList<string>>? calls = null) =>
        new(arguments =>
        {
            calls?.Add(arguments);
            return arguments[0] == "is-active"
                ? new ProcessRunner.CommandResult(isActive.Stdout, "", isActive.ExitCode)
                : new ProcessRunner.CommandResult(loadState.Stdout, "", loadState.ExitCode);
        });

    /// <summary>
    /// 老版本 systemd（&lt; 253）端到端：is-active=3+"inactive" + LoadState=not-found → NotFound。
    /// 若 GetStatus 退回「只 Map is-active」的旧逻辑，本用例会红。
    /// </summary>
    [Fact]
    public void GetStatus_OldSystemd_NotfoundLoadState_MapsToNotFound()
    {
        var calls = new List<IReadOnlyList<string>>();
        var control = Control(("inactive", 3), ("not-found", 0), calls);

        Assert.Equal(ServiceUnitStatus.NotFound, control.GetStatus("myapp.service"));

        // 兜底查询必须用 systemctl show -p LoadState --value <unit>
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { "show", "-p", "LoadState", "--value", "myapp.service" }, calls[1]);
    }

    [Fact]
    public void GetStatus_OldSystemd_LoadedLoadState_StaysStopped()
    {
        var control = Control(("inactive", 3), ("loaded", 0));
        Assert.Equal(ServiceUnitStatus.Stopped, control.GetStatus("myapp.service"));
    }

    [Fact]
    public void GetStatus_ModernSystemd_Exit4_SkipsLoadStateQuery()
    {
        var calls = new List<IReadOnlyList<string>>();
        var control = Control(("inactive", 4), ("loaded", 0), calls);

        Assert.Equal(ServiceUnitStatus.NotFound, control.GetStatus("myapp.service"));
        Assert.Single(calls); // 退出码 4 已能定性，不再多起一个进程
    }

    [Fact]
    public void GetStatus_Active_SkipsLoadStateQuery()
    {
        var calls = new List<IReadOnlyList<string>>();
        var control = Control(("active", 0), ("loaded", 0), calls);

        Assert.Equal(ServiceUnitStatus.Running, control.GetStatus("myapp.service"));
        Assert.Single(calls);
    }

    [Fact]
    public void GetStatus_LoadStateQueryFails_KeepsIsActiveResult()
    {
        var control = Control(("inactive", 3), ("", 1));
        Assert.Equal(ServiceUnitStatus.Stopped, control.GetStatus("myapp.service"));
    }

    /// <summary>启动等待期间（activating）已能确定 unit 存在，不应每 500ms 多起一个 systemctl show。</summary>
    [Fact]
    public void GetStatus_Activating_SkipsLoadStateQuery()
    {
        var calls = new List<IReadOnlyList<string>>();
        var control = Control(("activating", 3), ("loaded", 0), calls);

        Assert.Equal(ServiceUnitStatus.StartPending, control.GetStatus("myapp.service"));
        Assert.Single(calls);
    }

    /// <summary>管理命令失败必须抛出，且错误信息带退出码与 stderr（便于定位 unit 名写错/权限不足）。</summary>
    [Fact]
    public void Start_Failure_IncludesExitCodeAndStderr()
    {
        var control = new SystemdServiceControl(
            _ => new ProcessRunner.CommandResult("", "Unit myapp.service not found.", 5));

        var ex = Assert.Throws<InvalidOperationException>(() => control.Start("myapp.service"));
        Assert.Contains("5", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    /// <summary>
    /// start 必须带 --no-block：慢启动服务不再被 ProcessRunner 的 15 秒超时判失败，
    /// 由 WaitForRunning 用 is-active 判定是否真的起来了。
    /// </summary>
    [Fact]
    public void StartAndContinue_UseNoBlock()
    {
        var calls = new List<IReadOnlyList<string>>();
        var control = new SystemdServiceControl(arguments =>
        {
            calls.Add(arguments);
            return new ProcessRunner.CommandResult("", "", 0);
        });

        control.Start("myapp.service");
        control.Continue("myapp.service");

        Assert.Equal(new[] { "start", "--no-block", "myapp.service" }, calls[0]);
        Assert.Equal(new[] { "start", "--no-block", "myapp.service" }, calls[1]);
    }

    /// <summary>
    /// is-active 与 LoadState 都失败（systemd 未作为 PID1 / D-Bus 不可用 / 权限不足）时：
    /// 返回值语义不变（Other），但要能把 systemctl 的 stderr 记进诊断，否则没法定性。
    /// </summary>
    [Fact]
    public void GetStatus_BothQueriesFail_LogsStderr_AndKeepsSemantics()
    {
        var diagnostics = new List<string>();
        var control = new SystemdServiceControl(
            arguments => arguments[0] == "is-active"
                ? new ProcessRunner.CommandResult("", "Failed to connect to bus: No such file or directory", 1)
                : new ProcessRunner.CommandResult("", "System has not been booted with systemd", 1),
            diagnostics.Add);

        Assert.Equal(ServiceUnitStatus.Other, control.GetStatus("myapp.service"));

        Assert.Contains(diagnostics, message => message.Contains("bus", StringComparison.Ordinal));
        Assert.Contains(diagnostics, message => message.Contains("not been booted", StringComparison.Ordinal));
    }
}
