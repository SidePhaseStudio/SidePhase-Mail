using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Spam;

namespace SidePhase.Mail.Core.Services;

/// <summary>
/// Decides whether an Inbox message should be shown in Junk locally. This is a view-level decision only:
/// nothing is moved or deleted on the server unless the user explicitly moves the message.
/// </summary>
public sealed class JunkPolicy(ISenderRulesService rules, ISettingsService settings)
{
    public SpamVerdict GetVerdict(MessageSummary message) =>
        SpamClassifier.Classify(message.Spam, settings.Current.SpamThreshold);

    public bool IsLocallyJunk(MessageSummary message)
    {
        if (message.FolderKind != FolderKind.Inbox) return false;
        if (rules.IsTrusted(message.From)) return false;
        if (rules.IsBlocked(message.From)) return true;
        return settings.Current.TreatHighScoresAsJunk && GetVerdict(message) == SpamVerdict.Spam;
    }

    /// <summary>The folder kind the message appears under in SidePhase Mail.</summary>
    public FolderKind EffectiveFolder(MessageSummary message) =>
        IsLocallyJunk(message) ? FolderKind.Junk : message.FolderKind;
}
