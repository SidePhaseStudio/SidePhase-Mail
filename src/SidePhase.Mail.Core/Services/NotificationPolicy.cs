using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Services;

/// <summary>Decides whether a newly arrived message should raise a desktop notification.</summary>
public sealed class NotificationPolicy(ISettingsService settings, ISenderRulesService rules, JunkPolicy junk)
{
    public bool ShouldNotify(MessageSummary message)
    {
        if (!settings.Current.NotificationsEnabled) return false;
        if (message.IsRead) return false;
        if (message.FolderKind != FolderKind.Inbox) return false;
        if (rules.IsBlocked(message.From)) return false;
        return !junk.IsLocallyJunk(message);
    }
}
