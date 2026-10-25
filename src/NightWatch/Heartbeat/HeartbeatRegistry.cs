using System.Collections.Concurrent;

namespace NightWatch.Heartbeat;

/// <summary>单个 "产品/用户" 心跳的最新状态。</summary>
public class HeartbeatEntry
{
    public DateTime LastSeenUtc { get; init; }

    /// <summary>最近一次上报的来源 IP。</summary>
    public string? LastIp { get; init; }

    /// <summary>累计上报次数。</summary>
    public long Count { get; init; }
}

/// <summary>
/// 心跳注册表：内存中保存所有 "产品/用户" 键的最近上报时间。
/// 多产品多用户共用，键为 "{product}/{user}"（忽略大小写）。
/// </summary>
public class HeartbeatRegistry
{
    private readonly ConcurrentDictionary<string, HeartbeatEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 写入指定时间的心跳。供测试构造超时 / 保留期场景；生产路径请用 <see cref="Touch"/>。
    /// </summary>
    internal void Seed(string key, DateTime lastSeenUtc, string? ip, long count)
    {
        _entries[key] = new HeartbeatEntry
        {
            LastSeenUtc = lastSeenUtc,
            LastIp = ip,
            Count = count
        };
    }

    /// <summary>记录一次心跳上报（重复上报刷新时间并累加计数）。</summary>
    public void Touch(string product, string user, string? ip = null)
    {
        var key = $"{product}/{user}";
        var now = DateTime.UtcNow;
        _entries.AddOrUpdate(key,
            _ => new HeartbeatEntry { LastSeenUtc = now, LastIp = ip, Count = 1 },
            (_, entry) => new HeartbeatEntry
            {
                LastSeenUtc = now,
                LastIp = ip ?? entry.LastIp,
                Count = entry.Count + 1
            });
    }

    public IReadOnlyDictionary<string, HeartbeatEntry> Snapshot() => _entries;

    public bool IsKnown(string key) => _entries.ContainsKey(key);

    /// <summary>
    /// 清理超过保留期未上报的动态键。条目采用不可变替换，因此清理与并发心跳上报不会互相覆盖。
    /// </summary>
    public IReadOnlyList<string> RemoveStale(TimeSpan retention)
    {
        if (retention <= TimeSpan.Zero)
            return Array.Empty<string>();

        var cutoffUtc = DateTime.UtcNow - retention;
        var removed = new List<string>();
        foreach (var kv in _entries)
        {
            if (kv.Value.LastSeenUtc <= cutoffUtc &&
                ((ICollection<KeyValuePair<string, HeartbeatEntry>>)_entries).Remove(kv))
            {
                removed.Add(kv.Key);
            }
        }
        return removed;
    }
}
