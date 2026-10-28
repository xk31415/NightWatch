using Microsoft.Extensions.Logging.Abstractions;
using NightWatch.Heartbeat;
using NightWatch.Models;
using NightWatch.Monitoring;
using NightWatch.Notification;
using Xunit;

namespace NightWatch.Tests;

/// <summary>
/// 文件心跳的「超时 → 杀进程 → 拉起」全路径。
/// 与进程守护的门禁用例同型（见 ProcessLaunchTests.HungRestartPathTests）：
/// 心跳这条路径同样会结束目标进程，门禁错一处就是「杀掉却不拉起」——
/// 之前这条路径零测试，只能靠线上发现。杀/起动作经 <see cref="FileHeartbeatMonitor"/> 的
/// 注入点替换，用例不会真的动任何进程。
/// </summary>
public class FileHeartbeatMonitorTests
{
    /// <summary>阈值固定 5 分钟，断言文案时直接引用这个值。</summary>
    private const int StaleMinutes = 5;

    /// <summary>安全自检禁用拉起时：只告警，绝不 kill / start。</summary>
    [Fact]
    public void LaunchDisabled_DoesNotKillOrStart_AndSaysWhy()
    {
        using var h = new Harness(rule => rule.AllowLaunch = false);

        var result = h.Check();

        Assert.False(result.IsHealthy);
        Assert.Contains("安全自检已禁用自动拉起", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, h.KillCalls);
        Assert.Equal(0, h.StartCalls);
        h.AssertDiagnosable(result);
    }

    /// <summary>没配置可执行文件时同样不能动进程（否则杀掉就再也起不来）。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingFilePath_DoesNotKillOrStart(string? filePath)
    {
        using var h = new Harness(rule => rule.FilePath = filePath);

        var result = h.Check();

        Assert.False(result.IsHealthy);
        Assert.Contains("未配置 FilePath", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, h.KillCalls);
        Assert.Equal(0, h.StartCalls);
        h.AssertDiagnosable(result);
    }

    /// <summary>正常路径：杀 1 次、起 1 次，并在消息里说明已重启。</summary>
    [Fact]
    public void StaleHeartbeat_KillsOnceThenStartsOnce()
    {
        using var h = new Harness();

        var result = h.Check();

        Assert.False(result.IsHealthy);
        Assert.Contains("已重启目标进程", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, h.KillCalls);
        Assert.Equal(1, h.StartCalls);
        h.AssertDiagnosable(result);
    }

    /// <summary>
    /// 回归：旧进程没死就不要再拉一个，否则等于亲手制造双实例。
    /// 把「杀不掉就不拉起」这道门禁去掉时本用例必须变红（StartCalls 会变成 1）。
    /// </summary>
    [Fact]
    public void KillLeavesProcessRunning_SkipsStart_ToAvoidDoubleInstance()
    {
        using var h = new Harness();
        h.KillLeavesProcessRunning = true;

        var result = h.Check();

        Assert.False(result.IsHealthy);
        Assert.Contains("旧进程仍在运行", result.Message, StringComparison.Ordinal);
        Assert.Contains("避免双实例", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, h.KillCalls);
        Assert.Equal(0, h.StartCalls);
        h.AssertDiagnosable(result);
    }

