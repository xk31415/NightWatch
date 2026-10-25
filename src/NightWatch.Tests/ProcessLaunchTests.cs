using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NightWatch.Models;
using NightWatch.Monitoring;
using NightWatch.Notification;
using Xunit;

namespace NightWatch.Tests;

public class ProcessLaunchTests
{
    /// <summary>
    /// 回归：配置了 FilePath 时不能再用 ProcessName 当枚举名。
    /// 两者不一致会让存活判定永远失败，从而每个周期重复拉起（双实例）。
    /// </summary>
    [Fact]
    public void FindTargets_UsesImageName_WhenConfiguredProcessNameMismatches()
    {
        var self = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(self));

        var found = ProcessImage.FindTargets("definitely-not-this-test-host", self);
        try
        {
            Assert.Contains(found, p => p.Id == Environment.ProcessId);
        }
        finally
        {
            foreach (var p in found) p.Dispose();
        }
    }

    [Fact]
    public void FindTargets_EmptyProcessName_StillMatchesByPath()
    {
        var self = Environment.ProcessPath;
        var found = ProcessImage.FindTargets(null, self);
        try
        {
            Assert.Contains(found, p => p.Id == Environment.ProcessId);
        }
        finally
        {
            foreach (var p in found) p.Dispose();
        }
    }

    [Fact]
    public void IsRunning_MatchesByImagePath_NotByName()
    {
        Assert.True(ProcessImage.IsRunning("some-other-name", Environment.ProcessPath));
        Assert.False(ProcessImage.IsRunning("some-other-name", @"D:\no-such-dir\no-such-app.exe"));
    }

    [Fact]
    public void FindTargets_WithoutAnyIdentity_ReturnsEmpty()
    {
        Assert.Empty(ProcessImage.FindTargets(null, null));
        Assert.Empty(ProcessImage.FindTargets("  ", "  "));
    }

    /// <summary>
    /// 回归：AllowLaunch=false（安全自检禁用拉起）或缺少 FilePath 时不得强杀进程，
    /// 否则会把目标留在“已死且不会拉起”的状态。
    /// </summary>
    [Theory]
    [InlineData(false, @"D:\apps\myapp\myapp.exe", "已禁用自动拉起")]
    [InlineData(true, "", "未配置 FilePath")]
    [InlineData(true, null, "未配置 FilePath")]
    public void HungRestart_IsBlocked_WhenItCannotRestart(bool allowLaunch, string? filePath, string expected)
    {
        var rule = new ProcessRule
        {
            AllowLaunch = allowLaunch,
            FilePath = filePath,
            RestartIfNotResponding = true
        };

        var reason = ProcessMonitor.HungRestartBlockReason(rule);

        Assert.NotNull(reason);
        Assert.Contains(expected, reason);
        Assert.Contains("不结束进程", reason);
    }

    [Fact]
    public void HungRestart_IsAllowed_WhenLaunchEnabledAndPathConfigured()
    {
        var rule = new ProcessRule
        {
            AllowLaunch = true,
            FilePath = @"D:\apps\myapp\myapp.exe",
            RestartIfNotResponding = true
        };

        Assert.Null(ProcessMonitor.HungRestartBlockReason(rule));
    }

    /// <summary>回归：示例配置里的 "WorkingDirectory": "" 必须回落到 exe 所在目录，而不是 System32。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void StartInfo_BlankWorkingDirectory_FallsBackToExeDirectory(string? workingDirectory)
    {
        var psi = ProcessStartInfoFactory.Create(@"D:\apps\myapp\myapp.exe", null, workingDirectory);

        Assert.Equal(@"D:\apps\myapp", psi.WorkingDirectory);
        Assert.Equal(@"D:\apps\myapp\myapp.exe", psi.FileName);
        Assert.Equal("", psi.Arguments);
        Assert.False(psi.UseShellExecute);
    }

    [Fact]
    public void StartInfo_ExplicitWorkingDirectory_IsKept()
    {
        var psi = ProcessStartInfoFactory.Create(@"D:\apps\myapp\myapp.exe", "--flag", @"E:\data");

        Assert.Equal(@"E:\data", psi.WorkingDirectory);
        Assert.Equal("--flag", psi.Arguments);
    }

    [Fact]
    public void Coordinator_KeyIsCaseInsensitivePath()
    {
        var a = ProcessLaunchCoordinator.MakeKey(@"D:\Apps\MyApp\myapp.exe", "myapp");
        var b = ProcessLaunchCoordinator.MakeKey(@"d:\apps\myapp\MYAPP.EXE", "other-name");

        Assert.Equal(a, b);
    }
}

