using System.Text.RegularExpressions;

namespace SidePhase.Mail.Core.Composition;

public static partial class SubjectPrefixes
{
    /// <summary>Removes one leading reply/forward prefix (Re:, RE:, Fwd:, FW:, AW:, WG:, SV:, VS:, TR:).</summary>
    public static string Strip(string subject)
    {
        var match = PrefixRegex().Match(subject);
        return match.Success ? subject[match.Length..].TrimStart() : subject;
    }

    public static string ForReply(string? subject)
    {
        var s = (subject ?? string.Empty).Trim();
        return ReplyRegex().IsMatch(s) ? s : $"Re: {s}".TrimEnd();
    }

    public static string ForForward(string? subject)
    {
        var s = (subject ?? string.Empty).Trim();
        return ForwardRegex().IsMatch(s) ? s : $"Fwd: {s}".TrimEnd();
    }

    [GeneratedRegex(@"^\s*(re|fwd?|aw|wg|sv|vs|tr)(\[\d+\])?\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(@"^(re|aw|sv)(\[\d+\])?\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyRegex();

    [GeneratedRegex(@"^(fwd?|wg|tr)(\[\d+\])?\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ForwardRegex();
}
