using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Settings;

namespace SidePhase.Mail.Core.Abstractions;

public interface ISenderRulesService
{
    IReadOnlyList<SenderRule> Rules { get; }

    event EventHandler? RulesChanged;

    bool IsTrusted(EmailAddress sender);

    /// <summary>True if the sender's address or domain is blocked and the sender is not explicitly trusted.</summary>
    bool IsBlocked(EmailAddress sender);

    /// <summary>Adds a rule. Returns false if an identical rule already existed.</summary>
    bool AddRule(SenderRuleKind kind, string value);

    bool RemoveRule(SenderRuleKind kind, string value);
}
