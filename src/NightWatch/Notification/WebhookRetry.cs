using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NightWatch.Notification;

/// <summary>可重试的临时性通道失败：超时、5xx、限流 errcode 等。</summary>
internal sealed class TransientNotificationException : Exception
{
    public TransientNotificationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// webhook 通道的重试策略：只对「可重试失败」（超时 / 5xx / 限流 errcode）做 2 次退避重试
/// （默认 1s、3s）；4xx 与业务错误不重试，直接失败。
/// 所有尝试共享单通道超时预算（默认 15s）：每轮最多等 5s，预算耗尽后不再发起新的尝试，
/// 保证一个卡死的通道不会把队列排空拖到远超文档承诺的时间。
/// </summary>
internal static class WebhookRetry
{
    /// <summary>首次发送 + 2 次重试。</summary>
    public const int MaxAttempts = 3;

    /// <summary>单次尝试的超时（比 15s 预算小，否则超时后没有预算再重试）。</summary>
    public static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(5);

    /// <summary>单通道总超时预算，与 <see cref="QueuedNotifier"/> 的「单通道最长 15 秒」一致。</summary>
    public static readonly TimeSpan DefaultTotalBudget = TimeSpan.FromSeconds(15);

    /// <summary>两次重试前的等待（指数退避，约 1s、3s）。</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    /// <summary>
    /// 执行一次「发送 + 可选重试」。<paramref name="attempt"/> 抛
    /// <see cref="TransientNotificationException"/> / <see cref="HttpRequestException"/> /
    /// <see cref="TimeoutException"/> / <see cref="IOException"/> / <see cref="SocketException"/>、
    /// 或本次尝试超时时会重试；其它异常（如 4xx / 业务错误）原样抛出，不重试。
    /// </summary>
    /// <param name="elapsedProvider">
    /// 已耗时读数的来源，默认 <see cref="Stopwatch"/>；测试注入假时钟以避免依赖墙钟
    /// （CI 上每次 20ms 超时的调度开销叠加可能越过预算，导致「期望 3 次、实际 2 次」的假失败）。
    /// </param>
    public static async Task ExecuteAsync(string channelName, Func<CancellationToken, Task> attempt,
        ILogger logger, CancellationToken ct, IReadOnlyList<TimeSpan>? delays = null,
        TimeSpan? attemptTimeout = null, TimeSpan? totalBudget = null, Func<TimeSpan>? elapsedProvider = null)
    {
        var retryDelays = delays ?? DefaultDelays;
        var perAttemptTimeout = attemptTimeout ?? DefaultAttemptTimeout;
        var budget = totalBudget ?? DefaultTotalBudget;
        var maxAttempts = retryDelays.Count + 1;

        var stopwatch = Stopwatch.StartNew();
        TimeSpan Elapsed() => elapsedProvider?.Invoke() ?? stopwatch.Elapsed;
        Exception? lastError = null;
        var attemptsMade = 0;

        for (var attemptNumber = 1; attemptNumber <= maxAttempts; attemptNumber++)
        {
            var remaining = budget - Elapsed();
            if (remaining <= TimeSpan.Zero)
                break;

            attemptsMade++;
            var timeout = remaining < perAttemptTimeout ? remaining : perAttemptTimeout;
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(timeout);

            try
            {
                await attempt(attemptCts.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 调用方取消（停机）：向上抛，不重试
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException || IsRetryable(ex))
            {
                lastError = ex is OperationCanceledException
                    ? new TimeoutException($"第 {attemptNumber} 次尝试超时（{timeout.TotalSeconds:0.#}s 内未完成）", ex)
                    : ex;

                if (attemptNumber == maxAttempts)
                    break;

                var delay = retryDelays[attemptNumber - 1];
                if (Elapsed() + delay >= budget)
                    break;

                logger.LogWarning(
                    "通道 {Channel} 第 {Attempt}/{MaxAttempts} 次发送失败（可重试）：{Reason}；{DelaySeconds:0.#}s 后重试",
                    channelName, attemptNumber, maxAttempts, lastError.Message, delay.TotalSeconds);

                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"{channelName} 发送失败（已尝试 {attemptsMade} 次，单通道预算 {budget.TotalSeconds:0.#}s）：{lastError?.Message ?? "超出超时预算"}",
            lastError);
    }

    private static bool IsRetryable(Exception ex) =>
        ex is TransientNotificationException
            or HttpRequestException
            or TimeoutException
            or IOException
            or SocketException;
}
