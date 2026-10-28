using System.Text.RegularExpressions;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Composition;

public sealed record RecipientParseResult(IReadOnlyList<EmailAddress> Addresses, IReadOnlyList<string> Invalid)
{
    public bool IsValid => Invalid.Count == 0;
}

/// <summary>
/// Parses recipient fields typed by the user: <c>jane@example.com; "Doe, John" &lt;john@example.com&gt;, Ann &lt;ann@x.org&gt;</c>.
/// </summary>
public static partial class RecipientParser
{
    public static RecipientParseResult Parse(string? text)
    {
        var addresses = new List<EmailAddress>();
        var invalid = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return new RecipientParseResult(addresses, invalid);

        foreach (var raw in SplitRecipients(text))
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;

            var angle = AngleRegex().Match(token);
            if (angle.Success)
            {
                var name = angle.Groups["name"].Value.Trim().Trim('"').Trim();
                var addr = angle.Groups["addr"].Value.Trim();
                if (IsValidAddress(addr)) Add(new EmailAddress(name.Length == 0 ? null : name, addr));
                else invalid.Add(token);
            }
            else if (IsValidAddress(token))
            {
                Add(new EmailAddress(null, token));
            }
            else
            {
                invalid.Add(token);
            }
        }

        return new RecipientParseResult(addresses, invalid);

        void Add(EmailAddress address)
        {
            if (!addresses.Any(a => a.IsSameMailbox(address))) addresses.Add(address);
        }
    }

    public static string Format(IEnumerable<EmailAddress> addresses) =>
        string.Join("; ", addresses.Select(a =>
            string.IsNullOrWhiteSpace(a.Name) ? a.Address
            : a.Name!.IndexOfAny([',', ';', '"']) >= 0 ? $"\"{a.Name.Replace("\"", string.Empty)}\" <{a.Address}>"
            : $"{a.Name} <{a.Address}>"));

    /// <summary>The recipient being typed: the text after the last comma or semicolon (outside quotes and angle brackets).</summary>
    public static string LastToken(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text[(LastSeparator(text) + 1)..].Trim();
    }

    /// <summary>Replaces the recipient being typed with <paramref name="address"/> and starts the next one.</summary>
    public static string ReplaceLastToken(string? text, EmailAddress address)
    {
        text ??= string.Empty;
        var head = text[..(LastSeparator(text) + 1)].TrimEnd();
        var formatted = Format([address]);
        return (head.Length == 0 ? formatted : head + " " + formatted) + "; ";
    }

    private static int LastSeparator(string text)
    {
        var last = -1;
        var quoted = false;
        var angle = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '"': quoted = !quoted; break;
                case '<' when !quoted: angle++; break;
                case '>' when !quoted && angle > 0: angle--; break;
                case ',' or ';' when !quoted && angle == 0: last = i; break;
            }
        }

        return last;
    }

    public static bool IsValidAddress(string address) =>
        address.Length <= 254 && AddressRegex().IsMatch(address);

    /// <summary>Splits on commas and semicolons that are not inside quotes or angle brackets.</summary>
    private static IEnumerable<string> SplitRecipients(string text)
    {
        var start = 0;
        var inQuotes = false;
        var inAngle = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (c == '<' && !inQuotes) inAngle = true;
            else if (c == '>' && !inQuotes) inAngle = false;
            else if ((c == ',' || c == ';') && !inQuotes && !inAngle)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        yield return text[start..];
    }

    [GeneratedRegex(@"^(?<name>.*?)<(?<addr>[^<>]+)>\s*$")]
    private static partial Regex AngleRegex();

    // Deliberately pragmatic: local@domain.tld without spaces. Full RFC 5322 validation happens in MimeKit at send time.
    [GeneratedRegex(@"^[^\s@<>()\[\],;:""]+@[^\s@<>()\[\],;:""]+\.[^\s@<>()\[\],;:""]{2,}$")]
    private static partial Regex AddressRegex();
}
