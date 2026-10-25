namespace NightWatch.Platform;

/// <summary>
/// Linux 资源采集：读 <c>/proc/stat</c> 算整机 CPU 使用率，读 <c>/proc/meminfo</c> 算内存使用率。
/// </summary>
public sealed class LinuxSystemMetrics : ISystemMetrics
{
    private (long Idle, long Total)? _lastCpuSample;

    public async Task<double?> GetCpuPercentAsync(CancellationToken ct)
    {
        try
        {
            var first = await ReadCpuSampleAsync(ct);
            await Task.Delay(500, ct);
            var second = await ReadCpuSampleAsync(ct);

            // 优先用上一轮遗留的采样做差值，避免每次采样都 sleep 后仍然只有 500ms 窗口。
            var prev = _lastCpuSample ?? first;
            _lastCpuSample = second;

            var dTotal = second.Total - prev.Total;
            var dIdle = second.Idle - prev.Idle;
            if (dTotal <= 0)
                return null;

            return 100.0 * (dTotal - dIdle) / dTotal;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public double GetMemoryUsedPercent()
    {
        try
        {
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

    /// <summary>/proc/stat 首行的 idle 与 total 采样。total 含 idle + iowait。</summary>
    internal static (long Idle, long Total) ParseCpuLine(string line)
    {
        // 形如: cpu  user nice system idle iowait irq softirq steal ...
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
        // idle 在下标 4；iowait（下标 5）与 idle 同属非忙碌时间，一并计入。
        var idle = long.Parse(parts[4]) + (parts.Length > 5 ? long.Parse(parts[5]) : 0);
        return (idle, total);
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
