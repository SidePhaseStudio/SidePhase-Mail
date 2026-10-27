namespace SidePhase.Mail.Core.Models;

public enum FolderKind
{
    Inbox,
    Drafts,
    Sent,
    Archive,
    Junk,
    Trash,
    Custom,
}

/// <summary>
/// A folder (IMAP mailbox) belonging to one account.
/// <see cref="Path"/> is the server path and is the stable identifier used for synchronisation.
/// </summary>
public sealed record MailFolder
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required FolderKind Kind { get; init; }
    public Guid? ParentId { get; init; }
    public int SortOrder { get; init; }
}
