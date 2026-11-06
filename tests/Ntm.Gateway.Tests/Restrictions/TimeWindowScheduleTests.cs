using Ntm.Gateway.Restrictions;

namespace Ntm.Gateway.Tests.Restrictions;

/// <summary>可用时段（Q81）：星期几 + [开始, 结束)，开始大于结束是跨午夜、属于开始那天，开始等于结束是全天。</summary>
public sealed class TimeWindowScheduleTests
{
    private const int Weekdays = (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Tuesday)
        | (1 << (int)DayOfWeek.Wednesday) | (1 << (int)DayOfWeek.Thursday) | (1 << (int)DayOfWeek.Friday);

    private const int Friday = 1 << (int)DayOfWeek.Friday;

    // 2026-10-30 是周五。
    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0);

    [Fact]
    public void NoWindowsMeansAlwaysOpen()
    {
        Assert.True(TimeWindowSchedule.IsOpen([], At(31, 3)));
    }

    [Fact]
    public void ADaytimeWindowIsOpenOnlyInsideItsHoursOnItsDays()
    {
        TimeWindow[] windows = [new(Weekdays, 9 * 60, 18 * 60)];

        Assert.True(TimeWindowSchedule.IsOpen(windows, At(30, 9)));
        Assert.True(TimeWindowSchedule.IsOpen(windows, At(30, 17, 59)));

        // 结束那一分钟不算在内。
        Assert.False(TimeWindowSchedule.IsOpen(windows, At(30, 18)));
        Assert.False(TimeWindowSchedule.IsOpen(windows, At(30, 8, 59)));

        // 周六不在星期里。
        Assert.False(TimeWindowSchedule.IsOpen(windows, At(31, 12)));
    }

    [Fact]
    public void ACrossMidnightWindowBelongsToTheDayItStarts()
    {
        // 周五 22:00 到周六 06:00。
        TimeWindow[] windows = [new(Friday, 22 * 60, 6 * 60)];

        Assert.True(TimeWindowSchedule.IsOpen(windows, At(30, 23)));
        Assert.True(TimeWindowSchedule.IsOpen(windows, At(31, 5, 59)));
        Assert.False(TimeWindowSchedule.IsOpen(windows, At(31, 6)));

        // 周五凌晨属于周四开始的那一段，而周四没选。
        Assert.False(TimeWindowSchedule.IsOpen(windows, At(30, 2)));
    }

    [Fact]
    public void EqualStartAndEndMeansTheWholeDay()
    {
        TimeWindow[] windows = [new(Friday, 0, 0)];

        Assert.True(TimeWindowSchedule.IsOpen(windows, At(30, 0)));
        Assert.True(TimeWindowSchedule.IsOpen(windows, At(30, 23, 59)));
        Assert.False(TimeWindowSchedule.IsOpen(windows, At(31, 0)));
    }

    [Fact]
    public void NextOpenIsTheEarliestStartAfterNow()
    {
        TimeWindow[] windows = [new(Weekdays, 9 * 60, 18 * 60)];

        // 周五 20:00 关着，下一次是周一 09:00。
        Assert.Equal(At(30, 20).Date.AddDays(3).AddHours(9), TimeWindowSchedule.NextOpen(windows, At(30, 20)));

        // 周五 07:00 关着，当天 09:00 就开。
        Assert.Equal(At(30, 9), TimeWindowSchedule.NextOpen(windows, At(30, 7)));
    }

    [Fact]
    public void NextOpenWrapsToTheSameWeekdayNextWeek()
    {
        TimeWindow[] windows = [new(Friday, 9 * 60, 10 * 60)];

        Assert.Equal(At(30, 9).AddDays(7), TimeWindowSchedule.NextOpen(windows, At(30, 11)));
    }

    [Fact]
    public void NextOpenIsNullWhenNoDayIsSelected()
    {
        Assert.Null(TimeWindowSchedule.NextOpen([new TimeWindow(0, 9 * 60, 10 * 60)], At(30, 11)));
    }
}
