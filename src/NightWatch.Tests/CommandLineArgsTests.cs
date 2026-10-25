using Xunit;

namespace NightWatch.Tests;

public class CommandLineArgsTests
{
    /// <summary>
    /// 回归：README 里的 `--test-notify --test-message "文案"` 顺序必须能取到文案。
    /// 修复前配置系统把 `--test-notify` 的下一个 token 当成它的值，test-message 取到空串。
    /// </summary>
    [Fact]
    public void ReadsValue_AfterSwitch_InDocumentedOrder()
    {
        string[] args = ["--test-notify", "--test-message", "主机A通知通道验证"];

        Assert.Equal("主机A通知通道验证", CommandLineArgs.Value(args, "test-message"));
    }

    [Fact]
    public void ReadsValue_EqualsForm()
    {
        string[] args = ["--test-message=主机A通知通道验证"];

        Assert.Equal("主机A通知通道验证", CommandLineArgs.Value(args, "test-message"));
    }

    [Fact]
    public void ReadsValue_RegardlessOfSwitchOrder()
    {
        string[] args = ["--test-message", "abc", "--test-notify"];

        Assert.Equal("abc", CommandLineArgs.Value(args, "test-message"));
    }

    [Fact]
    public void MissingValue_ReturnsNull()
    {
        Assert.Null(CommandLineArgs.Value(["--test-notify", "--test-message"], "test-message"));
        Assert.Null(CommandLineArgs.Value(["--test-notify"], "test-message"));
    }

    [Fact]
    public void Absent_ReturnsNull()
    {
        Assert.Null(CommandLineArgs.Value(["--other", "x"], "test-message"));
        Assert.Null(CommandLineArgs.Value([], "test-message"));
    }

    [Fact]
    public void DoesNotConfuseSimilarNames()
    {
        string[] args = ["--test-message-extra=no", "--test-message=yes"];

        Assert.Equal("yes", CommandLineArgs.Value(args, "test-message"));
    }
}
