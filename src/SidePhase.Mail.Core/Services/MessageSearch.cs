using System.Globalization;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Services;

/// <summary>One parsed search term: an operator (e.g. <c>from</c>) and its value, or free text.</summary>
public sealed record SearchTerm(string? Operator, string Value)
{
    /// <summary>Free text, <c>body:</c> or <c>filename:</c>: also looked up in the local search index.</summary>
    public bool MatchesText => Operator is null or "body" or "filename";

    /// <summary>Key used for <see cref="SearchContext.BodyMatches"/>: the value for free text, otherwise "op:value".</summary>
    public string IndexKey => Operator is null ? Value : $"{Operator}:{Value}";
}

/// <summary>Extra information a search can use beyond the message summaries.</summary>
public sealed record SearchContext
{
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;

    /// <summary>Message ids whose downloaded text contains each term (from the local cache).</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<Guid>> BodyMatches { get; init; } = new Dictionary<string, IReadOnlySet<Guid>>();

    /// <summary>Messages the mail server reported as matching the whole query.</summary>
    public IReadOnlySet<Guid> ServerMatches { get; init; } = new HashSet<Guid>();

    /// <summary>Account display names and addresses, for <c>account:</c>.</summary>
    public IReadOnlyDictionary<Guid, string> AccountNames { get; init; } = new Dictionary<Guid, string>();

    /// <summary>Folder names, for <c>folder:</c>.</summary>
    public IReadOnlyDictionary<Guid, string> FolderNames { get; init; } = new Dictionary<Guid, string>();

    /// <summary>Local categories of each message (by message id), for <c>category:</c>.</summary>
    public Func<MessageSummary, IReadOnlyCollection<string>>? CategoriesOf { get; init; }
}

/// <summary>
/// Local search over cached messages. Terms are combined with AND. Operators:
/// <c>from:</c>, <c>to:</c>, <c>subject:</c>, <c>body:</c>, <c>has:attachment</c>, <c>is:unread</c>, <c>is:read</c>,
/// <c>is:starred</c>, <c>is:pinned</c>, <c>after:</c>, <c>before:</c>, <c>on:</c> (dates such as 2026-10-27),
/// <c>newer_than:</c> / <c>older_than:</c> (e.g. 7d, 2w, 3m, 1y), <c>account:</c> (name or address), <c>in:</c>
/// (inbox, sent, archive, drafts, junk, trash), <c>folder:</c> (name), <c>larger:</c> / <c>smaller:</c> (e.g. 5mb,
/// 200kb), <c>filename:</c>, <c>filetype:</c> (pdf, image, ...), <c>category:</c>, <c>is:followup</c>.
/// Quotes group words: <c>"quarterly report"</c>. Free text also matches the text of messages already downloaded to
/// this PC (through the local search index).
/// </summary>
public static class MessageSearch
{
    public static Func<MessageSummary, bool> Compile(string? query, SearchContext? context = null)
    {
        var terms = Parse(query);
        if (terms.Count == 0) return static _ => true;
        context ??= new SearchContext();

        var predicates = terms.Select(t => CompileTerm(t, context)).ToArray();
        return m =>
        {
            foreach (var p in predicates)
            {
                if (!p(m)) return false;
            }

            return true;
        };
    }

    public static IReadOnlyList<SearchTerm> Parse(string? query) =>
        Tokenize(query).Select(token =>
        {
            var colon = token.IndexOf(':');
            if (colon > 0 && colon < token.Length - 1)
            {
                var op = token[..colon].ToLowerInvariant();
                if (op is "from" or "to" or "subject" or "body" or "has" or "is" or "after" or "before" or "on" or "newer_than" or "older_than"
                    or "account" or "in" or "folder" or "larger" or "smaller" or "filename" or "filetype" or "category")
                {
                    return new SearchTerm(op, token[(colon + 1)..]);
                }
            }

            return new SearchTerm(null, token);
        }).ToList();

