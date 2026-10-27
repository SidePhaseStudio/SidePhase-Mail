using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Services;

/// <summary>
/// Simple local search. Terms are combined with AND. Supported operators:
/// <c>from:</c>, <c>to:</c>, <c>subject:</c>, <c>has:attachment</c>, <c>is:unread</c>, <c>is:read</c>,
/// <c>is:starred</c>, <c>is:pinned</c>. Quotes group words: <c>"quarterly report"</c>.
/// </summary>
public static class MessageSearch
{
    public static Func<MessageSummary, bool> Compile(string? query)
    {
        var terms = Tokenize(query);
        if (terms.Count == 0) return static _ => true;

        var predicates = terms.Select(CompileTerm).ToArray();
        return m =>
        {
            foreach (var p in predicates)
            {
                if (!p(m)) return false;
            }

            return true;
        };
    }

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

    private static Func<MessageSummary, bool> CompileTerm(string term)
    {
        var colon = term.IndexOf(':');
        if (colon > 0 && colon < term.Length - 1)
        {
            var op = term[..colon].ToLowerInvariant();
            var value = term[(colon + 1)..];
            switch (op)
            {
                case "from":
                    return m => Contains(m.From.DisplayName, value) || Contains(m.From.Address, value);
                case "to":
                    return m => m.To.Concat(m.Cc).Any(a => Contains(a.DisplayName, value) || Contains(a.Address, value));
                case "subject":
                    return m => Contains(m.Subject, value);
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
            }
        }

        return m => Contains(m.Subject, term)
                    || Contains(m.Preview, term)
                    || Contains(m.From.DisplayName, term)
                    || Contains(m.From.Address, term)
                    || m.To.Any(a => Contains(a.DisplayName, term) || Contains(a.Address, term));
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
}
