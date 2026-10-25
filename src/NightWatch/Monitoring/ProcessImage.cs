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
    public static List<Process> FindTargets(string? cleanName, string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
            return FindByImage(Path.GetFileNameWithoutExtension(filePath), filePath);

        if (!string.IsNullOrWhiteSpace(cleanName))
            return new List<Process>(Process.GetProcessesByName(cleanName));

        return new List<Process>();
    }

    private static List<Process> FindByImage(string? enumName, string filePath)
    {
        var matches = new List<Process>();
        var candidates = string.IsNullOrWhiteSpace(enumName)
            ? Process.GetProcesses()
            : Process.GetProcessesByName(enumName);

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
