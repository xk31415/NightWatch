using NightWatch.Notification;
using Xunit;

namespace NightWatch.Tests;

/// <summary>
/// EmailSender 的整封超时：MailKit 的 Timeout 只是单次网络操作超时，
/// 这里验证包装器能在总预算耗尽时抛「带阶段」的超时异常、且不吞调用方取消。
/// </summary>
public class EmailSendBudgetTests
{
    [Fact]
    public async Task OperationExceedingBudget_ThrowsTimeoutContainingStage()
    {
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => TimeBoundedOperation.RunAsync(
            async token => await Task.Delay(Timeout.InfiniteTimeSpan, token),
            () => "SMTP 认证",
            CancellationToken.None,
            TimeSpan.FromMilliseconds(50)));

        Assert.Contains("阶段：SMTP 认证", ex.Message, StringComparison.Ordinal);
        Assert.Contains("总预算", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAsCancellation_NotTimeout()
    {
        using var cts = new CancellationTokenSource();
        var task = TimeBoundedOperation.RunAsync(
            async token => await Task.Delay(Timeout.InfiniteTimeSpan, token),
            () => "发送邮件",
            cts.Token,
            TimeSpan.FromSeconds(30));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task OperationWithinBudget_CompletesWithoutThrowing()
    {
        var stage = "连接 SMTP";
        await TimeBoundedOperation.RunAsync(token =>
        {
            stage = "发送邮件";
            return Task.CompletedTask;
        }, () => stage, CancellationToken.None, TimeSpan.FromSeconds(1));

        Assert.Equal("发送邮件", stage);
    }

    [Fact]
    public void WholeSendBudget_IsFifteenSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(15), EmailSender.WholeSendBudget);
}
