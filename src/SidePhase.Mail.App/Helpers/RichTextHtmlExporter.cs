using System.Net;
using System.Text;
using Microsoft.UI.Text;

namespace SidePhase.Mail.App.Helpers;

/// <summary>
/// Converts a RichEditBox document to simple, standard HTML that every mail client renders the same way: paragraphs,
/// bold, italic, underline, strikethrough, bulleted and numbered lists and http(s)/mailto links. No styles or scripts.
/// </summary>
public static class RichTextHtmlExporter
{
    public static string Export(RichEditTextDocument document)
    {
        document.GetText(TextGetOptions.None, out var text);
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // RichEdit terminates the document with a paragraph mark; don't emit an extra empty paragraph for it.
        var paragraphs = text.Split('\r');
        var count = text.EndsWith('\r') ? paragraphs.Length - 1 : paragraphs.Length;

        var html = new StringBuilder(text.Length * 2);
        string? openList = null; // "ul" or "ol"
        var position = 0;

        for (var index = 0; index < count; index++)
        {
            var paragraph = paragraphs[index];
            var start = position;
            var end = start + paragraph.Length;
            var listType = document.GetRange(start, start).ParagraphFormat.ListType;
            var list = listType switch
            {
                MarkerType.None or MarkerType.Undefined => null,
                MarkerType.Bullet => "ul",
                _ => "ol",
            };

            if (list != openList)
            {
                if (openList is not null) html.Append("</").Append(openList).Append('>');
                if (list is not null) html.Append('<').Append(list).Append('>');
                openList = list;
            }

            html.Append(list is null ? "<div>" : "<li>");
            if (paragraph.Length == 0)
            {
                html.Append("<br>");
            }
            else
            {
                AppendRuns(document, text, start, end, html);
            }

            html.Append(list is null ? "</div>" : "</li>");
            position = end + 1;
        }

        if (openList is not null) html.Append("</").Append(openList).Append('>');
        return html.ToString();
    }

    /// <summary>The URL of a link run, if it's a web or mailto link (anything else is exported as plain text).</summary>
    internal static string? SafeLink(string? link)
    {
        var url = link?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme is "http" or "https" or "mailto" ? uri.AbsoluteUri : null;
    }

    private static void AppendRuns(RichEditTextDocument document, string text, int start, int end, StringBuilder html)
    {
        var p = start;
        while (p < end)
        {
            var range = document.GetRange(p, p + 1);
            range.Expand(TextRangeUnit.CharacterFormat);
            var runEnd = Math.Min(range.EndPosition, end);
            if (runEnd <= p) runEnd = p + 1;

            var format = range.CharacterFormat;
            var chunk = WebUtility.HtmlEncode(text[p..runEnd]);
            if (format.Bold == FormatEffect.On) chunk = $"<b>{chunk}</b>";
            if (format.Italic == FormatEffect.On) chunk = $"<i>{chunk}</i>";
            if (format.Underline is not (UnderlineType.None or UnderlineType.Undefined) && SafeLink(range.Link) is null) chunk = $"<u>{chunk}</u>";
            if (format.Strikethrough == FormatEffect.On) chunk = $"<s>{chunk}</s>";
            if (SafeLink(range.Link) is { } href) chunk = $"<a href=\"{WebUtility.HtmlEncode(href)}\">{chunk}</a>";
            html.Append(chunk);
            p = runEnd;
        }
    }
}
