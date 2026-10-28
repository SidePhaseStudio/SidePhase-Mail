using System.Text;

namespace SidePhase.Mail.Core.Contacts;

/// <summary>A contact read from a vCard file, before it's added to the address book.</summary>
public sealed record VCardContact(string DisplayName, IReadOnlyList<ContactEmail> Emails, string? Notes);

/// <summary>
/// Reads names, email addresses and notes from vCard 2.1, 3.0 and 4.0 files (as exported by Outlook, Google
/// Contacts, iCloud and phones). Other fields are ignored. Never throws on malformed input; bad cards are skipped.
/// </summary>
public static class VCardParser
{
    /// <summary>Files bigger than this are refused, so a huge or hostile file can't exhaust memory.</summary>
    public const int MaxFileCharacters = 20 * 1024 * 1024;

    public static IReadOnlyList<VCardContact> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxFileCharacters) throw new InvalidDataException("This file is too large to be a contacts file.");

        var result = new List<VCardContact>();
        List<(string Name, Dictionary<string, string> Params, string Value)>? card = null;
        foreach (var line in Unfold(text))
        {
            if (line.Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                card = [];
                continue;
            }

            if (line.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (card is not null && Build(card) is { } contact) result.Add(contact);
                card = null;
                continue;
            }

            if (card is not null && ParseLine(line) is { } property) card.Add(property);
        }

        return result;
    }

    private static VCardContact? Build(List<(string Name, Dictionary<string, string> Params, string Value)> card)
    {
        string? Get(string name) => card.FirstOrDefault(p => p.Name == name).Value;

        var emails = new List<ContactEmail>();
        foreach (var (name, parameters, value) in card.Where(p => p.Name == "EMAIL"))
        {
            var address = value.Trim();
            if (address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) address = address[7..];
            if (!LooksLikeEmail(address) || emails.Any(e => e.Address.Equals(address, StringComparison.OrdinalIgnoreCase))) continue;
            var label = parameters.TryGetValue("TYPE", out var type)
                ? type.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim().ToLowerInvariant())
                    .FirstOrDefault(t => t is "home" or "work" or "other")
                : null;
            emails.Add(new ContactEmail(address, label));
        }

        var displayName = Get("FN")?.Trim();
        if (string.IsNullOrEmpty(displayName) && Get("N") is { } structured)
        {
            // N:Family;Given;Middle;Prefix;Suffix
            var parts = SplitUnescaped(structured, ';');
            displayName = string.Join(' ', new[] { parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(0) }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        }

        if (string.IsNullOrEmpty(displayName)) displayName = Get("ORG")?.Split(';')[0].Trim();
        if (string.IsNullOrEmpty(displayName)) displayName = emails.FirstOrDefault()?.Address;
        if (string.IsNullOrEmpty(displayName)) return null;

        var notes = Get("NOTE")?.Trim();
        return new VCardContact(displayName, emails, string.IsNullOrEmpty(notes) ? null : notes);
    }

    private static (string, Dictionary<string, string>, string)? ParseLine(string line)
    {
        var colon = IndexOfUnquoted(line, ':');
        if (colon <= 0) return null;

        var head = line[..colon];
        var raw = line[(colon + 1)..];
        var segments = head.Split(';');

        // Strip group prefixes such as "item1.EMAIL".
        var name = segments[0];
        var dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        name = name.ToUpperInvariant();

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in segments.Skip(1))
        {
            var eq = segment.IndexOf('=');
            if (eq < 0)
            {
                // vCard 2.1 bare parameters, e.g. EMAIL;INTERNET;HOME or ;QUOTED-PRINTABLE
                if (segment.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase)) parameters["ENCODING"] = "QUOTED-PRINTABLE";
                else parameters["TYPE"] = parameters.TryGetValue("TYPE", out var t) ? t + "," + segment : segment;
                continue;
            }

            var key = segment[..eq].Trim();
            var val = segment[(eq + 1)..].Trim().Trim('"');
            parameters[key] = parameters.TryGetValue(key, out var existing) ? existing + "," + val : val;
        }

        string value;
        if (parameters.TryGetValue("ENCODING", out var encoding) && encoding.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase))
        {
            value = DecodeQuotedPrintable(raw, parameters.GetValueOrDefault("CHARSET"));
        }
        else
        {
            value = name is "N" or "ORG" ? raw : Unescape(raw);
        }

        return (name, parameters, value);
    }

    /// <summary>Joins folded lines (a line starting with a space or tab continues the previous one).</summary>
    private static IEnumerable<string> Unfold(string text)
    {
        var current = new StringBuilder();
        var softBreak = false;
        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (softBreak)
            {
                // Quoted-printable soft line break: "=" at the end of a line continues on the next.
                current.Append(rawLine);
            }
            else if (rawLine.Length > 0 && (rawLine[0] == ' ' || rawLine[0] == '\t') && current.Length > 0)
            {
                current.Append(rawLine, 1, rawLine.Length - 1);
            }
            else
            {
                if (current.Length > 0) yield return current.ToString();
                current.Clear().Append(rawLine);
            }

            softBreak = current.Length > 0 && current[^1] == '=' && current.ToString().Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
            if (softBreak) current.Length--;
        }

        if (current.Length > 0) yield return current.ToString();
    }

    private static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else
            {
                sb.Append(value[i]);
            }
        }

        return sb.ToString();
    }

    private static List<string> SplitUnescaped(string value, char separator)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                sb.Append(value[++i]);
            }
            else if (value[i] == separator)
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(value[i]);
            }
        }

        parts.Add(sb.ToString());
        return parts;
    }

    private static int IndexOfUnquoted(string line, char target)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == target && !quoted) return i;
        }

        return -1;
    }

    private static string DecodeQuotedPrintable(string value, string? charset)
    {
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '=' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
            }
        }

        Encoding encoding;
        try
        {
            encoding = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        return encoding.GetString(bytes.ToArray());
    }

    internal static bool LooksLikeEmail(string address)
    {
        var at = address.IndexOf('@');
        return at > 0 && at == address.LastIndexOf('@') && at < address.Length - 3 && address.IndexOf('.', at) > at + 1
            && !address.Any(char.IsWhiteSpace) && address.Length <= 254;
    }
}
