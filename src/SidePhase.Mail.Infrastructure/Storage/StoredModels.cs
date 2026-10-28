using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Spam;

namespace SidePhase.Mail.Infrastructure.Storage;

public sealed record StoredAccount(MailAccount Account, AccountConnectionSettings Settings);

public sealed record StoredFolder
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required FolderKind Kind { get; init; }
    public uint UidValidity { get; init; }
    public uint HighestUid { get; init; }
    public ulong HighestModSeq { get; init; }
    public DateTimeOffset? LastSync { get; init; }
    public int SortOrder { get; init; }

    /// <summary>False for folders that are only used as a move target (e.g. Gmail's "All Mail").</summary>
    public bool Synchronize { get; init; } = true;

    public MailFolder ToMailFolder() => new()
    {
        Id = Id,
        AccountId = AccountId,
        Name = Name,
        Path = Path,
        Kind = Kind,
        SortOrder = SortOrder,
    };
}

public sealed record StoredMessage
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required Guid FolderId { get; init; }
    public required uint Uid { get; init; }
    public required uint UidValidity { get; init; }
    public string? MessageIdHeader { get; init; }
    public required string IdentityKey { get; init; }
    public required EmailAddress From { get; init; }
    public IReadOnlyList<EmailAddress> To { get; init; } = [];
    public IReadOnlyList<EmailAddress> Cc { get; init; } = [];
    public string Subject { get; init; } = string.Empty;
    public string Preview { get; init; } = string.Empty;
    public required DateTimeOffset Date { get; init; }
    public bool IsRead { get; init; }
    public bool IsStarred { get; init; }
    public bool HasAttachments { get; init; }
    public SpamReport Spam { get; init; } = SpamReport.None;
    public long Size { get; init; }
    public string? InReplyTo { get; init; }
    public IReadOnlyList<string> References { get; init; } = [];
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];
}

/// <summary>Where a cached message lives on the server.</summary>
public sealed record StoredMessageRef(
    Guid Id,
    Guid AccountId,
    Guid FolderId,
    string FolderPath,
    FolderKind FolderKind,
    uint Uid,
    uint UidValidity,
    string IdentityKey,
    string? MessageIdHeader,
    bool IsRead,
    bool IsStarred);
