using System.Globalization;
using System.Text.RegularExpressions;

namespace SidePhase.Mail.Core.Spam;

/// <summary>
/// Parses the headers SpamAssassin adds to messages:
/// <c>X-Spam-Status</c>, <c>X-Spam-Flag</c>, <c>X-Spam-Score</c> and <c>X-Spam-Level</c>.
/// </summary>
public static partial class SpamAssassinHeaderParser
{
    public const string StatusHeader = "X-Spam-Status";
    public const string FlagHeader = "X-Spam-Flag";
    public const string ScoreHeader = "X-Spam-Score";
    public const string LevelHeader = "X-Spam-Level";

    public static SpamReport Parse(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        string? status = null, flag = null, score = null, level = null;
        foreach (var (name, value) in headers)
        {
            if (name.Equals(StatusHeader, StringComparison.OrdinalIgnoreCase)) status ??= value;
            else if (name.Equals(FlagHeader, StringComparison.OrdinalIgnoreCase)) flag ??= value;
            else if (name.Equals(ScoreHeader, StringComparison.OrdinalIgnoreCase)) score ??= value;
            else if (name.Equals(LevelHeader, StringComparison.OrdinalIgnoreCase)) level ??= value;
        }

        if (status is null && flag is null && score is null && level is null)
        {
            return SpamReport.None;
        }

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
        };
    }

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

    [GeneratedRegex(@"tests=(?<list>[A-Za-z0-9_,\s]*?)(?=\s+[a-z]+=|$)", RegexOptions.IgnoreCase)]
    private static partial Regex TestsRegex();
}
