using System.Diagnostics;
using System.Text;
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

        if (string.IsNullOrWhiteSpace(cleanName))
            return new List<Process>();

        var nameMatches = new List<Process>(byName(cleanName));

        // 同样受 comm 截断影响：只配 ProcessName 的规则（仅告警场景）在 Linux 上按全名
        // 枚举不到长名进程，会误报“不存在”。落空时回退全量扫描，按全名或 15 字符前缀匹配。
        if (nameMatches.Count == 0)
            nameMatches = FilterByName(cleanName, all());

        return nameMatches;
    }

    /// <summary>Linux 内核 /proc/&lt;pid&gt;/stat 的 comm 上限（含结尾 NUL 共 16 字节）。</summary>
    private const int LinuxCommLength = 15;

    /// <summary>
    /// 按内核 comm 规则截断进程名：comm 是 16 字节缓冲（留 1 字节 NUL，最多 15 字节），
    /// 按 UTF-8 <b>字节</b>截断而不是按 char 截断——中文等多字节名字按 char 会切错长度
    /// （例如 8 个汉字共 24 字节，char 口径不会截断，就永远匹配不上内核看到的 comm）。
    /// 截断点落在多字节字符中间时，残字节解码为 U+FFFD，与 .NET 读取 comm 的解码结果一致。
    /// </summary>
    internal static string TruncateToLinuxComm(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length <= LinuxCommLength)
            return name;

        return Encoding.UTF8.GetString(bytes, 0, LinuxCommLength);
    }

    private static List<Process> FilterByName(string name, IEnumerable<Process> candidates)
    {
        var comparison = PlatformServices.PathComparison;
        var truncated = OperatingSystem.IsLinux() ? TruncateToLinuxComm(name) : null;

        var matches = new List<Process>();
        foreach (var p in candidates)
        {
            if (MatchesName(p, name, truncated, comparison))
                matches.Add(p);
            else
                p.Dispose();
        }

        return matches;
    }

    private static bool MatchesName(Process process, string name, string? truncated, StringComparison comparison)
    {
        string? actual;
        try
        {
            actual = process.ProcessName;
        }
        catch
        {
            // 进程可能已退出；读不到名字就不认作目标
            return false;
        }

        return string.Equals(actual, name, comparison)
               || (truncated is not null && string.Equals(actual, truncated, comparison));
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
