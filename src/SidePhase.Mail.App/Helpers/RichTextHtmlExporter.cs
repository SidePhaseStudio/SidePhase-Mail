using System.Net;
using System.Text;
using Microsoft.UI.Text;

namespace SidePhase.Mail.App.Helpers;

/// <summary>Converts a RichEditBox document to simple, email-safe HTML (bold, italic, underline, bullets).</summary>
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
        var inList = false;
        var position = 0;

        for (var index = 0; index < count; index++)
        {
            var paragraph = paragraphs[index];
            var start = position;
            var end = start + paragraph.Length;
            var isBullet = document.GetRange(start, start).ParagraphFormat.ListType == MarkerType.Bullet;

            if (isBullet && !inList)
            {
                html.Append("<ul>");
                inList = true;
            }
            else if (!isBullet && inList)
            {
                html.Append("</ul>");
                inList = false;
            }

            html.Append(isBullet ? "<li>" : "<div>");
            if (paragraph.Length == 0)
            {
                html.Append("<br>");
            }
            else
            {
                AppendRuns(document, text, start, end, html);
            }

            html.Append(isBullet ? "</li>" : "</div>");
            position = end + 1;
        }

        if (inList) html.Append("</ul>");
        return html.ToString();
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
            if (format.Underline is not (UnderlineType.None or UnderlineType.Undefined)) chunk = $"<u>{chunk}</u>";
            html.Append(chunk);
            p = runEnd;
        }
    }
}
