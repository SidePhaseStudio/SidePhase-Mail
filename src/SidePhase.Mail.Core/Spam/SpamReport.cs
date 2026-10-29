namespace SidePhase.Mail.Core.Spam;

/// <summary>
/// What the receiving server's spam filter said about a message, parsed from the headers it added (SpamAssassin,
/// Rspamd, Microsoft 365 / Outlook.com, Barracuda), plus the sender checks it recorded (SPF, DKIM, DMARC).
/// </summary>
public sealed record SpamReport
{
    public static SpamReport None { get; } = new();

    /// <summary>True when at least one recognised spam-filter header was present.</summary>
    public bool HasHeaders { get; init; }

    /// <summary>The filter that wrote the headers (see <see cref="SpamFilters"/>); null means SpamAssassin (older data).</summary>
    public string? Filter { get; init; }

    public string FilterName => Filter ?? SpamFilters.SpamAssassin;

    /// <summary>SPF / DKIM / DMARC results recorded by the receiving server, when it added them in transit.</summary>
    public SenderAuthentication? Authentication { get; init; }

    /// <summary>Anything worth keeping: filter headers or sender checks.</summary>
    public bool HasInformation => HasHeaders || Authentication is not null;

    /// <summary>The server's own yes/no decision (X-Spam-Flag or the first word of X-Spam-Status).</summary>
    public bool? ServerFlaggedAsSpam { get; init; }

    public double? Score { get; init; }

    /// <summary>The server's "required" threshold from X-Spam-Status, if reported.</summary>
    public double? RequiredScore { get; init; }

    public IReadOnlyList<string> Tests { get; init; } = [];

    public string? Version { get; init; }

    /// <summary>Per-rule breakdown from <c>X-Spam-Report</c>, when the server includes it.</summary>
    public IReadOnlyList<SpamRule> Rules { get; init; } = [];

    /// <summary>Whether the headers were added by the mail servers that delivered the message.</summary>
    public SpamHeaderTrust Trust { get; init; } = SpamHeaderTrust.Unknown;

    /// <summary>More than one X-Spam-Status header with different verdicts (only the topmost is used).</summary>
    public bool HasConflictingHeaders { get; init; }

    /// <summary>The headers can be relied on for classification.</summary>
    public bool IsUsable => HasHeaders && Trust != SpamHeaderTrust.Untrusted;
}

/// <summary>One SpamAssassin rule that matched, e.g. <c>3.5 BAYES_99 Bayes spam probability is 99 to 100%</c>.</summary>
public sealed record SpamRule(double Score, string Name, string Description);

public enum SpamHeaderTrust
{
    /// <summary>Delivery (Received) headers weren't available, so the origin can't be checked.</summary>
    Unknown,

    /// <summary>The spam headers sit above at least one Received header: a server added them in transit.</summary>
    AddedInTransit,

    /// <summary>
    /// The spam headers are below every Received header, so they were already in the message when the sender
    /// submitted it. A sender can write anything there; they are ignored for classification.
    /// </summary>
    Untrusted,
}

public enum SpamVerdict
{
    /// <summary>
    /// No trustworthy SpamAssassin information. This is <em>not</em> a statement that the message is safe:
    /// the server may simply not run SpamAssassin.
    /// </summary>
    Unknown,
    Clean,

    /// <summary>Score is approaching the threshold.</summary>
    Suspicious,
    Spam,
}

/// <summary>Names of the spam filters whose headers are understood.</summary>
public static class SpamFilters
{
    public const string SpamAssassin = "SpamAssassin";
    public const string Rspamd = "Rspamd";
    public const string Microsoft = "Microsoft";
    public const string Barracuda = "Barracuda";
    public const string Generic = "Your mail server";
}

/// <summary>
/// Sender checks from the receiving server's <c>Authentication-Results</c> header: did the message really come from
/// the domain it claims (SPF), is it signed by it (DKIM), and does that line up with the From address (DMARC).
/// Values are as reported: pass, fail, softfail, neutral, none, temperror, permerror; null when not reported.
/// </summary>
public sealed record SenderAuthentication(string? Spf, string? Dkim, string? Dmarc, string? CheckedBy)
{
    /// <summary>DMARC failed, or (without a DMARC result) both SPF and DKIM failed: the From address is likely forged.</summary>
    public bool LooksForged =>
        Is(Dmarc, "fail") || (Dmarc is null && Is(Spf, "fail") && Dkim is not null && !Is(Dkim, "pass"));

    public string Describe() => string.Join(" · ", new[] { ("SPF", Spf), ("DKIM", Dkim), ("DMARC", Dmarc) }
        .Where(p => p.Item2 is not null).Select(p => $"{p.Item1} {p.Item2}"));

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
