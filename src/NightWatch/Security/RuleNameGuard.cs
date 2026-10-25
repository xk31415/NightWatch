using NightWatch.Models;

namespace NightWatch.Security;

/// <summary>同类规则 Name 必须非空且唯一，否则内部冷却/计时/恢复状态会串键。</summary>
public static class RuleNameGuard
{
    public static void Normalize(WatchDogOptions options, Serilog.ILogger logger)
    {
        EnsureUnique(options.Processes, r => r.Name, (r, n) => r.Name = n, "process", logger);
        EnsureUnique(options.Services, r => r.Name, (r, n) => r.Name = n, "service", logger);
        EnsureUnique(options.Files, r => r.Name, (r, n) => r.Name = n, "file", logger);
        EnsureUnique(options.FileHeartbeats, r => r.Name, (r, n) => r.Name = n, "heartbeat", logger);
    }

    public static void EnsureUnique<T>(
        IList<T> rules,
        Func<T, string> getName,
        Action<T, string> setName,
        string kind,
        Serilog.ILogger logger)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rules.Count; i++)
        {
            var original = getName(rules[i]) ?? "";
            var name = original.Trim();
            if (name.Length == 0)
                name = $"unnamed-{kind}-{i + 1}";

            var unique = name;
            var n = 2;
            while (!seen.Add(unique))
                unique = $"{name}#{n++}";

            if (!string.Equals(unique, original, StringComparison.Ordinal))
            {
                logger.Warning("规则 Name 已规范化: [{Kind}] '{Old}' -> '{New}'", kind, original, unique);
                setName(rules[i], unique);
            }
        }
    }
}
