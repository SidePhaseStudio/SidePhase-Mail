using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SidePhase.Mail.Core.Rendering;

/// <summary>Converts plain-text email bodies into safe HTML (escaped, line breaks preserved, links detected).</summary>
public static partial class PlainTextHtml
{
    public static string Convert(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder(normalized.Length + 64);
        var last = 0;
        foreach (Match m in UrlRegex().Matches(normalized))
        {
            sb.Append(WebUtility.HtmlEncode(normalized[last..m.Index]));
            var url = m.Value.TrimEnd('.', ',', ')', ';', ':', '!', '?');
            var encoded = WebUtility.HtmlEncode(url);
            sb.Append("<a href=\"").Append(encoded).Append("\">").Append(encoded).Append("</a>");
            sb.Append(WebUtility.HtmlEncode(m.Value[url.Length..]));
            last = m.Index + m.Length;
        }

        sb.Append(WebUtility.HtmlEncode(normalized[last..]));
        return "<div class=\"sp-plain\">" + sb.ToString().Replace("\n", "<br>\n") + "</div>";
    }

    [GeneratedRegex(@"\bhttps?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}

public static partial class HtmlText
{
    /// <summary>Very small HTML-to-text conversion used for previews and quoting.</summary>
    public static string? ToPlainText(string? html)
    {
        if (html is null) return null;
        var s = HtmlEmailDocumentBuilder.ExtractBodyContent(html);
        s = DropBlocksRegex().Replace(s, string.Empty);
        s = BreakRegex().Replace(s, "\n");
        s = BlockEndRegex().Replace(s, "\n");
        s = TagRegex().Replace(s, string.Empty);
        s = WebUtility.HtmlDecode(s);
        s = MultiNewlineRegex().Replace(s, "\n\n");
        return s.Trim();
    }

    /// <summary>A single-line preview of at most <paramref name="maxLength"/> characters.</summary>
    public static string Preview(string? text, int maxLength = 160)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var collapsed = WhitespaceRegex().Replace(text, " ").Trim();
        return collapsed.Length <= maxLength ? collapsed : collapsed[..(maxLength - 1)].TrimEnd() + "…";
    }

    [GeneratedRegex(@"<(script|style|head|title)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DropBlocksRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"</(p|div|li|tr|h[1-6]|blockquote)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\n[ \t]*\n[\s]*")]
    private static partial Regex MultiNewlineRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
