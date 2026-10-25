namespace NightWatch.Platform;

/// <summary>
/// Linux 资源采集：读 <c>/proc/stat</c> 算整机 CPU 使用率，读 <c>/proc/meminfo</c> 算内存使用率。
/// </summary>
public sealed class LinuxSystemMetrics : ISystemMetrics
{
    private (long Idle, long Total)? _lastCpuSample;

    /// <summary>首次调用（没有历史采样）时的采样窗口。</summary>
    internal static readonly TimeSpan FirstSampleWindow = TimeSpan.FromMilliseconds(500);

    public async Task<double?> GetCpuPercentAsync(CancellationToken ct)
    {
        // 已取消的令牌直接抛出，不必先读 /proc，也让取消语义可测
        ct.ThrowIfCancellationRequested();

        try
        {
            var current = await ReadCpuSampleAsync(ct);
            var previous = _lastCpuSample;

            if (previous is null)
            {
                // 首次调用没有历史采样，只能等一个短窗口再采一次
                await Task.Delay(FirstSampleWindow, ct);
                previous = current;
                current = await ReadCpuSampleAsync(ct);
            }

            _lastCpuSample = current;
            return ComputeCpuPercent(previous.Value, current);
        }
        catch (OperationCanceledException)
        {
            // 停机取消必须向上抛，否则关闭流程会被当成“采集失败”吞掉
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 两次 /proc/stat 采样求整机 CPU 使用率。采样间隔为 0（计数未变化）时返回 null；
    /// 计数器回绕导致 idle 减少时按 0 空闲处理，结果夹在 [0,100]。
    /// </summary>
    internal static double? ComputeCpuPercent((long Idle, long Total) previous, (long Idle, long Total) current)
    {
        var deltaTotal = current.Total - previous.Total;
        if (deltaTotal <= 0)
            return null;

        var deltaIdle = current.Idle - previous.Idle;
        var busy = Math.Clamp(deltaTotal - deltaIdle, 0, deltaTotal);
        return 100.0 * busy / deltaTotal;
    }

    public double GetMemoryUsedPercent()
    {
        try
        {
            // 容器里 /proc/meminfo 是宿主机口径；cgroup v2 有限额时以限额为准。
            var cgroup = ReadCgroupMemoryUsedPercent();
            if (cgroup is not null)
                return cgroup.Value;

            var map = ReadMemInfo();
            if (!map.TryGetValue("MemTotal", out var total) || total <= 0)
                return 0;

            // MemAvailable 是内核对「可回收+空闲」的估算，比 MemFree 更贴近真实可用量。
            if (map.TryGetValue("MemAvailable", out var available))
                return 100.0 * (total - available) / total;

            var free = map.GetValueOrDefault("MemFree");
            var buffers = map.GetValueOrDefault("Buffers");
            var cached = map.GetValueOrDefault("Cached");
            return 100.0 * (total - free - buffers - cached) / total;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>/proc/stat 首行的 idle 与 total 采样。idle 含 iowait；total 不含 guest/guest_nice。</summary>
    internal static (long Idle, long Total) ParseCpuLine(string line)
    {
        // 形如: cpu  user nice system idle iowait irq softirq steal guest guest_nice
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || parts[0] != "cpu")
            throw new FormatException($"/proc/stat 首行格式异常: {line}");

        long Sum(int from)
        {
            long sum = 0;
            for (var i = from; i < parts.Length; i++)
                sum += long.Parse(parts[i]);
            return sum;
        }

        var total = Sum(1);

        // 内核把 guest / guest_nice 的时间已经算进 user / nice，重复相加会让 total 偏大、
        // CPU 使用率偏低。它们在尾部两个字段（下标 9、10），有就减掉。
        if (parts.Length > 9)
            total -= long.Parse(parts[9]);
        if (parts.Length > 10)
            total -= long.Parse(parts[10]);

        // idle 在下标 4；iowait（下标 5）与 idle 同属非忙碌时间，一并计入。
        var idle = long.Parse(parts[4]) + (parts.Length > 5 ? long.Parse(parts[5]) : 0);
        return (idle, total);
    }

    /// <summary>
    /// cgroup v2 内存使用率：/sys/fs/cgroup/memory.current ÷ memory.max。
    /// memory.max 为 "max"（无限额）或读不到时返回 null，回退到 /proc/meminfo。
    /// </summary>
    internal static double? ComputeCgroupMemoryPercent(string currentText, string maxText)
    {
        var max = (maxText ?? "").Trim();
        if (max.Length == 0 || max == "max")
            return null;

        if (!long.TryParse(max, out var limit) || limit <= 0)
            return null;
        if (!long.TryParse((currentText ?? "").Trim(), out var usage) || usage < 0)
            return null;

        return Math.Clamp(100.0 * usage / limit, 0, 100);
    }

    private static double? ReadCgroupMemoryUsedPercent()
    {
        const string currentPath = "/sys/fs/cgroup/memory.current";
        const string maxPath = "/sys/fs/cgroup/memory.max";
        try
        {
            if (!File.Exists(currentPath) || !File.Exists(maxPath))
                return null;

            return ComputeCgroupMemoryPercent(File.ReadAllText(currentPath), File.ReadAllText(maxPath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析 /proc/meminfo，值统一为 kB。</summary>
    internal static Dictionary<string, long> ParseMemInfo(string content)
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var raw in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var key = line[..colon].Trim();
            var rest = line[(colon + 1)..].Trim();
            var space = rest.IndexOf(' ');
            var number = space > 0 ? rest[..space] : rest;
            if (long.TryParse(number, out var kb))
                map[key] = kb;
        }

        return map;
    }

    private static async Task<(long Idle, long Total)> ReadCpuSampleAsync(CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync("/proc/stat", ct);
        var firstLine = text.Split('\n')[0];
        return ParseCpuLine(firstLine);
    }

    private static Dictionary<string, long> ReadMemInfo() =>
        ParseMemInfo(File.ReadAllText("/proc/meminfo"));

    public void Dispose()
    {
        // 无非托管资源
    }
}
