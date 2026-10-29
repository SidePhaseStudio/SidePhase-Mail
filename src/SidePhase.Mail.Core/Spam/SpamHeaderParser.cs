using System.Globalization;
using System.Text.RegularExpressions;

namespace SidePhase.Mail.Core.Spam;

/// <summary>
/// Reads whatever spam verdict the receiving server left in a message: SpamAssassin (X-Spam-*), Rspamd
/// (X-Spamd-Result, X-Rspamd-Score), Microsoft 365 / Outlook.com (SCL in X-MS-Exchange-Organization-SCL or
/// X-Forefront-Antispam-Report), Barracuda (X-Barracuda-Spam-*), or a bare <c>X-Spam: yes</c>; and the sender checks
/// from <c>Authentication-Results</c>. Headers below every Received header were written by the sender and are not
/// trusted (see <see cref="SpamHeaderTrust"/>).
/// </summary>
public static partial class SpamHeaderParser
{
    /// <summary>Every header this parser reads, for fetching only what's needed during sync.</summary>
    public static readonly IReadOnlyList<string> HeaderNames =
    [
        SpamAssassinHeaderParser.StatusHeader, SpamAssassinHeaderParser.FlagHeader, SpamAssassinHeaderParser.ScoreHeader,
        SpamAssassinHeaderParser.LevelHeader, SpamAssassinHeaderParser.ReportHeader,
        RspamdResult, RspamdScore, GenericFlag,
        MicrosoftScl, MicrosoftForefront,
        BarracudaScore, BarracudaStatus,
        AuthenticationResults,
        SpamAssassinHeaderParser.ReceivedHeader,
    ];

    public const string RspamdResult = "X-Spamd-Result";
    public const string RspamdScore = "X-Rspamd-Score";
    public const string GenericFlag = "X-Spam";
    public const string MicrosoftScl = "X-MS-Exchange-Organization-SCL";
    public const string MicrosoftForefront = "X-Forefront-Antispam-Report";
    public const string BarracudaScore = "X-Barracuda-Spam-Score";
    public const string BarracudaStatus = "X-Barracuda-Spam-Status";
    public const string AuthenticationResults = "Authentication-Results";

    public static SpamReport Parse(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var list = headers.ToList();

        var report = SpamAssassinHeaderParser.Parse(list);
        if (!report.HasHeaders) report = ParseRspamd(list) ?? ParseMicrosoft(list) ?? ParseBarracuda(list) ?? ParseGeneric(list) ?? SpamReport.None;
        var authentication = ParseAuthentication(list);
        return authentication is null ? report : report with { Authentication = authentication };
    }

    private static SpamReport? ParseRspamd(List<KeyValuePair<string, string>> headers)
    {
        var result = First(headers, RspamdResult);
        var score = First(headers, RspamdScore);
        var flag = First(headers, GenericFlag);
        if (result is null && score is null) return null;

        bool? flagged = IsYes(flag);
        double? parsedScore = Number(score);
        double? required = null;
        var rules = new List<SpamRule>();
        if (result is not null)
        {
            // "default: False [3.40 / 15.00]; R_SPF_ALLOW(-0.20)[+ip4:1.2.3.4]; BAYES_HAM(-2.99)[99.9%]; ..."
            var head = RspamdHeadRegex().Match(result);
            if (head.Success)
            {
                flagged ??= head.Groups["flag"].Value.Equals("True", StringComparison.OrdinalIgnoreCase);
                parsedScore ??= Number(head.Groups["score"].Value);
                required = Number(head.Groups["required"].Value);
            }

            foreach (Match symbol in RspamdSymbolRegex().Matches(result))
            {
                if (Number(symbol.Groups["score"].Value) is { } value) rules.Add(new SpamRule(value, symbol.Groups["name"].Value, symbol.Groups["options"].Value.Trim()));
            }
        }

        return Report(headers, SpamFilters.Rspamd, flagged, parsedScore, required, rules, [RspamdResult, RspamdScore, GenericFlag]);
    }

    private static SpamReport? ParseMicrosoft(List<KeyValuePair<string, string>> headers)
    {
        // Spam confidence level: -1 (bypassed, trusted), 0-1 not spam, 5-6 spam, 9 high-confidence spam.
        var forefront = First(headers, MicrosoftForefront);
        var sclText = First(headers, MicrosoftScl)?.Trim();
        if (sclText is null && forefront is not null && MicrosoftFieldRegex("SCL").Match(forefront) is { Success: true } m) sclText = m.Groups["v"].Value;
        if (sclText is null || !int.TryParse(sclText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scl)) return null;

        var verdict = forefront is null ? null : MicrosoftFieldRegex("SFV").Match(forefront) is { Success: true } v ? v.Groups["v"].Value : null;
        bool? flagged = verdict is "SPM" or "SKS" || scl >= 5 ? true : scl is >= -1 and <= 1 || verdict is "NSPM" or "SKN" or "SFE" ? false : null;
        var tests = new List<SpamRule> { new(scl, "SCL", "Spam confidence level (5 or more is spam)") };
        if (verdict is not null) tests.Add(new SpamRule(0, "SFV:" + verdict, "Microsoft filtering verdict"));
        return Report(headers, SpamFilters.Microsoft, flagged, Math.Max(scl, 0), 5, tests, [MicrosoftScl, MicrosoftForefront]);
    }

