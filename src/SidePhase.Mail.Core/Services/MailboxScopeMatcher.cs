using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Services;

public static class MailboxScopeMatcher
{
    public static bool Matches(MessageSummary message, MailboxScope scope, Func<MessageSummary, bool> isLocallyJunk)
    {
        var junk = isLocallyJunk(message);
        var effective = junk ? FolderKind.Junk : message.FolderKind;

        // Snoozed messages are hidden everywhere except the Snoozed view until they come back.
        if (scope.Kind == MailboxScopeKind.Snoozed) return message.IsSnoozed && effective != FolderKind.Trash;
        if (message.IsSnoozed) return false;

        return scope.Kind switch
        {
            MailboxScopeKind.FollowUps => message.FollowUpAt is not null && effective != FolderKind.Trash,
            MailboxScopeKind.UnifiedInbox => effective == FolderKind.Inbox,
            MailboxScopeKind.AllAccounts => effective is not (FolderKind.Junk or FolderKind.Trash or FolderKind.Drafts),
            MailboxScopeKind.Pinned => message.IsPinned && effective is not (FolderKind.Trash or FolderKind.Junk),
            MailboxScopeKind.Favourites => message.IsStarred && effective != FolderKind.Trash,
            MailboxScopeKind.UnifiedFolder => effective == scope.FolderKind,
            MailboxScopeKind.AccountFolder => MatchesAccountFolder(message, scope, junk),
            _ => false,
        };
    }

    private static bool MatchesAccountFolder(MessageSummary message, MailboxScope scope, bool locallyJunk)
    {
        if (message.AccountId != scope.AccountId) return false;
        return scope.FolderKind switch
        {
            FolderKind.Inbox => message.FolderId == scope.FolderId && !locallyJunk,
            FolderKind.Junk => message.FolderId == scope.FolderId || locallyJunk,
            _ => message.FolderId == scope.FolderId,
        };
    }
}
