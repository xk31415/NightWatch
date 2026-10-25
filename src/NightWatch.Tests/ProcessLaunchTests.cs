using System.Diagnostics;
using NightWatch.Models;
using NightWatch.Monitoring;
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
