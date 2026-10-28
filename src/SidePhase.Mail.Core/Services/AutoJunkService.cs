using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Services;

/// <summary>
/// Optional automatic Junk rule: moves newly arrived Inbox mail from blocked senders or domains into the account's
/// Junk folder on the server. Off by default. It never deletes anything, never acts on spam scores alone, and
/// trusted senders always win.
/// </summary>
public sealed class AutoJunkService(IMailDataSource mail, ISenderRulesService rules, ISettingsService settings)
{
    public IReadOnlyList<MessageSummary> SelectForJunk(IEnumerable<MessageSummary> arrived)
    {
        if (!settings.Current.MoveBlockedMailToServerJunk || mail.IsDemo) return [];
        return arrived
            .Where(m => m.FolderKind == FolderKind.Inbox && !rules.IsTrusted(m.From) && rules.IsBlocked(m.From))
            .ToList();
    }

    /// <summary>Moves the selected messages and returns the ones that were moved.</summary>
    public async Task<IReadOnlyList<MessageSummary>> ProcessNewMailAsync(IReadOnlyList<MessageSummary> arrived, CancellationToken cancellationToken = default)
    {
        var targets = SelectForJunk(arrived);
        if (targets.Count == 0) return [];
        var moves = await mail.MoveToAsync(targets.Select(m => m.Id).ToList(), FolderKind.Junk, cancellationToken).ConfigureAwait(false);
        return moves.Count == 0 ? [] : targets;
    }
}
