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
        var sleep = new[] { "/bin/sleep", "/usr/bin/sleep" }.FirstOrDefault(File.Exists);
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
}