    /// <summary>The terms to look up in the local search index, as <see cref="SearchTerm.IndexKey"/>s.</summary>
    public static IReadOnlyList<string> TextTerms(string? query) =>
        Parse(query).Where(t => t.MatchesText).Select(t => t.IndexKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    internal static IReadOnlyList<string> Tokenize(string? query)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return tokens;

        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in query)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                Flush();
                continue;
            }

            current.Append(c);
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length > 0) tokens.Add(current.ToString());
            current.Clear();
        }
    }

    private static Func<MessageSummary, bool> CompileTerm(SearchTerm term, SearchContext context)
    {
        var local = CompileLocalTerm(term, context);
        if (term.Operator is not (null or "from" or "to" or "subject" or "body")) return local;

        // The server searched the full message (including mail not downloaded here) for text terms.
        var server = context.ServerMatches;
        return server.Count == 0 ? local : m => server.Contains(m.Id) || local(m);
    }

    private static Func<MessageSummary, bool> CompileLocalTerm(SearchTerm term, SearchContext context)
    {
        var value = term.Value;
        var body = context.BodyMatches.TryGetValue(term.IndexKey, out var ids) ? ids : null;
        switch (term.Operator)
        {
            case "account":
                return m => context.AccountNames.TryGetValue(m.AccountId, out var name) && Contains(name, value);
            case "in" when FolderKindFor(value) is { } kind:
                return m => m.FolderKind == kind;
            case "folder":
                return m => context.FolderNames.TryGetValue(m.FolderId, out var folder) && Contains(folder, value);
            case "larger" when TryParseSize(value, out var min):
                return m => m.Size >= min;
            case "smaller" when TryParseSize(value, out var max):
                return m => m.Size > 0 && m.Size < max;
            case "filename":
                return m => m.Attachments.Any(a => Contains(a.FileName, value)) || body?.Contains(m.Id) == true;
            case "filetype":
                return m => m.Attachments.Any(a => AttachmentTypes.Matches(a, value));
            case "category":
                return m => m.Categories.Any(c => c.Equals(value, StringComparison.CurrentCultureIgnoreCase))
                            || context.CategoriesOf?.Invoke(m).Any(c => c.Equals(value, StringComparison.CurrentCultureIgnoreCase)) == true;
            case "from":
                return m => Contains(m.From.DisplayName, value) || Contains(m.From.Address, value);
            case "to":
                return m => m.To.Concat(m.Cc).Any(a => Contains(a.DisplayName, value) || Contains(a.Address, value));
            case "subject":
                return m => Contains(m.Subject, value);
            case "body":
                return m => Contains(m.Preview, value) || body?.Contains(m.Id) == true;
            case "has" when value.StartsWith("attach", StringComparison.OrdinalIgnoreCase):
                return m => m.HasAttachments;
            case "is":
                switch (value.ToLowerInvariant())
                {
                    case "unread": return m => !m.IsRead;
                    case "read": return m => m.IsRead;
                    case "starred" or "favourite" or "favorite": return m => m.IsStarred;
                    case "pinned": return m => m.IsPinned;
                    case "followup" or "follow-up": return m => m.FollowUpAt is not null;
                    case "snoozed": return m => m.IsSnoozed;
                }

                break;
            case "after" when TryParseDate(value, out var after):
                return m => m.Date.ToLocalTime().Date > after;
            case "before" when TryParseDate(value, out var before):
                return m => m.Date.ToLocalTime().Date < before;
            case "on" when TryParseDate(value, out var on):
                return m => m.Date.ToLocalTime().Date == on;
            case "newer_than" when TryParseAge(value, out var age):
                return m => m.Date >= context.Now - age;
            case "older_than" when TryParseAge(value, out var age):
                return m => m.Date < context.Now - age;
        }

        // Free text, or an operator that couldn't be understood (searched as plain text).
        var text = term.Operator is null ? value : $"{term.Operator}:{value}";
        return m => Contains(m.Subject, text)
                    || Contains(m.Preview, text)
                    || Contains(m.From.DisplayName, text)
                    || Contains(m.From.Address, text)
                    || m.To.Any(a => Contains(a.DisplayName, text) || Contains(a.Address, text))
                    || body?.Contains(m.Id) == true;
    }

    /// <summary>Accepts ISO dates (2026-10-27) and the current culture's short date format.</summary>
    public static bool TryParseDate(string value, out DateTime date)
    {
        if (DateTime.TryParseExact(value, ["yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
        {
            date = date.Date;
            return true;
        }

        return false;
    }

    private static FolderKind? FolderKindFor(string value) => value.ToLowerInvariant() switch
    {
        "inbox" => FolderKind.Inbox,
        "sent" => FolderKind.Sent,
        "archive" or "archived" => FolderKind.Archive,
        "drafts" or "draft" => FolderKind.Drafts,
        "junk" or "spam" => FolderKind.Junk,
        "trash" or "deleted" => FolderKind.Trash,
        _ => null,
    };

    /// <summary>Parses sizes such as 5mb, 200kb, 1.5m, 1000 (bytes).</summary>
    public static bool TryParseSize(string value, out long bytes)
    {
        bytes = 0;
        var v = value.Trim().ToLowerInvariant();
        long multiplier = 1;
        foreach (var (suffix, factor) in new[] { ("gb", 1L << 30), ("g", 1L << 30), ("mb", 1L << 20), ("m", 1L << 20), ("kb", 1L << 10), ("k", 1L << 10), ("b", 1L) })
        {
            if (v.EndsWith(suffix, StringComparison.Ordinal))
            {
                multiplier = factor;
                v = v[..^suffix.Length];
                break;
            }
        }

        if (!double.TryParse(v, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) || n <= 0) return false;
        bytes = (long)(n * multiplier);
        return true;
    }

    /// <summary>Parses ages such as 3d, 2w, 6m, 1y.</summary>
    public static bool TryParseAge(string value, out TimeSpan age)
    {
        age = default;
        if (value.Length < 2 || !int.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0) return false;
        age = char.ToLowerInvariant(value[^1]) switch
        {
            'd' => TimeSpan.FromDays(n),
            'w' => TimeSpan.FromDays(7 * n),
            'm' => TimeSpan.FromDays(30 * n),
            'y' => TimeSpan.FromDays(365 * n),
            _ => default,
        };
        return age > TimeSpan.Zero;
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
}
