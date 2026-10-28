using System.Diagnostics;
using NightWatch.Platform;
using Xunit;

namespace NightWatch.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public void Run_NonexistentBinary_Throws()
    {
        Assert.ThrowsAny<Exception>(() =>
            ProcessRunner.Run("nightwatch-no-such-binary-xyz", ["--version"]));
    }

    /// <summary>超时必须抛 TimeoutException 并结束子进程，不能把调用线程永久占住。</summary>
    [LinuxFact]
    public void Run_TimesOut()
    {
        var sleep = FindSleep();
        Assert.False(sleep is null, "找不到 sleep 可执行文件");

        var ex = Assert.Throws<TimeoutException>(() =>
            ProcessRunner.Run(sleep!, ["5"], TimeSpan.FromMilliseconds(200)));

        Assert.Contains("未返回", ex.Message);
    }

    /// <summary>参数必须原样传给子进程，不能被空格或引号破坏。</summary>
    [Fact]
    public void Run_PassesArgumentsVerbatim()
    {
        var probe = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        // Windows 上必须把整条命令作为单个参数：ArgumentList 会给含空格的参数加引号，
        // 拆成 /c echo "a b" 会把引号一起打印出来。
        var arguments = OperatingSystem.IsWindows()
            ? new[] { "/c", "echo a b" }
            : new[] { "-c", "printf '%s' \"$1\"", "sh", "a b" };

        var result = ProcessRunner.Run(probe, arguments);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("a b", result.Stdout.Trim());
    }

    // ---- 超时诊断：stderr 尾部必须随异常带出，否则「卡住」时无从排查 ----

    [LinuxFact]
    public void Run_Timeout_IncludesStderrTail()
    {
        var shell = FindShell();
        Assert.False(shell is null, "找不到 shell 可执行文件");

        var ex = Assert.Throws<TimeoutException>(() => ProcessRunner.Run(
            shell!, ["-c", "echo boom-on-stderr >&2; sleep 5"], TimeSpan.FromMilliseconds(300)));

        Assert.Contains("未返回", ex.Message);
        Assert.Contains("boom-on-stderr", ex.Message);
    }

    [WindowsFact]
    public void Run_Timeout_IncludesStderrTail_Windows()
    {
        // ArgumentList 会把整条命令作为单个参数交给 cmd /c（含空格与 & 也不会被拆散）
        var ex = Assert.Throws<TimeoutException>(() => ProcessRunner.Run(
            "cmd.exe", ["/c", "echo boom-on-stderr 1>&2 & ping -n 10 127.0.0.1 >nul"],
            TimeSpan.FromMilliseconds(500)));

        Assert.Contains("未返回", ex.Message);
        Assert.Contains("boom-on-stderr", ex.Message);
    }

    /// <summary>纯函数：诊断只保留尾部、并去掉首尾空白。</summary>
    [Fact]
    public void Tail_KeepsTailAndTruncates()
    {
        var longText = "  " + new string('x', ProcessRunner.MaxDiagnosticLength + 50) + "  ";
        var tail = ProcessRunner.Tail(longText);

        Assert.StartsWith("…", tail);
        Assert.Equal(ProcessRunner.MaxDiagnosticLength + 1, tail.Length);
        Assert.EndsWith(new string('x', 10), tail);

        Assert.Equal("boom", ProcessRunner.Tail(" boom \n"));
        Assert.Equal("", ProcessRunner.Tail(null));
    }

    // ---- 取消：kill 子进程并抛 OperationCanceledException，不能傻等超时 ----

    [LinuxFact]
    public void Run_Cancel_KillsChild_AndThrowsOperationCanceled()
    {
        var sleep = FindSleep();
        Assert.False(sleep is null, "找不到 sleep 可执行文件");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var stopwatch = Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ProcessRunner.Run(sleep!, ["5"], TimeSpan.FromSeconds(10), cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"取消应立即返回（不应等满 10 秒超时），实际耗时 {stopwatch.Elapsed}");
    }

    [WindowsFact]
    public void Run_Cancel_KillsChild_AndThrowsOperationCanceled_Windows()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() => ProcessRunner.Run(
            "cmd.exe", ["/c", "ping -n 10 127.0.0.1 >nul"], TimeSpan.FromSeconds(10), cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"取消应立即返回（不应等满 10 秒超时），实际耗时 {stopwatch.Elapsed}");
    }

    private static string? FindSleep() =>
        new[] { "/bin/sleep", "/usr/bin/sleep" }.FirstOrDefault(File.Exists);

    private static string? FindShell() =>
        new[] { "/bin/sh", "/usr/bin/sh" }.FirstOrDefault(File.Exists);
}
