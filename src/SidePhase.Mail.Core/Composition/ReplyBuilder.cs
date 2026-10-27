using System.Globalization;
using System.Net;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Rendering;

namespace SidePhase.Mail.Core.Composition;

/// <summary>Creates reply, reply-all and forward drafts from an existing message.</summary>
public static class ReplyBuilder
{
    public static DraftMessage Create(
        ComposeMode mode,
        MessageSummary original,
        MessageBody? body,
        MailAccount account,
        CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var me = account.Address;
        var dateText = original.Date.ToString("f", culture);

        IReadOnlyList<EmailAddress> to = [];
        IReadOnlyList<EmailAddress> cc = [];
        string subject;
        string headerHtml;
        string headerText;

        switch (mode)
        {
            case ComposeMode.Reply:
                to = [ReplyTarget(original, me)];
                subject = SubjectPrefixes.ForReply(original.Subject);
                (headerHtml, headerText) = ReplyHeader(original, dateText);
                break;

            case ComposeMode.ReplyAll:
                var everyone = new List<EmailAddress> { ReplyTarget(original, me) };
                everyone.AddRange(original.To);
                to = Distinct(everyone, me);
                cc = Distinct(original.Cc, me).Where(c => !to.Any(t => t.IsSameMailbox(c))).ToList();
                subject = SubjectPrefixes.ForReply(original.Subject);
                (headerHtml, headerText) = ReplyHeader(original, dateText);
                break;

            case ComposeMode.Forward:
                subject = SubjectPrefixes.ForForward(original.Subject);
                (headerHtml, headerText) = ForwardHeader(original, dateText);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Only Reply, ReplyAll and Forward are supported.");
        }

        var originalHtml = body?.HtmlBody is { Length: > 0 } html
            ? HtmlEmailDocumentBuilder.Sanitize(HtmlEmailDocumentBuilder.ExtractBodyContent(html))
            : PlainTextHtml.Convert(body?.TextBody ?? original.Preview);
        var originalText = body?.TextBody ?? HtmlText.ToPlainText(body?.HtmlBody) ?? original.Preview;

        var quotedHtml = mode == ComposeMode.Forward
            ? $"<div class=\"sp-forward\">{headerHtml}{originalHtml}</div>"
            : $"<div class=\"sp-quote-header\">{headerHtml}</div><blockquote>{originalHtml}</blockquote>";
        var quotedText = mode == ComposeMode.Forward
            ? $"{headerText}\n{originalText}"
            : $"{headerText}\n{QuoteLines(originalText)}";

        return new DraftMessage
        {
            AccountId = account.Id,
            Mode = mode,
            SourceMessageId = original.Id,
            To = to,
            Cc = cc,
            Subject = subject,
            QuotedHtml = quotedHtml,
            QuotedText = quotedText,
            Attachments = mode == ComposeMode.Forward && body is not null
                ? body.Attachments.Select(a => new DraftAttachment(a.FileName, a.SizeBytes, null, a.ContentType)).ToList()
                : [],
        };
    }

    private static EmailAddress ReplyTarget(MessageSummary original, EmailAddress me) =>
        // Replying to your own sent message goes to its original recipients.
        original.From.IsSameMailbox(me) && original.To.Count > 0 ? original.To[0] : original.From;

    private static List<EmailAddress> Distinct(IEnumerable<EmailAddress> addresses, EmailAddress me)
    {
        var result = new List<EmailAddress>();
        foreach (var a in addresses)
        {
            if (a.IsSameMailbox(me)) continue;
            if (result.Any(r => r.IsSameMailbox(a))) continue;
            result.Add(a);
        }

        return result;
    }

    private static (string Html, string Text) ReplyHeader(MessageSummary original, string dateText)
    {
        var who = original.From.ToString();
        return ($"<p>On {E(dateText)}, {E(who)} wrote:</p>", $"On {dateText}, {who} wrote:");
    }

    private static (string Html, string Text) ForwardHeader(MessageSummary original, string dateText)
    {
        var to = string.Join(", ", original.To.Select(a => a.ToString()));
        var html =
            "<p>---------- Forwarded message ----------<br>" +
            $"From: {E(original.From.ToString())}<br>" +
            $"Date: {E(dateText)}<br>" +
            $"Subject: {E(original.Subject)}<br>" +
            $"To: {E(to)}</p>";
        var text =
            "---------- Forwarded message ----------\n" +
            $"From: {original.From}\nDate: {dateText}\nSubject: {original.Subject}\nTo: {to}\n";
        return (html, text);
    }

    private static string QuoteLines(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(l => "> " + l));

    private static string E(string s) => WebUtility.HtmlEncode(s);
}
