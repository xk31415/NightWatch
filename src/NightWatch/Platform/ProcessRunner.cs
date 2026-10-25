using System.Diagnostics;

namespace NightWatch.Platform;

/// <summary>
/// 一次性外部命令执行（systemctl / stat 等）：带超时、stdout/stderr 并发读取。
/// 不这样做的话，systemd 卡住会把监控线程永久占住，stderr 写满管道还会死锁。
/// 参数一律走 <see cref="ProcessStartInfo.ArgumentList"/>，避免拼接后被空格/引号破坏。
/// </summary>
internal static class ProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    internal readonly record struct CommandResult(string Stdout, string Stderr, int ExitCode);

    public static CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan? timeout = null)
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

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动 {fileName}");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var limit = timeout ?? DefaultTimeout;

        if (!proc.WaitForExit((int)limit.TotalMilliseconds))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已自行退出
            }

            throw new TimeoutException(
                $"{fileName} {string.Join(' ', arguments)} 超过 {limit.TotalSeconds:F0} 秒未返回");
        }

        return new CommandResult(
            stdoutTask.GetAwaiter().GetResult(),
            stderrTask.GetAwaiter().GetResult(),
            proc.ExitCode);
    }
}
