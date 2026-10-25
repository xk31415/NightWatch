using NightWatch.Models;
using NightWatch.Monitoring;
using Xunit;

namespace NightWatch.Tests;

public class ScheduleTests
{
    private static ScheduleRule Windows(params (string Start, string End, string[] Days)[] windows) => new()
    {
        Windows = windows
            .Select(w => new TimeWindow { Start = w.Start, End = w.End, Days = [.. w.Days] })
            .ToList()
    };

    private static ScheduleRule Single(string start, string end, params string[] days) =>
        Windows((start, end, days));

    /// <summary>构造指定星期几的本地时间（基准日期任意，按 DayOfWeek 对齐）。</summary>
    private static DateTime At(DayOfWeek day, int hour, int minute = 0)
    {
        var d = new DateTime(2026, 1, 5); // 周一
        while (d.DayOfWeek != day)
            d = d.AddDays(1);
        return d.AddHours(hour).AddMinutes(minute);
    }

    [Fact]
    public void NoSchedule_IsAlwaysActive()
    {
        Assert.True(Schedule.IsActive(null, At(DayOfWeek.Monday, 10)));
        Assert.True(Schedule.IsActive(new ScheduleRule(), At(DayOfWeek.Monday, 10)));
    }

    [Fact]
    public void InsideWindow_IsActive()
    {
        var schedule = Single("09:00", "18:00");
        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 10)));
    }

    [Fact]
    public void BoundariesAreStartInclusive_EndExclusive()
    {
        var schedule = Single("09:00", "18:00");

        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 9, 0)));
        Assert.False(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 8, 59)));
        Assert.False(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 18, 0)));
        Assert.False(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 18, 1)));
    }

    [Fact]
    public void DayFilter_OnlyListedDaysAreActive()
    {
        var schedule = Single("09:00", "18:00", "Mon");

        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 10)));
        Assert.False(Schedule.IsActive(schedule, At(DayOfWeek.Tuesday, 10)));
    }

    [Theory]
    [InlineData("Mon")]
    [InlineData("monday")]
    [InlineData("1")]
    [InlineData("周一")]
    public void DayNames_AreAccepted(string day)
    {
        var schedule = Single("09:00", "18:00", day);
        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 10)));
    }

    [Theory]
    [InlineData(1, DayOfWeek.Monday)]
    [InlineData(5, DayOfWeek.Friday)]
    [InlineData(7, DayOfWeek.Sunday)]
    public void DayNumbers_MapToDayOfWeek(int dayNumber, DayOfWeek expected)
    {
        var schedule = Single("00:00", "23:59", dayNumber.ToString());
        Assert.True(Schedule.IsActive(schedule, At(expected, 10)));
    }

    [Fact]
    public void OvernightWindow_WrapsToNextMorning()
    {
        var schedule = Single("22:00", "06:00");

        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 23)));
        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Tuesday, 3)));
        Assert.False(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 12)));
        Assert.False(Schedule.IsActive(schedule, At(DayOfWeek.Tuesday, 6, 0)));
    }

    [Fact]
    public void OvernightWindow_DaysReferToStartDay()
    {
        var schedule = Single("22:00", "06:00", "Mon");

        // 周一 23:00 生效
        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 23)));
        // 周二 03:00 仍属于「周一 22:00-06:00」
        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Tuesday, 3)));
        // 周三 03:00 不属于
        Assert.False(Schedule.IsActive(schedule, At(DayOfWeek.Wednesday, 3)));
        // 周二 23:00 不是周一的窗口
        Assert.False(Schedule.IsActive(schedule, At(DayOfWeek.Tuesday, 23)));
    }

    [Fact]
    public void StartEqualsEnd_MeansAllDay()
    {
        var schedule = Single("00:00", "00:00", "Sat");

        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Saturday, 0)));
        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Saturday, 23, 59)));
        Assert.False(Schedule.IsActive(schedule, At(DayOfWeek.Sunday, 12)));
    }

    /// <summary>典型场景：只在 A 股开盘时段监控（09:30-11:30、13:00-15:00，工作日）。</summary>
    [Fact]
    public void AShareTradingSessions_OnlyActiveDuringMarketHours()
    {
        var schedule = Windows(
            ("09:30", "11:30", new[] { "Mon", "Tue", "Wed", "Thu", "Fri" }),
            ("13:00", "15:00", new[] { "Mon", "Tue", "Wed", "Thu", "Fri" }));

        Assert.False(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 9, 29)));
        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 9, 30)));
        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 11, 29)));
        Assert.False(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 11, 30)));
        Assert.False(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 12, 0)));
        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Friday, 13, 0)));
        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Friday, 14, 59)));
        Assert.False(Schedule.IsActive(schedule, At(DayOfWeek.Friday, 15, 0)));
        Assert.False(Schedule.IsActive(schedule, At(DayOfWeek.Saturday, 10, 0)));
    }

    [Fact]
    public void MultipleWindows_AnyMatchIsActive()
    {
        var schedule = Windows(("09:00", "12:00", []), ("14:00", "18:00", []));

        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 10)));
        Assert.False(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 13)));
        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 15)));
    }

    [Fact]
    public void InvalidTime_FallsBackToAlwaysActive()
    {
        var schedule = Single("25:99", "18:00");
        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 3)));
    }

    [Fact]
    public void PartiallyInvalidWindow_StillWorksWithValidOne()
    {
        var schedule = Windows(("25:99", "26:00", []), ("09:00", "18:00", []));

        Assert.True(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 10)));
        Assert.False(Schedule.IsActive(schedule, At(DayOfWeek.Monday, 20)));
    }

    [Fact]
    public void Describe_MentionsWindowAndDays()
    {
        var text = Schedule.Describe(Single("22:00", "06:00", "Mon", "Tue"));

        Assert.Contains("22:00-06:00", text);
        Assert.Contains("Mon", text);
    }

    [Fact]
    public void Validate_ReportsBadTimeAndDay()
    {
        var options = new WatchDogOptions
        {
            Processes = [new ProcessRule { Name = "p", Schedule = Single("9点", "18:00", "Funday") }]
        };

        var problems = Schedule.Validate(options);

        Assert.Contains(problems, p => p.Contains("Start"));
        Assert.Contains(problems, p => p.Contains("Days"));
    }

    [Fact]
    public void Validate_AcceptsValidConfig()
    {
        var options = new WatchDogOptions
        {
            Schedule = Single("08:30", "18:30", "Mon", "Tue"),
            Processes = [new ProcessRule { Name = "p", Schedule = Single("0:00", "23:59", "周日") }]
        };

        Assert.Empty(Schedule.Validate(options));
    }

    [Fact]
    public void Resolve_PrefersRuleSchedule()
    {
        var global = Single("09:00", "18:00");
        var rule = Single("00:00", "23:59");

        Assert.Same(rule, Schedule.Resolve(rule, global));
        Assert.Same(global, Schedule.Resolve(null, global));
        Assert.Null(Schedule.Resolve(null, null));
    }
}
