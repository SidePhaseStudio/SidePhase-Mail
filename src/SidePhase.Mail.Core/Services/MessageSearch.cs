using System.Globalization;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Services;

/// <summary>One parsed search term: an operator (e.g. <c>from</c>) and its value, or free text.</summary>
public sealed record SearchTerm(string? Operator, string Value)
{
    /// <summary>Free text (or <c>body:</c>) that can also match the text of downloaded messages.</summary>
    public bool MatchesText => Operator is null or "body";
}

/// <summary>Extra information a search can use beyond the message summaries.</summary>
public sealed record SearchContext
{
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;

    /// <summary>Message ids whose downloaded text contains each term (from the local cache).</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<Guid>> BodyMatches { get; init; } = new Dictionary<string, IReadOnlySet<Guid>>();

    /// <summary>Messages the mail server reported as matching the whole query.</summary>
    public IReadOnlySet<Guid> ServerMatches { get; init; } = new HashSet<Guid>();
}

/// <summary>
/// Local search over cached messages. Terms are combined with AND. Operators:
/// <c>from:</c>, <c>to:</c>, <c>subject:</c>, <c>body:</c>, <c>has:attachment</c>, <c>is:unread</c>, <c>is:read</c>,
/// <c>is:starred</c>, <c>is:pinned</c>, <c>after:</c>, <c>before:</c>, <c>on:</c> (dates such as 2026-10-27),
/// <c>newer_than:</c> / <c>older_than:</c> (e.g. 7d, 2w, 3m, 1y). Quotes group words: <c>"quarterly report"</c>.
/// Free text also matches the text of messages already downloaded to this PC.
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
                if (op is "from" or "to" or "subject" or "body" or "has" or "is" or "after" or "before" or "on" or "newer_than" or "older_than")
                {
                    return new SearchTerm(op, token[(colon + 1)..]);
                }
            }

            return new SearchTerm(null, token);
        }).ToList();

    /// <summary>The words that should also be looked up in downloaded message text.</summary>
    public static IReadOnlyList<string> TextTerms(string? query) =>
        Parse(query).Where(t => t.MatchesText).Select(t => t.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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
        var body = context.BodyMatches.TryGetValue(value, out var ids) ? ids : null;
        switch (term.Operator)
        {
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
