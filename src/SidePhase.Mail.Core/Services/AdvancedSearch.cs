using System.Globalization;
using System.Text;

namespace SidePhase.Mail.Core.Services;

/// <summary>
/// The fields of the advanced search form. It's just another way of writing a search query, so the form and the
/// search box always agree: <see cref="ToQuery"/> writes operators and <see cref="FromQuery"/> reads them back.
/// </summary>
public sealed record AdvancedSearch
{
    public string Words { get; init; } = string.Empty;
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string Account { get; init; } = string.Empty;

    /// <summary>inbox, sent, archive, drafts, junk, trash, or empty for any.</summary>
    public string In { get; init; } = string.Empty;

    public DateTime? After { get; init; }
    public DateTime? Before { get; init; }
    public bool HasAttachment { get; init; }
    public bool Unread { get; init; }
    public bool Favourite { get; init; }
    public bool Pinned { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string FileType { get; init; } = string.Empty;

    /// <summary>e.g. "5mb".</summary>
    public string LargerThan { get; init; } = string.Empty;

    public string ToQuery()
    {
        var sb = new StringBuilder();
        void Add(string op, string value)
        {
            value = value.Trim();
            if (value.Length == 0) return;
            if (sb.Length > 0) sb.Append(' ');
            if (op.Length > 0) sb.Append(op).Append(':');
            sb.Append(value.Contains(' ') ? "\"" + value.Replace("\"", string.Empty) + "\"" : value);
        }

        foreach (var word in MessageSearch.Tokenize(Words)) Add(string.Empty, word);
        Add("from", From);
        Add("to", To);
        Add("subject", Subject);
        Add("body", Body);
        Add("account", Account);
        Add("in", In);
        if (After is { } after) Add("after", after.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (Before is { } before) Add("before", before.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (HasAttachment) Add("has", "attachment");
        if (Unread) Add("is", "unread");
        if (Favourite) Add("is", "starred");
        if (Pinned) Add("is", "pinned");
        Add("filename", FileName);
        Add("filetype", FileType);
        Add("larger", LargerThan);
        return sb.ToString();
    }

    /// <summary>Reads the form back from a query. Anything the form has no field for stays in <see cref="Words"/>.</summary>
    public static AdvancedSearch FromQuery(string? query)
    {
        var result = new AdvancedSearch();
        var words = new List<string>();
        foreach (var term in MessageSearch.Parse(query))
        {
            var value = term.Value;
            result = (term.Operator, value.ToLowerInvariant()) switch
            {
                ("from", _) => result with { From = value },
                ("to", _) => result with { To = value },
                ("subject", _) => result with { Subject = value },
                ("body", _) => result with { Body = value },
                ("account", _) => result with { Account = value },
                ("in", _) => result with { In = value },
                ("after", _) when MessageSearch.TryParseDate(value, out var d) => result with { After = d },
                ("before", _) when MessageSearch.TryParseDate(value, out var d) => result with { Before = d },
                ("has", var v) when v.StartsWith("attach", StringComparison.Ordinal) => result with { HasAttachment = true },
                ("is", "unread") => result with { Unread = true },
                ("is", "starred" or "favourite" or "favorite") => result with { Favourite = true },
                ("is", "pinned") => result with { Pinned = true },
                ("filename", _) => result with { FileName = value },
                ("filetype", _) => result with { FileType = value },
                ("larger", _) => result with { LargerThan = value },
                _ => Keep(result, term),
            };
        }

        return result with { Words = string.Join(' ', words) };

        AdvancedSearch Keep(AdvancedSearch r, SearchTerm t)
        {
            var text = t.Operator is null ? t.Value : $"{t.Operator}:{t.Value}";
            words.Add(text.Contains(' ') ? "\"" + text + "\"" : text);
            return r;
        }
    }
}