    private static SpamReport? ParseBarracuda(List<KeyValuePair<string, string>> headers)
    {
        var score = First(headers, BarracudaScore);
        var status = First(headers, BarracudaStatus);
        if (score is null && status is null) return null;

        // "No, SCORE=0.31 using global scores of TAG_LEVEL=3.5 QUARANTINE_LEVEL=1000.0 KILL_LEVEL=9.0 tests=..."
        bool? flagged = status is null ? null : IsYes(status.TrimStart().Split([' ', ','], 2)[0]);
        var parsedScore = Number(score) ?? Number(KeyValueRegex("SCORE").Match(status ?? string.Empty).Groups["v"].Value);
        var required = Number(KeyValueRegex("TAG_LEVEL").Match(status ?? string.Empty).Groups["v"].Value);
        return Report(headers, SpamFilters.Barracuda, flagged, parsedScore, required, [], [BarracudaScore, BarracudaStatus]);
    }

    private static SpamReport? ParseGeneric(List<KeyValuePair<string, string>> headers)
    {
        var flag = First(headers, GenericFlag);
        return IsYes(flag) is { } flagged ? Report(headers, SpamFilters.Generic, flagged, null, null, [], [GenericFlag]) : null;
    }

    private static SpamReport Report(
        List<KeyValuePair<string, string>> headers, string filter, bool? flagged, double? score, double? required,
        IReadOnlyList<SpamRule> rules, IReadOnlyCollection<string> names) => new()
    {
        HasHeaders = true,
        Filter = filter,
        ServerFlaggedAsSpam = flagged,
        Score = score,
        RequiredScore = required,
        Rules = rules,
        Tests = rules.Select(r => r.Name).ToArray(),
        Trust = TrustOf(headers, names),
    };

    /// <summary>
    /// Sender checks from the topmost Authentication-Results header, the one the receiving server added. It's only
    /// used when a Received header follows it (added in transit); one the sender wrote is ignored.
    /// </summary>
    public static SenderAuthentication? ParseAuthentication(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var list = headers.ToList();
        var index = list.FindIndex(h => h.Key.Equals(AuthenticationResults, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        var lastReceived = list.FindLastIndex(h => h.Key.Equals(SpamAssassinHeaderParser.ReceivedHeader, StringComparison.OrdinalIgnoreCase));
        if (lastReceived < index) return null;

        var value = list[index].Value;
        var checkedBy = value.Split(';', 2)[0].Trim();
        var dkim = MethodRegex("dkim").Matches(value).Select(m => m.Groups["r"].Value.ToLowerInvariant()).ToList();
        var result = new SenderAuthentication(
            Method(value, "spf"),
            dkim.Count == 0 ? null : dkim.Contains("pass") ? "pass" : dkim[0],
            Method(value, "dmarc"),
            checkedBy.Length == 0 ? null : checkedBy);
        return result.Spf is null && result.Dkim is null && result.Dmarc is null ? null : result;
    }

    private static string? Method(string value, string method) =>
        MethodRegex(method).Match(value) is { Success: true } m ? m.Groups["r"].Value.ToLowerInvariant() : null;

    private static SpamHeaderTrust TrustOf(List<KeyValuePair<string, string>> headers, IReadOnlyCollection<string> names)
    {
        var first = headers.FindIndex(h => names.Contains(h.Key, StringComparer.OrdinalIgnoreCase));
        var lastReceived = headers.FindLastIndex(h => h.Key.Equals(SpamAssassinHeaderParser.ReceivedHeader, StringComparison.OrdinalIgnoreCase));
        return lastReceived < 0 ? SpamHeaderTrust.Unknown : first < lastReceived ? SpamHeaderTrust.AddedInTransit : SpamHeaderTrust.Untrusted;
    }

    private static string? First(List<KeyValuePair<string, string>> headers, string name) =>
        headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool? IsYes(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "yes" or "true" => true,
        "no" or "false" => false,
        _ => null,
    };

    private static double? Number(string? value) =>
        !string.IsNullOrWhiteSpace(value) && double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static Regex MicrosoftFieldRegex(string field) => new($@"(?:^|;)\s*{field}:(?<v>-?[A-Za-z0-9]+)", RegexOptions.IgnoreCase);

    private static Regex KeyValueRegex(string key) => new($@"\b{key}=(?<v>-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);

    private static Regex MethodRegex(string method) => new($@"(?:^|[;\s]){method}=(?<r>[a-z]+)", RegexOptions.IgnoreCase);

    [GeneratedRegex(@"^\s*[\w-]+:\s*(?<flag>True|False)\s*\[\s*(?<score>-?\d+(?:\.\d+)?)\s*/\s*(?<required>-?\d+(?:\.\d+)?)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex RspamdHeadRegex();

    [GeneratedRegex(@"(?<name>[A-Z][A-Z0-9_]{2,})\((?<score>-?\d+(?:\.\d+)?)\)(?:\[(?<options>[^\]]*)\])?")]
    private static partial Regex RspamdSymbolRegex();
}
