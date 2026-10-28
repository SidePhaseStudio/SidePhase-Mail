namespace SidePhase.Mail.Core.Spam;

/// <summary>
/// What the receiving server's SpamAssassin said about a message, parsed from its X-Spam-* headers.
/// </summary>
public sealed record SpamReport
{
    public static SpamReport None { get; } = new();

    /// <summary>True when at least one recognised SpamAssassin header was present.</summary>
    public bool HasHeaders { get; init; }

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
