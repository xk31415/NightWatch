using System.Diagnostics;
using NightWatch.Platform;

namespace NightWatch.Monitoring;

/// <summary>按可执行映像路径识别/结束进程，避免同名诱饵或误杀无关进程。</summary>
public static class ProcessImage
{
    public static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        var comparison = PlatformServices.PathComparison;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
        }
        catch
        {
            return string.Equals(a, b, comparison);
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
    /// 配置了 FilePath 时以路径为准：枚举名取可执行文件名主干，再用完整路径过滤。
    /// 不能用配置里的 ProcessName 作为枚举名——它和实际文件名不一致时（例如配置 "my-service"，文件是 myapp.exe）
    /// 会永远找不到已启动的进程，于是每个检查周期都重新拉起一次，恰好击穿“防双实例”。
    /// 未配置 FilePath 时退回进程名（仅告警场景）。
    /// </summary>
    public static List<Process> FindTargets(string? cleanName, string? filePath) =>
        FindTargets(cleanName, filePath, Process.GetProcessesByName, Process.GetProcesses);

    /// <summary>可注入进程枚举，便于测试“名字枚举落空后回退全量扫描”这条路径。</summary>
    internal static List<Process> FindTargets(string? cleanName, string? filePath,
        Func<string, Process[]> byName, Func<Process[]> all)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var enumName = Path.GetFileNameWithoutExtension(filePath);
            var matches = FilterByImage(
                string.IsNullOrWhiteSpace(enumName) ? all() : byName(enumName), filePath);

            // Linux 的进程名取自 /proc/<pid>/stat 的 comm，内核会截断到 15 字符；
            // 名字枚举落空时必须回退全量扫描，否则长名 exe 会被判定为“不存在”而每轮重复拉起。
            if (matches.Count == 0 && !string.IsNullOrWhiteSpace(enumName))
                matches = FilterByImage(all(), filePath);

            return matches;
        }

        if (!string.IsNullOrWhiteSpace(cleanName))
            return new List<Process>(byName(cleanName));

        return new List<Process>();
    }

    private static List<Process> FilterByImage(IEnumerable<Process> candidates, string filePath)
    {
        var matches = new List<Process>();
        foreach (var p in candidates)
        {
            var image = TryGetImage(p);
            if (image is not null && PathsEqual(image, filePath))
                matches.Add(p);
            else
                p.Dispose();
        }

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
