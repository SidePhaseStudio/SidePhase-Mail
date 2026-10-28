using System.Globalization;

namespace SidePhase.Mail.Core.Scheduling;

/// <summary>Named times offered in Snooze, Follow-up and Schedule send menus.</summary>
public enum QuickTime
{
    /// <summary>A few hours from now, on the hour.</summary>
    LaterToday,

    /// <summary>Tomorrow at 08:00.</summary>
    TomorrowMorning,

    /// <summary>Saturday at 09:00 (Sunday if it's Saturday; next Saturday if it's Sunday).</summary>
    ThisWeekend,

    /// <summary>Next Monday at 08:00.</summary>
    NextWeek,

    /// <summary>Tomorrow at 09:00.</summary>
    Tomorrow,

    /// <summary>Three days from now at 09:00.</summary>
    InThreeDays,
}

/// <summary>
/// Turns named times and wall-clock times into exact instants in the user's time zone. Times that don't exist
/// (the hour skipped when clocks go forward) move forward by the gap; times that happen twice (the hour repeated when
/// clocks go back) use the first occurrence. Everything is stored in UTC.
/// </summary>
public static class QuickTimes
{
    public static DateTimeOffset Resolve(QuickTime preset, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        return preset switch
        {
            QuickTime.LaterToday => LaterToday(now, zone),
            QuickTime.TomorrowMorning => FromLocal(today.AddDays(1), new TimeOnly(8, 0), zone),
            QuickTime.ThisWeekend => FromLocal(NextWeekendDay(today), new TimeOnly(9, 0), zone),
            QuickTime.NextWeek => FromLocal(NextMonday(today), new TimeOnly(8, 0), zone),
            QuickTime.Tomorrow => FromLocal(today.AddDays(1), new TimeOnly(9, 0), zone),
            QuickTime.InThreeDays => FromLocal(today.AddDays(3), new TimeOnly(9, 0), zone),
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
    }

    /// <summary>Converts a date and wall-clock time in <paramref name="zone"/> to an exact instant.</summary>
    public static DateTimeOffset FromLocal(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var wall = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall))
        {
            // The clock skips this time (spring forward): use the same wall time plus the size of the gap.
            var gap = GapAt(wall, zone);
            wall = wall + gap;
        }

        TimeSpan offset;
        if (zone.IsAmbiguousTime(wall))
        {
            // The clock shows this time twice (fall back): use the first occurrence (the larger offset).
            offset = zone.GetAmbiguousTimeOffsets(wall).Max();
        }
        else
        {
            offset = zone.GetUtcOffset(wall);
        }

        return new DateTimeOffset(wall, offset).ToUniversalTime();
    }

    /// <summary>Label such as "Tomorrow 08:00" or "Sat 9 Nov 09:00", in local time.</summary>
    public static string Describe(DateTimeOffset when, DateTimeOffset now, TimeZoneInfo zone, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var local = TimeZoneInfo.ConvertTime(when, zone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var day = DateOnly.FromDateTime(local.DateTime);
        var time = local.ToString("t", culture);
        if (day == today) return $"Today {time}";
        if (day == today.AddDays(1)) return $"Tomorrow {time}";
        if (day > today && day < today.AddDays(7)) return $"{local.ToString("dddd", culture)} {time}";
        return $"{local.ToString("ddd d MMM", culture)} {time}";
    }

    private static DateTimeOffset LaterToday(DateTimeOffset now, TimeZoneInfo zone)
    {
        // Three hours from now, rounded up to the next whole hour.
        var target = now.ToUniversalTime().AddHours(3);
        var rounded = new DateTimeOffset(target.Year, target.Month, target.Day, target.Hour, 0, 0, TimeSpan.Zero);
        if (rounded < target) rounded = rounded.AddHours(1);
        return rounded;
    }

    private static DateOnly NextWeekendDay(DateOnly today) => today.DayOfWeek switch
    {
        DayOfWeek.Saturday => today.AddDays(1),
        DayOfWeek.Sunday => today.AddDays(6),
        _ => today.AddDays(((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7),
    };

    private static DateOnly NextMonday(DateOnly today)
    {
        var days = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(days == 0 ? 7 : days);
    }

    private static TimeSpan GapAt(DateTime wall, TimeZoneInfo zone)
    {
        var before = zone.GetUtcOffset(wall.AddHours(-3));
        var after = zone.GetUtcOffset(wall.AddHours(3));
        var gap = after - before;
        return gap > TimeSpan.Zero ? gap : TimeSpan.FromHours(1);
    }
}
