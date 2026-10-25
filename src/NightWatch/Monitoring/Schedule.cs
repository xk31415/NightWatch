using System.Globalization;
using NightWatch.Models;

namespace NightWatch.Monitoring;

/// <summary>
/// 监控时段判定：纯函数，便于单测。按服务器本地时间计算，区间左闭右开。
/// <see cref="TimeWindow.Days"/> 指窗口“开始那天”，因此跨零点窗口（22:00-06:00）
/// 在次日凌晨仍算前一天，例如「周一 22:00-06:00」包含周二 03:00。
/// </summary>
public static class Schedule
{
    /// <summary>规则未配置时段时继承全局时段。</summary>
    public static ScheduleRule? Resolve(ScheduleRule? ruleSchedule, ScheduleRule? globalSchedule) =>
        ruleSchedule ?? globalSchedule;

    public static bool IsActive(ScheduleRule? schedule, DateTime localNow) =>
        IsActive(schedule, localNow, out _);

    /// <summary>
    /// 当前是否处于生效时段。未配置时段、Windows 为空、或所有窗口都无法解析时一律返回 true
    /// （配置写错时保持监控，避免“静默不监控”这种更危险的失败方式）。
    /// </summary>
    public static bool IsActive(ScheduleRule? schedule, DateTime localNow, out string reason)
    {
        if (schedule is null || schedule.Windows.Count == 0)
        {
            reason = "未配置时段（全天生效）";
            return true;
        }

        var parsed = new List<(TimeWindow Window, TimeSpan Start, TimeSpan End)>();
        foreach (var window in schedule.Windows)
        {
            if (TryParseTime(window.Start, out var start) && TryParseTime(window.End, out var end))
                parsed.Add((window, start, end));
        }

        if (parsed.Count == 0)
        {
            reason = "时段配置无法解析，按全天生效";
            return true;
        }

        foreach (var (window, start, end) in parsed)
        {
            if (Matches(window, start, end, localNow))
            {
                reason = $"命中 {Describe(window)}";
                return true;
            }
        }

        reason = $"当前不在 {Describe(schedule)}";
        return false;
    }

    private static bool Matches(TimeWindow window, TimeSpan start, TimeSpan end, DateTime now)
    {
        var days = ParseDays(window.Days);
        var time = now.TimeOfDay;

        if (start == end)
            return DayMatches(days, now.DayOfWeek);

        if (start < end)
            return DayMatches(days, now.DayOfWeek) && time >= start && time < end;

        // 跨零点：今天 [start, 24:00) 或昨天窗口延续到 [00:00, end)
        return (DayMatches(days, now.DayOfWeek) && time >= start)
               || (DayMatches(days, PreviousDay(now.DayOfWeek)) && time < end);
    }

    private static bool DayMatches(HashSet<DayOfWeek> days, DayOfWeek day) =>
        days.Count == 0 || days.Contains(day);

    private static DayOfWeek PreviousDay(DayOfWeek day) =>
        day == DayOfWeek.Monday ? DayOfWeek.Sunday : day - 1;

    /// <summary>解析星期列表；无法识别的项被忽略（由 Validate 上报），空集合表示每天。</summary>
    internal static HashSet<DayOfWeek> ParseDays(IEnumerable<string>? days)
    {
        var set = new HashSet<DayOfWeek>();
        if (days is null)
            return set;

        foreach (var raw in days)
        {
            if (TryParseDay(raw, out var day))
                set.Add(day);
        }

        return set;
    }

    internal static bool TryParseDay(string? text, out DayOfWeek day)
    {
        day = default;
        var s = (text ?? "").Trim();
        if (s.Length == 0)
            return false;

        // 1..7，周一 = 1
        if (int.TryParse(s, out var n) && n is >= 1 and <= 7)
        {
            day = (DayOfWeek)(n % 7);
            return true;
        }

        day = s.ToLowerInvariant() switch
        {
            "mon" or "monday" or "周一" or "星期一" => DayOfWeek.Monday,
            "tue" or "tues" or "tuesday" or "周二" or "星期二" => DayOfWeek.Tuesday,
            "wed" or "wednesday" or "周三" or "星期三" => DayOfWeek.Wednesday,
            "thu" or "thur" or "thurs" or "thursday" or "周四" or "星期四" => DayOfWeek.Thursday,
            "fri" or "friday" or "周五" or "星期五" => DayOfWeek.Friday,
            "sat" or "saturday" or "周六" or "星期六" => DayOfWeek.Saturday,
            "sun" or "sunday" or "周日" or "周天" or "星期日" or "星期天" => DayOfWeek.Sunday,
            _ => (DayOfWeek)(-1)
        };

        return (int)day >= 0;
    }

    /// <summary>解析 HH:mm（也接受 H:mm 与带秒的 HH:mm:ss）；不接受 24:00。</summary>
    internal static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        var s = (text ?? "").Trim();
        if (s.Length == 0)
            return false;

        var formats = new[] { @"hh\:mm", @"h\:mm", @"hh\:mm\:ss", @"h\:mm\:ss" };
        return TimeSpan.TryParseExact(s, formats, CultureInfo.InvariantCulture, out time)
               && time >= TimeSpan.Zero
               && time < TimeSpan.FromDays(1);
    }

    public static string Describe(ScheduleRule schedule) =>
        schedule.Windows.Count == 0
            ? "全天"
            : string.Join("; ", schedule.Windows.Select(Describe));

    public static string Describe(TimeWindow window)
    {
        var days = window.Days is { Count: > 0 } ? string.Join(",", window.Days) : "每天";
        return $"{days} {window.Start}-{window.End}";
    }

    /// <summary>启动期校验：返回配置问题（只报告，不抛异常）。</summary>
    public static List<string> Validate(WatchDogOptions options)
    {
        var problems = new List<string>();
        Check("全局", options.Schedule, problems);
        foreach (var r in options.Processes) Check($"进程规则[{r.Name}]", r.Schedule, problems);
        foreach (var r in options.Services) Check($"服务规则[{r.Name}]", r.Schedule, problems);
        foreach (var r in options.Files) Check($"文件规则[{r.Name}]", r.Schedule, problems);
        foreach (var r in options.FileHeartbeats) Check($"文件心跳规则[{r.Name}]", r.Schedule, problems);
        Check("资源监控", options.Resources.Schedule, problems);
        if (options.HttpHeartbeat is not null) Check("HTTP 心跳", options.HttpHeartbeat.Schedule, problems);
        return problems;
    }

    private static void Check(string owner, ScheduleRule? schedule, List<string> problems)
    {
        if (schedule is null)
            return;

        if (schedule.Windows.Count == 0)
            return;

        foreach (var window in schedule.Windows)
        {
            if (!TryParseTime(window.Start, out _))
                problems.Add($"{owner}: Start「{window.Start}」不是合法时间，应为 HH:mm");
            if (!TryParseTime(window.End, out _))
                problems.Add($"{owner}: End「{window.End}」不是合法时间，应为 HH:mm");

            foreach (var d in window.Days)
            {
                if (!TryParseDay(d, out _))
                    problems.Add($"{owner}: Days 里的「{d}」不是合法星期，应为 Mon..Sun 或 1..7");
            }
        }
    }
}
