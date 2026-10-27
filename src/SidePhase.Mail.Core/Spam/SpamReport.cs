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
}

public enum SpamVerdict
{
    /// <summary>No SpamAssassin information is available.</summary>
    Unknown,
    Clean,

    /// <summary>Score is approaching the threshold.</summary>
    Suspicious,
    Spam,
}
