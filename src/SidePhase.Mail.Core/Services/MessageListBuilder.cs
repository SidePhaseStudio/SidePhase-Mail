using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Services;

public enum MessageFilter
{
    All,
    Unread,
    Starred,
    WithAttachments,
}

public enum MessageSort
{
    NewestFirst,
    OldestFirst,
    SenderAscending,
    SubjectAscending,
}

public enum MessageSectionKind
{
    Pinned,
    Today,
    Yesterday,
    EarlierThisWeek,
    LastWeek,
    EarlierThisMonth,
    Older,

    /// <summary>A single, untitled section used when the list is not sorted by date.</summary>
    All,
}

public sealed record MessageSection(MessageSectionKind Kind, string Title, IReadOnlyList<MessageSummary> Messages, bool IsCollapsed)
{
    public bool IsCollapsible => Kind == MessageSectionKind.Pinned;
}

public sealed record MessageListOptions
{
    public string? SearchText { get; init; }

    /// <summary>Downloaded-text and server matches for <see cref="SearchText"/>.</summary>
    public SearchContext? Search { get; init; }

    public MessageFilter Filter { get; init; } = MessageFilter.All;
    public MessageSort Sort { get; init; } = MessageSort.NewestFirst;

    /// <summary>Show pinned messages in their own section above the rest.</summary>
    public bool ShowPinnedSection { get; init; } = true;
    public bool PinnedSectionCollapsed { get; init; }

    /// <summary>Group the remaining messages by date (only applies to date sorts).</summary>
    public bool GroupByDate { get; init; } = true;

    public required DateTimeOffset Now { get; init; }

    /// <summary>First day of the week for "Earlier this week" grouping.</summary>
    public DayOfWeek FirstDayOfWeek { get; init; } = DayOfWeek.Monday;
}

/// <summary>
/// Filters, sorts and sections a set of messages for display. Pure and UI-independent.
/// </summary>
public static class MessageListBuilder
{
    public static IReadOnlyList<MessageSection> Build(IEnumerable<MessageSummary> messages, MessageListOptions options)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(options);

        var search = MessageSearch.Compile(options.SearchText, (options.Search ?? new SearchContext()) with { Now = options.Now });
        var filtered = messages.Where(m => PassesFilter(m, options.Filter) && search(m)).ToList();

        var sections = new List<MessageSection>();
        var rest = filtered;

        if (options.ShowPinnedSection)
        {
            var pinned = filtered.Where(m => m.IsPinned)
                .OrderByDescending(m => m.PinnedAt ?? m.Date)
                .ThenByDescending(m => m.Date)
                .ToList();
            if (pinned.Count > 0 && pinned.Count < filtered.Count)
            {
                sections.Add(new MessageSection(MessageSectionKind.Pinned, "Pinned", pinned, options.PinnedSectionCollapsed));
                rest = filtered.Where(m => !m.IsPinned).ToList();
            }
        }

        var sorted = Sort(rest, options.Sort);
        var isDateSort = options.Sort is MessageSort.NewestFirst or MessageSort.OldestFirst;

        if (!isDateSort || !options.GroupByDate)
        {
            if (sorted.Count > 0) sections.Add(new MessageSection(MessageSectionKind.All, string.Empty, sorted, false));
            return sections;
        }

        foreach (var group in sorted.GroupBy(m => Bucket(m.Date, options.Now, options.FirstDayOfWeek)))
        {
            sections.Add(new MessageSection(group.Key, TitleFor(group.Key), group.ToList(), false));
        }

        return sections;
    }

    public static bool PassesFilter(MessageSummary m, MessageFilter filter) => filter switch
    {
        MessageFilter.Unread => !m.IsRead,
        MessageFilter.Starred => m.IsStarred,
        MessageFilter.WithAttachments => m.HasAttachments,
        _ => true,
    };

    public static List<MessageSummary> Sort(IEnumerable<MessageSummary> messages, MessageSort sort) => sort switch
    {
        MessageSort.OldestFirst => messages.OrderBy(m => m.Date).ToList(),
        MessageSort.SenderAscending => messages
            .OrderBy(m => m.From.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(m => m.Date).ToList(),
        MessageSort.SubjectAscending => messages
            .OrderBy(m => StripPrefixes(m.Subject), StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(m => m.Date).ToList(),
        _ => messages.OrderByDescending(m => m.Date).ToList(),
    };

    public static MessageSectionKind Bucket(DateTimeOffset date, DateTimeOffset now, DayOfWeek firstDayOfWeek = DayOfWeek.Monday)
    {
        var day = date.ToOffset(now.Offset).Date;
        var today = now.Date;
        if (day >= today) return MessageSectionKind.Today;
        if (day == today.AddDays(-1)) return MessageSectionKind.Yesterday;

        var startOfWeek = today.AddDays(-(((int)today.DayOfWeek - (int)firstDayOfWeek + 7) % 7));
        if (day >= startOfWeek) return MessageSectionKind.EarlierThisWeek;
        if (day >= startOfWeek.AddDays(-7)) return MessageSectionKind.LastWeek;
        if (day.Year == today.Year && day.Month == today.Month) return MessageSectionKind.EarlierThisMonth;
        return MessageSectionKind.Older;
    }

    public static string TitleFor(MessageSectionKind kind) => kind switch
    {
        MessageSectionKind.Pinned => "Pinned",
        MessageSectionKind.Today => "Today",
        MessageSectionKind.Yesterday => "Yesterday",
        MessageSectionKind.EarlierThisWeek => "Earlier this week",
        MessageSectionKind.LastWeek => "Last week",
        MessageSectionKind.EarlierThisMonth => "Earlier this month",
        MessageSectionKind.Older => "Older",
        _ => string.Empty,
    };

    internal static string StripPrefixes(string subject)
    {
        var s = subject.TrimStart();
        while (true)
        {
            var next = Composition.SubjectPrefixes.Strip(s);
            if (next == s) return s;
            s = next;
        }
    }
}
