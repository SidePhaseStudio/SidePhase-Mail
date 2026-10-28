using System.Globalization;
using System.Text.RegularExpressions;

namespace SidePhase.Mail.Core.Spam;

/// <summary>
/// Parses the headers SpamAssassin adds to messages:
/// <c>X-Spam-Status</c>, <c>X-Spam-Flag</c>, <c>X-Spam-Score</c>, <c>X-Spam-Level</c> and <c>X-Spam-Report</c>.
/// </summary>
public static partial class SpamAssassinHeaderParser
{
    public const string StatusHeader = "X-Spam-Status";
    public const string FlagHeader = "X-Spam-Flag";
    public const string ScoreHeader = "X-Spam-Score";
    public const string LevelHeader = "X-Spam-Level";

    public const string ReportHeader = "X-Spam-Report";
    public const string ReceivedHeader = "Received";

    /// <summary>
    /// Parses headers in message order (top first). The topmost copy of each header wins: it was added last,
    /// closest to the user's mailbox. Pass <c>Received</c> headers too so the origin of the spam headers can be checked.
    /// </summary>
    public static SpamReport Parse(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        string? status = null, flag = null, score = null, level = null, report = null;
        var statuses = new List<string>();
        var firstSpamIndex = -1;
        var lastReceivedIndex = -1;
        var index = 0;
        foreach (var (name, value) in headers)
        {
            var isSpamHeader = true;
            if (name.Equals(StatusHeader, StringComparison.OrdinalIgnoreCase))
            {
                status ??= value;
                statuses.Add(value);
            }
            else if (name.Equals(FlagHeader, StringComparison.OrdinalIgnoreCase)) flag ??= value;
            else if (name.Equals(ScoreHeader, StringComparison.OrdinalIgnoreCase)) score ??= value;
            else if (name.Equals(LevelHeader, StringComparison.OrdinalIgnoreCase)) level ??= value;
            else if (name.Equals(ReportHeader, StringComparison.OrdinalIgnoreCase)) report ??= value;
            else
            {
                isSpamHeader = false;
                if (name.Equals(ReceivedHeader, StringComparison.OrdinalIgnoreCase)) lastReceivedIndex = index;
            }

            if (isSpamHeader && firstSpamIndex < 0) firstSpamIndex = index;
            index++;
        }

        if (status is null && flag is null && score is null && level is null && report is null)
        {
            return SpamReport.None;
        }

        var trust = lastReceivedIndex < 0
            ? SpamHeaderTrust.Unknown
            : firstSpamIndex < lastReceivedIndex ? SpamHeaderTrust.AddedInTransit : SpamHeaderTrust.Untrusted;
        var conflicting = statuses.Select(FirstWord).Where(w => w is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

        bool? flagged = null;
        double? parsedScore = null;
        double? required = null;
        IReadOnlyList<string> tests = [];
        string? version = null;

        if (status is not null)
        {
            // Headers may be folded over several lines; collapse whitespace first.
            var normalized = WhitespaceRegex().Replace(status, " ").Trim();

            var firstWord = normalized.Split([' ', ','], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstWord is not null)
            {
                if (firstWord.Equals("Yes", StringComparison.OrdinalIgnoreCase)) flagged = true;
                else if (firstWord.Equals("No", StringComparison.OrdinalIgnoreCase)) flagged = false;
            }

            parsedScore = ParseNumber(KeyValue(normalized, "score") ?? KeyValue(normalized, "hits"));
            required = ParseNumber(KeyValue(normalized, "required"));
            version = KeyValue(normalized, "version");

            var testList = TestsRegex().Match(normalized);
            if (testList.Success)
            {
                tests = testList.Groups["list"].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(t => t.Length > 0 && !t.Equals("none", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
        }

        if (flag is not null)
        {
            var f = flag.Trim();
            if (f.StartsWith("YES", StringComparison.OrdinalIgnoreCase)) flagged = true;
            else if (f.StartsWith("NO", StringComparison.OrdinalIgnoreCase)) flagged ??= false;
        }

        parsedScore ??= ParseNumber(score);

        var rules = report is null ? (IReadOnlyList<SpamRule>)[] : ParseReport(report);
        if (report is not null && (parsedScore is null || required is null))
        {
            // "Content analysis details:   (6.1 points, 5.0 required)"
            var summary = ReportSummaryRegex().Match(report);
            if (summary.Success)
            {
                parsedScore ??= ParseNumber(summary.Groups["score"].Value);
                required ??= ParseNumber(summary.Groups["required"].Value);
            }
        }

        if (tests.Count == 0 && rules.Count > 0) tests = rules.Select(r => r.Name).ToArray();

        if (parsedScore is null && level is not null)
        {
            // X-Spam-Level is one character (usually '*') per whole point.
            var stars = level.Trim();
            if (stars.Length > 0 && stars.All(c => c == stars[0]))
            {
                parsedScore = stars.Length;
            }
        }

        return new SpamReport
        {
            HasHeaders = true,
            ServerFlaggedAsSpam = flagged,
            Score = parsedScore,
            RequiredScore = required,
            Tests = tests,
            Version = version,
            Rules = rules,
            Trust = trust,
            HasConflictingHeaders = conflicting,
        };
    }

    /// <summary>Parses the rule lines of an <c>X-Spam-Report</c> header (both the "*" and the table layout).</summary>
    public static IReadOnlyList<SpamRule> ParseReport(string report)
    {
        var rules = new List<SpamRule>();
        // Folded headers keep one rule per line; an unfolded value has them run together after each "*".
        var text = UnfoldedRuleRegex().Replace(report.Replace("\r", string.Empty), "\n* ");
        foreach (var rawLine in text.Split('\n'))
        {
            var match = ReportLineRegex().Match(rawLine);
            if (!match.Success) continue;
            var value = ParseNumber(match.Groups["score"].Value);
            if (value is null) continue;
            rules.Add(new SpamRule(value.Value, match.Groups["name"].Value, match.Groups["desc"].Value.Trim()));
        }

        return rules;
    }

    private static string? FirstWord(string status) =>
        WhitespaceRegex().Replace(status, " ").Trim().Split([' ', ','], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    private static string? KeyValue(string status, string key)
    {
        var match = Regex.Match(status, $@"(?:^|[\s,]){Regex.Escape(key)}=(?<v>[^\s,]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value : null;
    }

    private static double? ParseNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^\s*\*?\s*(?<score>-?\d+(?:\.\d+)?)\s+(?<name>[A-Z][A-Z0-9_]{2,})\b\s*(?<desc>.*)$")]
    private static partial Regex ReportLineRegex();

    [GeneratedRegex(@"[ \t]\*[ \t]+(?=-?\d)")]
    private static partial Regex UnfoldedRuleRegex();

    [GeneratedRegex(@"\((?<score>-?\d+(?:\.\d+)?)\s+points?,\s*(?<required>-?\d+(?:\.\d+)?)\s+required\)", RegexOptions.IgnoreCase)]
    private static partial Regex ReportSummaryRegex();

    [GeneratedRegex(@"tests=(?<list>[A-Za-z0-9_,\s]*?)(?=\s+[a-z]+=|$)", RegexOptions.IgnoreCase)]
    private static partial Regex TestsRegex();
}
