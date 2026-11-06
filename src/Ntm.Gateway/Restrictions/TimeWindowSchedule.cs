using Ntm.Gateway.Configuration;

namespace Ntm.Gateway.Restrictions;

/// <summary>可用时段的判断（Q81）。时段是网关时区里的墙上时间。</summary>
public static class TimeWindowSchedule
{
    public const int MinutesPerDay = 24 * 60;

    /// <summary>所有星期几都选上的位掩码。</summary>
    public const int AllWeekdays = 0b111_1111;

    /// <summary>此刻在不在某一段里。没有时段表示随时可用。</summary>
    public static bool IsOpen(IReadOnlyList<TimeWindow> windows, DateTime local)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count == 0)
        {
            return true;
        }

        int minute = (local.Hour * 60) + local.Minute;
        DayOfWeek today = local.DayOfWeek;
        DayOfWeek yesterday = (DayOfWeek)(((int)today + 6) % 7);

        foreach (TimeWindow window in windows)
        {
            if (window.StartMinute == window.EndMinute)
            {
                if (window.Covers(today))
                {
                    return true;
                }
            }
            else if (window.StartMinute < window.EndMinute)
            {
                if (window.Covers(today) && minute >= window.StartMinute && minute < window.EndMinute)
                {
                    return true;
                }
            }
            else
            {
                // 跨午夜：属于开始那天。今天开始的晚段，或者昨天开始、延续到今天凌晨的那一段。
                if ((window.Covers(today) && minute >= window.StartMinute)
                    || (window.Covers(yesterday) && minute < window.EndMinute))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 从 <paramref name="local"/> 之后最早哪个时刻开始可用（墙上时间）。只在当前不可用时有意义；
    /// 时段都没选星期几时返回 null。
    /// </summary>
    public static DateTime? NextOpen(IReadOnlyList<TimeWindow> windows, DateTime local)
    {
        ArgumentNullException.ThrowIfNull(windows);
        DateTime? best = null;
        DateTime today = local.Date;

        // 一周之内一定轮到每个星期几；多看一天覆盖「今天这段已经过了，下周今天才再开」。
        for (int day = 0; day <= 7; day++)
        {
            DateTime date = today.AddDays(day);
            foreach (TimeWindow window in windows)
            {
                if (!window.Covers(date.DayOfWeek))
                {
                    continue;
                }

                DateTime start = date.AddMinutes(window.StartMinute == window.EndMinute ? 0 : window.StartMinute);
                if (start > local && (best is null || start < best))
                {
                    best = start;
                }
            }

            if (best is not null)
            {
                return best;
            }
        }

        return best;
    }
}

/// <summary>快照里的一段可用时段，见 <see cref="ChannelTimeWindow"/>。</summary>
public readonly record struct TimeWindow(int Weekdays, int StartMinute, int EndMinute)
{
    public bool Covers(DayOfWeek day) => (Weekdays & (1 << (int)day)) != 0;
}
