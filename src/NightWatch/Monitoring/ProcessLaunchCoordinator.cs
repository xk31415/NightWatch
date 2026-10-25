using System.Collections.Concurrent;

namespace NightWatch.Monitoring;

/// <summary>
/// 进程启动协调器：进程守护、文件心跳重启等多个监控器都可能拉起同一目标进程，
/// 通过"按启动键去重 + 串行领取启动权"防止同一窗口内重复拉起导致多实例。
/// 启动键建议用目标可执行文件的完整路径（不同监控器对同一目标保持一致）。
/// </summary>
public class ProcessLaunchCoordinator
{
    /// <summary>去重窗口：窗口内同一启动键只允许一次真实拉起。</summary>
    public const int DefaultDedupSeconds = 30;

    private sealed class Entry
    {
        public SemaphoreSlim Gate = new(1, 1);
        public DateTime LastLaunchUtc = DateTime.MinValue;
    }

    /// <summary>
    /// 去重键比较规则：Windows 路径大小写不敏感，Linux 敏感。
    /// 不能用 OrdinalIgnoreCase —— 在 Linux 上 /opt/App/x 与 /opt/app/x 是两个不同的可执行文件。
    /// </summary>
    private static readonly StringComparer KeyComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(KeyComparer);

    /// <summary>
    /// 尝试领取启动权。返回 false 表示该启动键在去重窗口内刚被拉起过（或正在拉起），
    /// 调用方应跳过本次启动，避免产生第二个实例。
    /// </summary>
    public bool TryBeginLaunch(string key, int dedupSeconds = DefaultDedupSeconds)
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        entry.Gate.Wait();
        try
        {
            var now = DateTime.UtcNow;
            if (now - entry.LastLaunchUtc < TimeSpan.FromSeconds(Math.Max(1, dedupSeconds)))
                return false;

            // 先占位再启动：即使启动动作尚未完成，也能挡住并发的第二个请求
            entry.LastLaunchUtc = now;
            return true;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// 强制领取启动权（绕过去重窗口）。用于心跳重启等"已结束旧进程、接管生命周期"的场景：
    /// 杀完进程后必须立即拉起，不能被窗口挡住造成服务空窗。
    /// </summary>
    public void ForceBeginLaunch(string key)
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        entry.Gate.Wait();
        try
        {
            entry.LastLaunchUtc = DateTime.UtcNow;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// 启动键：优先用可执行文件完整路径（跨监控器一致），无路径时退回进程名。
    /// 路径不做大小写折叠——由 <see cref="KeyComparer"/> 按平台决定是否忽略大小写，
    /// 否则 Linux 上两个仅大小写不同的目标会共用同一个去重窗口。
    /// </summary>
    public static string MakeKey(string? filePath, string? processName)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            try { return "path:" + Path.GetFullPath(filePath); }
            catch { return "path:" + filePath; }
        }
        return $"name:{(processName ?? "").Trim().ToLowerInvariant()}";
    }
}
