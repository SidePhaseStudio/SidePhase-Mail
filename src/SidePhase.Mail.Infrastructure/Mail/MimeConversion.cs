using System.Text;
using MimeKit;
using MimeKit.Utils;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Rendering;
using SidePhase.Mail.Core.Spam;

namespace SidePhase.Mail.Infrastructure.Mail;

/// <summary>Reads the parts of a downloaded message that SidePhase Mail displays.</summary>
public static class MessageBodyExtractor
{
    /// <summary>Inline images larger than this are not embedded (they still appear as attachments).</summary>
    public const long MaxInlineImageBytes = 8 * 1024 * 1024;

    public static MessageBody Extract(Guid messageId, MimeMessage message)
    {
        var html = message.HtmlBody;
        if (html is not null) html = EmbedInlineImages(message, html);

        return new MessageBody
        {
            MessageId = messageId,
            HtmlBody = html,
            TextBody = message.TextBody ?? HtmlText.ToPlainText(message.HtmlBody),
            Attachments = ListAttachments(message),
            Bcc = ToAddresses(message.Bcc),
        };
    }

    /// <summary>
    /// Replaces <c>cid:</c> references with <c>data:</c> URIs so inline images display without any network access or
    /// privileged file access from the message view.
    /// </summary>
    internal static string EmbedInlineImages(MimeMessage message, string html)
    {
        if (!html.Contains("cid:", StringComparison.OrdinalIgnoreCase)) return html;

        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (string.IsNullOrEmpty(part.ContentId) || !part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = new MemoryStream();
            part.Content?.DecodeTo(stream);
            if (stream.Length == 0 || stream.Length > MaxInlineImageBytes) continue;

            var dataUri = $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(stream.ToArray())}";
            html = html.Replace("cid:" + part.ContentId, dataUri, StringComparison.OrdinalIgnoreCase);
        }

        return html;
    }

    public static IReadOnlyList<AttachmentInfo> ListAttachments(MimeMessage message)
    {
        var result = new List<AttachmentInfo>();
        foreach (var entity in message.Attachments)
        {
            var (name, type, size) = Describe(entity);
            result.Add(new AttachmentInfo(name, type, size, (entity as MimePart)?.ContentId));
        }

        return result;
    }

    /// <summary>Decoded bytes of the attachment at <paramref name="index"/> (same order as <see cref="ListAttachments"/>).</summary>
    public static byte[] GetAttachmentContent(MimeMessage message, int index)
    {
        var entity = message.Attachments.ElementAtOrDefault(index)
            ?? throw new ArgumentOutOfRangeException(nameof(index), "That attachment no longer exists in the message.");
        using var stream = new MemoryStream();
        switch (entity)
        {
            case MessagePart rfc822:
                rfc822.Message.WriteTo(stream);
                break;
            case MimePart part:
                part.Content?.DecodeTo(stream);
                break;
        }

        return stream.ToArray();
    }

    public static SpamReport ReadSpamHeaders(HeaderList headers) =>
        SpamAssassinHeaderParser.Parse(headers.Select(h => new KeyValuePair<string, string>(h.Field, h.Value)));

    public static IReadOnlyList<EmailAddress> ToAddresses(InternetAddressList list) =>
        list.Mailboxes.Select(m => new EmailAddress(string.IsNullOrWhiteSpace(m.Name) ? null : m.Name, m.Address)).ToList();

    private static (string Name, string Type, long Size) Describe(MimeEntity entity)
    {
        switch (entity)
        {
            case MessagePart rfc822:
            {
                using var stream = new MemoryStream();
                rfc822.Message.WriteTo(stream);
                var subject = rfc822.Message.Subject;
                return ($"{(string.IsNullOrWhiteSpace(subject) ? "Attached message" : subject)}.eml", "message/rfc822", stream.Length);
            }

            case MimePart part:
            {
                using var stream = new MemoryStream();
                part.Content?.DecodeTo(stream);
                var name = string.IsNullOrWhiteSpace(part.FileName) ? "attachment" : Path.GetFileName(part.FileName);
                return (name, part.ContentType.MimeType, stream.Length);
            }

            default:
                return ("attachment", entity.ContentType.MimeType, 0);
        }
    }
}

/// <summary>Builds the MIME message that is actually sent or saved as a draft.</summary>
public static class MimeMessageBuilder
{
    public static MimeMessage Build(
        DraftMessage draft,
        MailAccount from,
        string? inReplyTo,
        IReadOnlyList<string>? references,
        IReadOnlyDictionary<DraftAttachment, byte[]>? attachmentContent = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(FromName(from), from.EmailAddress));
        message.To.AddRange(draft.To.Select(ToMailbox));
        message.Cc.AddRange(draft.Cc.Select(ToMailbox));
        message.Bcc.AddRange(draft.Bcc.Select(ToMailbox));
        message.Subject = draft.Subject;
        message.Date = DateTimeOffset.Now;
        var domain = from.EmailAddress.Contains('@') ? from.EmailAddress[(from.EmailAddress.LastIndexOf('@') + 1)..] : "sidephase.local";
        message.MessageId = MimeUtils.GenerateMessageId(domain);

        if (!string.IsNullOrWhiteSpace(inReplyTo))
        {
            message.InReplyTo = inReplyTo.Trim('<', '>');
            foreach (var reference in references ?? []) message.References.Add(reference.Trim('<', '>'));
            if (!message.References.Contains(message.InReplyTo)) message.References.Add(message.InReplyTo);
        }

        var builder = new BodyBuilder
        {
            TextBody = ComposeText(draft),
        };
        if (!draft.IsPlainText)
        {
            builder.HtmlBody = ComposeHtml(draft);
        }

        foreach (var attachment in draft.Attachments)
        {
            byte[] content;
            if (attachmentContent is not null && attachmentContent.TryGetValue(attachment, out var preloaded))
            {
                content = preloaded;
            }
            else if (!string.IsNullOrEmpty(attachment.SourcePath))
            {
                content = File.ReadAllBytes(attachment.SourcePath);
            }
            else
            {
                throw new FileNotFoundException($"The attachment \"{attachment.FileName}\" is no longer available.");
            }

            builder.Attachments.Add(attachment.FileName, content, ContentType.Parse(attachment.ContentType));
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    internal static string ComposeText(DraftMessage draft) =>
        draft.TextBody + (string.IsNullOrEmpty(draft.QuotedText) ? string.Empty : "\n\n" + draft.QuotedText);

    internal static string ComposeHtml(DraftMessage draft)
    {
        var body = string.IsNullOrEmpty(draft.HtmlBody) ? PlainTextHtml.Convert(draft.TextBody) : draft.HtmlBody;
        var html = new StringBuilder("<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body style=\"font-family:'Segoe UI',sans-serif;font-size:14px\">");
        html.Append(body);
        if (!string.IsNullOrEmpty(draft.QuotedHtml))
        {
            html.Append("<br><div style=\"color:#555\">").Append(draft.QuotedHtml).Append("</div>");
        }

        return html.Append("</body></html>").ToString();
    }

    private static MailboxAddress ToMailbox(EmailAddress a) => new(a.Name ?? string.Empty, a.Address);

    private static string FromName(MailAccount account)
    {
        // Account display names like "Work (demo)" aren't people's names; use the part before '@' if nothing better.
        var name = account.DisplayName;
        return string.IsNullOrWhiteSpace(name) ? account.EmailAddress : name;
    }
}
