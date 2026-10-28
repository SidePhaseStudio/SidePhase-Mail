using System.Text;

namespace SidePhase.Mail.Core.Services;

/// <summary>
/// Turns a search term into an SQLite FTS5 query. Every word is quoted (so FTS operators typed by the user are
/// treated as text) and matched as a prefix, so "invo" finds "invoice". Returns null when there's nothing to search.
/// </summary>
public static class SearchIndexQuery
{
    public static string? For(SearchTerm term)
    {
        var words = Words(term.Value);
        if (words.Count == 0) return null;
        var expression = string.Join(' ', words.Select(w => "\"" + w + "\"*"));
        return term.Operator switch
        {
            "body" => "body : (" + expression + ")",
            "filename" => "attachments : (" + expression + ")",
            _ => expression,
        };
    }

    /// <summary>Parses an <see cref="SearchTerm.IndexKey"/> back into a term.</summary>
    public static SearchTerm FromKey(string key)
    {
        var colon = key.IndexOf(':');
        if (colon > 0)
        {
            var op = key[..colon];
            if (op is "body" or "filename") return new SearchTerm(op, key[(colon + 1)..]);
        }

        return new SearchTerm(null, key);
    }

    /// <summary>Letters and digits only; everything else separates words (as the FTS tokenizer does).</summary>
    internal static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0) words.Add(current.ToString());
        return words.Take(16).ToList();
    }
}