/// <summary>
/// 回归：强杀门禁必须作用在**真实处理路径**上，而不只是门禁函数本身。
/// 之前的实现只在测试里调用了 HungRestartBlockReason，生产路径直接杀进程再 StartProcess，
/// 导致 AllowLaunch=false 时既杀掉又重启（比修复前更糟）。这里通过注入 kill/start 覆盖整条路径。
/// </summary>
public class HungRestartPathTests
{
    private const string ExePath = @"D:\apps\myapp\myapp.exe";

    private static ProcessMonitor NewMonitor(ProcessRule rule) =>
        new(new WatchDogOptions { Processes = [rule] },
            new ProcessLaunchCoordinator(),
            new NoopNotifier(),
            NullLogger<ProcessMonitor>.Instance);

    private static ProcessRule NewRule(bool allowLaunch = true, string? filePath = ExePath) => new()
    {
        Name = "app",
        ProcessName = "myapp",
        FilePath = filePath,
        AllowLaunch = allowLaunch,
        RestartIfNotResponding = true
    };

    [Fact]
    public void LaunchDisabled_DoesNotKillOrStart()
    {
        var rule = NewRule(allowLaunch: false);
        var monitor = NewMonitor(rule);
        var kills = 0;
        var starts = 0;

        var result = monitor.HandleHungProcesses(rule, [101], "myapp",
            kill: _ => { kills++; return true; },
            start: _ => { starts++; return ProcessMonitor.StartOutcome.Started; });

        Assert.False(result.IsHealthy);
        Assert.Contains("已禁用自动拉起", result.Message);
        Assert.Contains("不结束进程", result.Message);
        Assert.Equal(0, kills);
        Assert.Equal(0, starts);
    }

    [Fact]
    public void MissingFilePath_DoesNotKillOrStart()
    {
        var rule = NewRule(filePath: " ");
        var monitor = NewMonitor(rule);
        var kills = 0;
        var starts = 0;

        var result = monitor.HandleHungProcesses(rule, [102], "myapp",
            kill: _ => { kills++; return true; },
            start: _ => { starts++; return ProcessMonitor.StartOutcome.Started; });

        Assert.False(result.IsHealthy);
        Assert.Contains("未配置 FilePath", result.Message);
        Assert.Equal(0, kills);
        Assert.Equal(0, starts);
    }

    [Fact]
    public void LaunchEnabled_KillsAndStarts()
    {
        var rule = NewRule();
        var monitor = NewMonitor(rule);
        var killedPids = new List<int>();
        var starts = 0;

        var result = monitor.HandleHungProcesses(rule, [103, 104], "myapp",
            kill: pid => { killedPids.Add(pid); return true; },
            start: _ => { starts++; return ProcessMonitor.StartOutcome.Started; });

        Assert.False(result.IsHealthy);
        Assert.Contains("已强杀并重启 2 个实例", result.Message);
        Assert.Equal(new[] { 103, 104 }, killedPids);
        Assert.Equal(1, starts);
    }

    [Fact]
    public void KillFailure_SkipsStart()
    {
        var rule = NewRule();
        var monitor = NewMonitor(rule);
        var starts = 0;

        var result = monitor.HandleHungProcesses(rule, [105], "myapp",
            kill: _ => false,
            start: _ => { starts++; return ProcessMonitor.StartOutcome.Started; });

        Assert.Contains("已跳过重启以避免双实例", result.Message);
        Assert.Equal(0, starts);
    }

    [Fact]
    public void StartFailure_IsReportedAsFailure()
    {
        var rule = NewRule();
        var monitor = NewMonitor(rule);

        var result = monitor.HandleHungProcesses(rule, [106], "myapp",
            kill: _ => true,
            start: _ => ProcessMonitor.StartOutcome.Failed);

        Assert.Contains("已强杀但拉起失败", result.Message);
    }

    [Fact]
    public void SecondCallWithinDedupWindow_SkipsKill()
    {
        var rule = NewRule();
        var monitor = NewMonitor(rule);
        var kills = 0;
        var starts = 0;

        var first = monitor.HandleHungProcesses(rule, [107], "myapp",
            kill: _ => { kills++; return true; },
            start: _ => { starts++; return ProcessMonitor.StartOutcome.Started; });

        var second = monitor.HandleHungProcesses(rule, [107], "myapp",
            kill: _ => { kills++; return true; },
            start: _ => { starts++; return ProcessMonitor.StartOutcome.Started; });

        Assert.Contains("已强杀并重启", first.Message);
        Assert.Contains("去重窗口", second.Message);
        Assert.False(second.Notify);
        Assert.Equal(1, kills);
        Assert.Equal(1, starts);
    }

    private sealed class NoopNotifier : IEventNotifier
    {
        public Task SendAsync(AlertMessage alert, CancellationToken ct = default) => Task.CompletedTask;
    }
}
