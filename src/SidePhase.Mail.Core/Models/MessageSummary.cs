using SidePhase.Mail.Core.Spam;

namespace SidePhase.Mail.Core.Models;

/// <summary>
/// The lightweight, list-level representation of a message. Bodies are loaded lazily through
/// <see cref="Abstractions.IMailDataSource.GetMessageBodyAsync"/> so large mailboxes never hold every body in memory.
/// </summary>
/// <remarks>
/// <see cref="Id"/> is a stable local identifier. It is never an IMAP sequence number; when real accounts are
/// connected it maps to (account, folder path, UIDVALIDITY, UID) plus the Message-ID header so that state such as
/// pins survives folder moves and resynchronisation.
/// </remarks>
public sealed record MessageSummary
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required Guid FolderId { get; init; }
    public required FolderKind FolderKind { get; init; }

    /// <summary>RFC 5322 Message-ID header, used to re-identify a message after it moves.</summary>
    public string? MessageIdHeader { get; init; }

    public required EmailAddress From { get; init; }
    public IReadOnlyList<EmailAddress> To { get; init; } = [];
    public IReadOnlyList<EmailAddress> Cc { get; init; } = [];

    public string Subject { get; init; } = string.Empty;
    public string Preview { get; init; } = string.Empty;
    public required DateTimeOffset Date { get; init; }

    public bool IsRead { get; init; }
    public bool IsStarred { get; init; }
    public bool IsPinned { get; init; }
    public DateTimeOffset? PinnedAt { get; init; }
    public bool HasAttachments { get; init; }

    /// <summary>Size of the whole message on the server in bytes (0 if unknown).</summary>
    public long Size { get; init; }

    /// <summary>
    /// Names, types and sizes of the attachments, from the server's description of the message (nothing is
    /// downloaded to learn them). May be empty for older cached mail until its details are fetched.
    /// </summary>
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];

    /// <summary>Local categories (labels) added by you or your rules. Kept on this PC.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>When a snoozed message returns to its folder. Null when not snoozed (local to this PC).</summary>
    public DateTimeOffset? SnoozedUntil { get; init; }

    /// <summary>When to follow up on this message. Null when no reminder is set (local to this PC).</summary>
    public DateTimeOffset? FollowUpAt { get; init; }

    /// <summary>
    /// Message-IDs of the messages this one replies to (References, then In-Reply-To), used to group conversations.
    /// </summary>
    public IReadOnlyList<string> References { get; init; } = [];

    public bool IsSnoozed => SnoozedUntil is not null;

    public SpamReport Spam { get; init; } = SpamReport.None;

    public string DisplaySubject => string.IsNullOrWhiteSpace(Subject) ? "(No subject)" : Subject;
}
