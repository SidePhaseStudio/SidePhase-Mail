using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Settings;

namespace SidePhase.Mail.Core.Services;

public sealed record SendTheBuggerBackOptions(bool BlockDomain);

/// <summary>Everything needed to undo a "Send the Bugger Back!" action.</summary>
public sealed record SendTheBuggerBackResult(
    EmailAddress Sender,
    IReadOnlyList<SenderRule> AddedRules,
    IReadOnlyList<MoveRecord> Moves,
    bool DomainBlocked)
{
    public string Summary => DomainBlocked
        ? $"Sent the bugger back! Everything from @{Sender.Domain} now goes to Junk."
        : $"Sent the bugger back! {Sender.DisplayName} has been blocked and moved to Junk.";
}

/// <summary>
/// "Send the Bugger Back!" — blocks the sender (and optionally their domain) and moves the message to Junk.
/// </summary>
/// <remarks>
/// It deliberately does not reply to, bounce, or otherwise contact the sender. A manually confirmed rejection reply
/// can be added later through <see cref="IRejectionResponder"/>.
/// </remarks>
public sealed class SendTheBuggerBackService(IMailDataSource mail, ISenderRulesService rules, IClock clock)
{
    /// <summary>
    /// Public mailbox providers whose domains must never be blocked wholesale: doing so would block everyone who uses them.
    /// </summary>
    private static readonly HashSet<string> SharedProviderDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "hotmail.co.uk", "live.com", "live.co.uk",
        "msn.com", "yahoo.com", "yahoo.co.uk", "ymail.com", "icloud.com", "me.com", "mac.com", "aol.com",
        "proton.me", "protonmail.com", "gmx.com", "gmx.net", "gmx.de", "web.de", "mail.com", "zoho.com",
        "yandex.com", "yandex.ru", "btinternet.com", "sky.com", "virginmedia.com", "fastmail.com", "hey.com",
    };

    public static bool CanBlockDomain(EmailAddress sender) =>
        sender.Domain.Length > 0 && !SharedProviderDomains.Contains(sender.Domain);

    public async Task<SendTheBuggerBackResult> ExecuteAsync(
        MessageSummary message,
        SendTheBuggerBackOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var sender = message.From;
        var added = new List<SenderRule>();

        // A trusted sender is being explicitly rejected, so the trust no longer applies.
        rules.RemoveRule(SenderRuleKind.TrustedSender, sender.Address);

        if (rules.AddRule(SenderRuleKind.BlockedSender, sender.Address))
            added.Add(new SenderRule(SenderRuleKind.BlockedSender, sender.NormalizedAddress, clock.Now));

        var blockDomain = options.BlockDomain && CanBlockDomain(sender);
        if (blockDomain && rules.AddRule(SenderRuleKind.BlockedDomain, sender.Domain))
            added.Add(new SenderRule(SenderRuleKind.BlockedDomain, sender.Domain, clock.Now));

        IReadOnlyList<MoveRecord> moves = message.FolderKind == FolderKind.Junk
            ? []
            : await mail.MoveToAsync([message.Id], FolderKind.Junk, cancellationToken).ConfigureAwait(false);

        return new SendTheBuggerBackResult(sender, added, moves, blockDomain);
    }

    public async Task UndoAsync(SendTheBuggerBackResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        foreach (var rule in result.AddedRules)
        {
            rules.RemoveRule(rule.Kind, rule.Value);
        }

        if (result.Moves.Count > 0)
        {
            await mail.RestoreAsync(result.Moves, cancellationToken).ConfigureAwait(false);
        }
    }
}
