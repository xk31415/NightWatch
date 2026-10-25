using System.Diagnostics;

namespace NightWatch.Monitoring;

/// <summary>按可执行映像路径识别/结束进程，避免同名诱饵或误杀无关进程。</summary>
public static class ProcessImage
{
    public static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static string? TryGetImage(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 配置了 FilePath 时只收映像路径匹配的进程；读不到 MainModule 的同名进程不当作目标。
    /// 未配置 FilePath 时退回进程名（仅告警场景）。
    /// </summary>
    public static List<Process> FindTargets(string? cleanName, string? filePath)
    {
        var matches = new List<Process>();
        if (!string.IsNullOrWhiteSpace(filePath) && !string.IsNullOrWhiteSpace(cleanName))
        {
            foreach (var p in Process.GetProcessesByName(cleanName))
            {
                var image = TryGetImage(p);
                if (image is not null && PathsEqual(image, filePath))
                    matches.Add(p);
                else
                    p.Dispose();
            }

            return matches;
        }

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            foreach (var p in Process.GetProcesses())
            {
                var image = TryGetImage(p);
                if (image is not null && PathsEqual(image, filePath))
                    matches.Add(p);
                else
                    p.Dispose();
            }

            return matches;
        }

        if (!string.IsNullOrWhiteSpace(cleanName))
            matches.AddRange(Process.GetProcessesByName(cleanName));

        return matches;
    }

    public static bool IsRunning(string? cleanName, string? filePath)
    {
        var found = FindTargets(cleanName, filePath);
        foreach (var p in found)
            p.Dispose();
        return found.Count > 0;
    }

    public static int KillTargets(string? cleanName, string? filePath, ILogger logger, int waitMs = 5000)
    {
        var killed = 0;
        foreach (var p in FindTargets(cleanName, filePath))
        {
            using (p)
            {
                logger.LogWarning("结束进程 {Name} (PID {Pid}, {Image})",
                    cleanName, p.Id, TryGetImage(p) ?? filePath);
                try
                {
                    p.Kill(entireProcessTree: true);
                    if (!p.WaitForExit(waitMs))
                        logger.LogWarning("进程 PID {Pid} 在 {Wait}ms 内未退出", p.Id, waitMs);
                    killed++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "结束 PID {Pid} 失败", p.Id);
                }
            }
        }

        return killed;
    }
}
