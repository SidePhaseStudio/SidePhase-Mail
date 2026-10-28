using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Composition;

/// <summary>
/// A parsed <c>mailto:</c> link (RFC 6068), used when SidePhase Mail is the default email app or a link in a message
/// is clicked. Only recipients, subject and body are taken; anything else in the link (for example <c>attach=</c>,
/// which some programs abuse to attach local files) is ignored, and nothing is ever sent without the user pressing Send.
/// </summary>
public sealed record MailtoLink(
    IReadOnlyList<EmailAddress> To,
    IReadOnlyList<EmailAddress> Cc,
    IReadOnlyList<EmailAddress> Bcc,
    string Subject,
    string Body)
{
    /// <summary>Longest subject taken from a link (the line-length limit of an email header).</summary>
    public const int MaxSubjectLength = 998;

    /// <summary>Longest body taken from a link.</summary>
    public const int MaxBodyLength = 64 * 1024;

    /// <summary>Parses a mailto: link; null if the text isn't one.</summary>
    public static MailtoLink? Parse(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        var text = link.Trim();
        if (!text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return null;
        text = text["mailto:".Length..];

        var query = string.Empty;
        var q = text.IndexOf('?');
        if (q >= 0)
        {
            query = text[(q + 1)..];
            text = text[..q];
        }

        var to = new List<string> { Decode(text) };
        var cc = new List<string>();
        var bcc = new List<string>();
        var subject = string.Empty;
        var body = string.Empty;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = (eq < 0 ? pair : pair[..eq]).Trim().ToLowerInvariant();
            var value = eq < 0 ? string.Empty : Decode(pair[(eq + 1)..]);
            switch (name)
            {
                case "to": to.Add(value); break;
                case "cc": cc.Add(value); break;
                case "bcc": bcc.Add(value); break;
                case "subject" when subject.Length == 0: subject = SingleLine(value); break;
                case "body" when body.Length == 0: body = value; break;
            }
        }

        if (subject.Length > MaxSubjectLength) subject = subject[..MaxSubjectLength];
        if (body.Length > MaxBodyLength) body = body[..MaxBodyLength];
        body = body.Replace("\r\n", "\n", StringComparison.Ordinal);

        return new MailtoLink(Addresses(to), Addresses(cc), Addresses(bcc), subject, body);
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static IReadOnlyList<EmailAddress> Addresses(IEnumerable<string> lists)
    {
        var parsed = RecipientParser.Parse(string.Join(",", lists.Where(l => !string.IsNullOrWhiteSpace(l))));
        return parsed.Addresses;
    }
}
