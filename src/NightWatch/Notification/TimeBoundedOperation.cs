namespace NightWatch.Notification;

/// <summary>
/// 给「一次完整发送」套总超时预算：MailKit 的 <c>Timeout</c> 只约束单次网络操作，
/// 连接、认证、发信、断开串起来时总耗时可能远超单个操作超时。
/// 这里用链接 CTS 控制整段流程，超时抛出带「当前阶段」的 <see cref="TimeoutException"/>，便于排查卡在哪一步。
/// </summary>
internal static class TimeBoundedOperation
{
    /// <summary>
    /// 在 <paramref name="budget"/> 内执行 <paramref name="operation"/>。
    /// 调用方主动取消（<paramref name="ct"/>）时按取消向上抛；预算耗尽时抛包含阶段信息的超时异常。
    /// </summary>
    public static async Task RunAsync(Func<CancellationToken, Task> operation, Func<string> currentStage,
        CancellationToken ct, TimeSpan budget)
    {
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(budget);

        try
        {
            await operation(budgetCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && budgetCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"操作超过 {budget.TotalSeconds:0.#} 秒总预算（阶段：{currentStage()}）", ex);
        }
    }
}
