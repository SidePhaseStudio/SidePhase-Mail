using System.Globalization;

namespace SidePhase.Mail.Core.Formatting;

public static class DateDisplayFormatter
{
    /// <summary>Compact date for message lists: time today, "Yesterday", weekday this week, then a date.</summary>
    public static string ForList(DateTimeOffset date, DateTimeOffset now, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var local = date.ToOffset(now.Offset);
        var day = local.Date;
        var today = now.Date;

        if (day == today) return local.ToString("t", culture);
        if (day == today.AddDays(-1)) return "Yesterday";
        if (day > today.AddDays(-7) && day < today) return local.ToString("ddd", culture);
        if (day.Year == today.Year) return local.ToString(MonthDayPattern(culture), culture);
        return local.ToString("d", culture);
    }

    /// <summary>Full date for the reading pane, e.g. "Tuesday 27 October 2026, 10:42".</summary>
    public static string ForReadingPane(DateTimeOffset date, DateTimeOffset now, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var local = date.ToOffset(now.Offset);
        return $"{local.ToString("D", culture)}, {local.ToString("t", culture)}";
    }

    /// <summary>"12 Mar" / "Mar 12" depending on the culture's day/month order.</summary>
    internal static string MonthDayPattern(CultureInfo culture)
    {
        var pattern = culture.DateTimeFormat.ShortDatePattern;
        var dayIndex = pattern.IndexOf('d');
        var monthIndex = pattern.IndexOf('M');
        return dayIndex >= 0 && monthIndex >= 0 && monthIndex < dayIndex ? "MMM d" : "d MMM";
    }
}

public static class FileSizeFormatter
{
    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value >= 100
            ? $"{value.ToString("0", culture)} {units[unit]}"
            : $"{value.ToString("0.#", culture)} {units[unit]}";
    }
}
