namespace SidePhase.Mail.Core.Models;

public enum MailboxScopeKind
{
    /// <summary>Inbox of every account.</summary>
    UnifiedInbox,

    /// <summary>All mail in every account, excluding Junk and Trash.</summary>
    AllAccounts,

    /// <summary>Messages pinned to the top, from every account.</summary>
    Pinned,

    /// <summary>Starred messages from every account.</summary>
    Favourites,

    /// <summary>A well-known folder (Drafts, Sent, ...) across every account.</summary>
    UnifiedFolder,

    /// <summary>One specific folder of one account.</summary>
    AccountFolder,

    /// <summary>Messages hidden until a chosen time.</summary>
    Snoozed,

    /// <summary>Messages with a follow-up reminder.</summary>
    FollowUps,

    /// <summary>A saved search (smart folder). Matching needs the folder's query, so lists handle it specially.</summary>
    SmartFolder,
}

/// <summary>
/// Describes which messages a list shows. Immutable so it can be used as a dictionary key.
/// </summary>
public sealed record MailboxScope(
    MailboxScopeKind Kind,
    FolderKind? FolderKind = null,
    Guid? AccountId = null,
    Guid? FolderId = null)
{
    public static MailboxScope UnifiedInbox { get; } = new(MailboxScopeKind.UnifiedInbox);
    public static MailboxScope AllAccounts { get; } = new(MailboxScopeKind.AllAccounts);
    public static MailboxScope Pinned { get; } = new(MailboxScopeKind.Pinned);
    public static MailboxScope Favourites { get; } = new(MailboxScopeKind.Favourites);
    public static MailboxScope Snoozed { get; } = new(MailboxScopeKind.Snoozed);
    public static MailboxScope FollowUps { get; } = new(MailboxScopeKind.FollowUps);

    public static MailboxScope Unified(FolderKind kind) => new(MailboxScopeKind.UnifiedFolder, kind);

    /// <summary>A smart folder, identified by its id (carried in <see cref="FolderId"/>).</summary>
    public static MailboxScope ForSmartFolder(Guid smartFolderId) => new(MailboxScopeKind.SmartFolder, FolderId: smartFolderId);

    public static MailboxScope ForFolder(MailFolder folder) =>
        new(MailboxScopeKind.AccountFolder, folder.Kind, folder.AccountId, folder.Id);
}
