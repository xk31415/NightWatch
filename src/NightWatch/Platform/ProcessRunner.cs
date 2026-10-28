using System.Diagnostics;

namespace NightWatch.Platform;

/// <summary>
/// 一次性外部命令执行（systemctl / stat 等）：带超时、取消、stdout/stderr 并发读取。
/// 不这样做的话，systemd 卡住会把监控线程永久占住，stderr 写满管道还会死锁。
/// 参数一律走 <see cref="ProcessStartInfo.ArgumentList"/>，避免拼接后被空格/引号破坏。
/// </summary>
internal static class ProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>超时/取消时收集诊断信息的长度上限：stderr 只保留尾部，避免异常消息失控。</summary>
    internal const int MaxDiagnosticLength = 500;

    /// <summary>kill 之后等待进程退出的时间：回收僵尸并让 stderr 管道读到 EOF。</summary>
    internal static readonly TimeSpan KillWait = TimeSpan.FromSeconds(2);

    internal readonly record struct CommandResult(string Stdout, string Stderr, int ExitCode);

    /// <summary>
    /// 执行命令并等待退出。<paramref name="ct"/> 取消时立即 kill 子进程并抛
    /// <see cref="OperationCanceledException"/>（不会吞掉，也不会傻等到超时）。
    /// </summary>
    public static CommandResult Run(string fileName, IReadOnlyList<string> arguments,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        ct.ThrowIfCancellationRequested();

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动 {fileName}");

        // 先并发把两条管道读完：只 WaitForExit 的话，stderr 写满管道会死锁。
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var limit = timeout ?? DefaultTimeout;

        // 取消时由回调立刻 kill：只等 WaitForExit 的话，取消最坏要等满超时（默认 15 秒）。
        var cancelRequested = 0;
        using var registration = ct.Register(() =>
        {
            Interlocked.Exchange(ref cancelRequested, 1);
            TryKill(proc, out _);
        });

        if (!proc.WaitForExit(Milliseconds(limit)))
        {
            var diagnostics = new List<string>();
            KillAndWait(proc, diagnostics);
            var stderrTail = TryReadTail(stderrTask);
            if (stderrTail.Length > 0)
                diagnostics.Add($"stderr: {stderrTail}");

            if (Volatile.Read(ref cancelRequested) != 0)
            {
                throw new OperationCanceledException(
                    $"{fileName} 已取消；清理诊断: {string.Join("；", diagnostics)}", ct);
            }

            var suffix = diagnostics.Count > 0 ? $"（{string.Join("；", diagnostics)}）" : "";
            throw new TimeoutException(
                $"{fileName} {string.Join(' ', arguments)} 超过 {limit.TotalSeconds:F1} 秒未返回{suffix}");
        }

        if (Volatile.Read(ref cancelRequested) != 0)
        {
            // 取消回调已 kill；等它退出后再抛，避免留下僵尸进程和未读完的管道。
            proc.WaitForExit(Milliseconds(KillWait));
            throw new OperationCanceledException($"{fileName} 已被取消", ct);
        }

        return new CommandResult(
            stdoutTask.GetAwaiter().GetResult(),
            stderrTask.GetAwaiter().GetResult(),
            proc.ExitCode);
    }

    /// <summary>把毫秒数安全地转成 WaitForExit 的 int 参数（过大的 TimeSpan 不能溢出成负数）。</summary>
    internal static int Milliseconds(TimeSpan value) =>
        (int)Math.Clamp(value.TotalMilliseconds, 0, int.MaxValue);

    /// <summary>kill 并等待退出；失败原因写进 <paramref name="diagnostics"/>，随超时异常一起抛出。</summary>
    private static void KillAndWait(Process proc, List<string> diagnostics)
    {
        if (!TryKill(proc, out var killError))
            diagnostics.Add($"结束子进程失败: {killError}");

        try
        {
            if (!proc.WaitForExit(Milliseconds(KillWait)))
                diagnostics.Add($"kill 后 {KillWait.TotalSeconds:F0} 秒内未退出");
        }
        catch (Exception ex)
        {
            diagnostics.Add($"等待子进程退出失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool TryKill(Process proc, out string error)
    {
        error = "";
        try
        {
            // 已经退出（例如取消回调先到、或进程自行结束）不算失败，避免诊断里出现假「kill 失败」。
            if (proc.HasExited)
                return true;

            proc.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>超时后取 stderr 尾部用于诊断；读不到/没读完时给出可读的占位说明。</summary>
    private static string TryReadTail(Task<string> stderrTask)
    {
        try
        {
            if (!stderrTask.Wait(KillWait))
                return "(stderr 未读完)";

            return Tail(stderrTask.Result);
        }
        catch (Exception ex)
        {
            return $"(stderr 读取失败: {ex.GetType().Name})";
        }
    }

    /// <summary>截取文本尾部（诊断用）：过长时保留最后 <paramref name="maxLength"/> 个字符。</summary>
    internal static string Tail(string? text, int maxLength = MaxDiagnosticLength)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length <= maxLength)
            return trimmed;

        return "…" + trimmed[^maxLength..];
    }
}