    /// <summary>
    /// 连续 6 轮：只有第 1 轮会 kill+start，之后进入重启退避，只告警不动进程
    /// （否则每轮都杀一次会把目标反复打断）。
    /// </summary>
    [Fact]
    public void SixRounds_OnlyFirstKills_ThenBacksOff()
    {
        using var h = new Harness();

        for (var round = 1; round <= 6; round++)
        {
            var result = h.Check();
            Assert.False(result.IsHealthy);

            if (round == 1)
            {
                Assert.Contains("已重启目标进程", result.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("重启退避中", result.Message, StringComparison.Ordinal);
                Assert.Contains("本轮不杀进程", result.Message, StringComparison.Ordinal);
            }

            h.AssertDiagnosable(result);
        }

        Assert.Equal(1, h.KillCalls);
        Assert.Equal(1, h.StartCalls);
    }

    /// <summary>
    /// 退避熔断（连续 <see cref="RestartBackoff.MaxAttempts"/> 次无效后）：
    /// 不再 kill / start，消息要提示需人工介入。真实退避按分钟计，用例直接播种退避状态。
    /// </summary>
    [Fact]
    public void FusedBackoff_StopsKilling_AndAsksForManualIntervention()
    {
        using var h = new Harness();
        for (var i = 0; i < RestartBackoff.MaxAttempts; i++)
            h.Monitor.Backoff.NoteAttempt(h.Rule.Name);

        var result = h.Check();

        Assert.False(result.IsHealthy);
        Assert.Contains("已停止自动拉起", result.Message, StringComparison.Ordinal);
        Assert.Contains("需人工介入", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, h.KillCalls);
        Assert.Equal(0, h.StartCalls);
        h.AssertDiagnosable(result);
    }

    /// <summary>心跳恢复正常：不 kill / start，并清空退避（下次失联可立即拉起）。</summary>
    [Fact]
    public void FreshHeartbeat_ReportsHealthy_AndResetsBackoff()
    {
        using var h = new Harness(stale: false);
        h.Monitor.Backoff.NoteAttempt(h.Rule.Name);

        var result = h.Check();

        Assert.True(result.IsHealthy);
        Assert.Contains("心跳正常", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, h.KillCalls);
        Assert.Equal(0, h.StartCalls);

        // Reset 后立刻可以再次领取启动权
        Assert.True(h.Monitor.Backoff.TryClaim(h.Rule.Name, StaleMinutes, out var fused));
        Assert.False(fused);
    }

    /// <summary>拉起失败要报「重启失败」，并且这次失败也要计入退避（否则失败会无节制重试）。</summary>
    [Fact]
    public void StartFailure_ReportsFailure_AndCountsTowardBackoff()
    {
        using var h = new Harness();
        h.StartFailure = new InvalidOperationException("boom");

        var result = h.Check();

        Assert.False(result.IsHealthy);
        Assert.Contains("且重启失败", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, h.KillCalls);
        Assert.Equal(1, h.StartCalls);
        h.AssertDiagnosable(result);

        // 失败已计入 attempts=1，退避窗口内不允许再拉起
        Assert.False(h.Monitor.Backoff.TryClaim(h.Rule.Name, StaleMinutes, out _));
    }

    /// <summary>被测对象 + 临时心跳文件 + 记录调用的注入点。</summary>
    private sealed class Harness : IDisposable
    {
        private readonly string _dir;

        public Harness(Action<HeartbeatRule>? configure = null, bool stale = true)
        {
            _dir = Path.Combine(Path.GetTempPath(), "nw-hb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            HeartbeatFile = Path.Combine(_dir, "heartbeat.txt");
            var exePath = Path.Combine(_dir, "nw-heartbeat-probe.exe");
            File.WriteAllText(HeartbeatFile, "beat");
            File.WriteAllText(exePath, "placeholder");   // 只要求路径存在；用例不会执行它
            if (stale)
                File.SetLastWriteTime(HeartbeatFile, DateTime.Now.AddMinutes(-(StaleMinutes + 5)));

            Rule = new HeartbeatRule
            {
                Name = "hb-rule",
                HeartbeatFile = HeartbeatFile,
                StaleMinutes = StaleMinutes,
                ProcessName = "nw-heartbeat-probe",
                FilePath = exePath,
                AllowLaunch = true
            };
            configure?.Invoke(Rule);

            Monitor = new FileHeartbeatMonitor(
                new WatchDogOptions { FileHeartbeats = [Rule] },
                new ProcessLaunchCoordinator(),
                new NoopNotifier(),
                NullLogger<FileHeartbeatMonitor>.Instance);

            // 注入点：不真的结束/启动进程，只记录调用次数
            Monitor.KillAndProbeStillRunning = (_, _) =>
            {
                KillCalls++;
                return KillLeavesProcessRunning;
            };
            Monitor.StartTarget = _ =>
            {
                StartCalls++;
                if (StartFailure is not null)
                    throw StartFailure;
            };
        }

        public string HeartbeatFile { get; }

        public HeartbeatRule Rule { get; }

        public FileHeartbeatMonitor Monitor { get; }

        public int KillCalls { get; private set; }

        public int StartCalls { get; private set; }

        /// <summary>true = 注入的 kill 结束后进程仍存活（模拟杀不掉）。</summary>
        public bool KillLeavesProcessRunning { get; set; }

        /// <summary>非 null 时注入的 start 抛这个异常（模拟拉起失败）。</summary>
        public Exception? StartFailure { get; set; }

        public MonitorResult Check() => Monitor.CheckRule(Rule, CancellationToken.None);

        /// <summary>每条超时告警都必须能定位到文件与阈值（排查时不用再去翻配置）。</summary>
        public void AssertDiagnosable(MonitorResult result)
        {
            Assert.Contains(HeartbeatFile, result.Message, StringComparison.Ordinal);
            Assert.Contains($"阈值 {StaleMinutes} 分钟", result.Message, StringComparison.Ordinal);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // 清理失败不影响断言结果
            }
        }
    }

    private sealed class NoopNotifier : IEventNotifier
    {
        public Task SendAsync(AlertMessage alert, CancellationToken ct = default) => Task.CompletedTask;
    }
}
