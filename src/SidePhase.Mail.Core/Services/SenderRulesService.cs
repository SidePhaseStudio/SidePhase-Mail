using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Settings;

namespace SidePhase.Mail.Core.Services;

/// <summary>Trusted senders, blocked senders and blocked domains, persisted with the app settings.</summary>
public sealed class SenderRulesService : ISenderRulesService
{
    private readonly ISettingsService _settings;
    private readonly IClock _clock;

    public SenderRulesService(ISettingsService settings, IClock clock)
    {
        _settings = settings;
        _clock = clock;
        _settings.SettingsChanged += (_, _) => RulesChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<SenderRule> Rules => _settings.Current.SenderRules;

    public event EventHandler? RulesChanged;

    public bool IsTrusted(EmailAddress sender)
    {
        var address = sender.NormalizedAddress;
        return Rules.Any(r => r.Kind == SenderRuleKind.TrustedSender && r.NormalizedValue == address);
    }

    public bool IsBlocked(EmailAddress sender)
    {
        if (IsTrusted(sender)) return false;
        var address = sender.NormalizedAddress;
        var domain = sender.Domain;
        foreach (var rule in Rules)
        {
            switch (rule.Kind)
            {
                case SenderRuleKind.BlockedSender when rule.NormalizedValue == address:
                    return true;
                case SenderRuleKind.BlockedDomain when domain.Length > 0 && DomainMatches(domain, rule.NormalizedValue):
                    return true;
            }
        }

        return false;
    }

    public bool AddRule(SenderRuleKind kind, string value)
    {
        var normalized = Normalize(kind, value);
        if (normalized.Length == 0) return false;
        if (Rules.Any(r => r.Kind == kind && r.NormalizedValue == normalized)) return false;

        _settings.Update(s =>
        {
            // Trusting a sender removes a conflicting block, and vice versa.
            if (kind == SenderRuleKind.TrustedSender)
                s.SenderRules.RemoveAll(r => r.Kind == SenderRuleKind.BlockedSender && r.NormalizedValue == normalized);
            if (kind == SenderRuleKind.BlockedSender)
                s.SenderRules.RemoveAll(r => r.Kind == SenderRuleKind.TrustedSender && r.NormalizedValue == normalized);
            s.SenderRules.Add(new SenderRule(kind, normalized, _clock.Now));
        });
        return true;
    }

    public bool RemoveRule(SenderRuleKind kind, string value)
    {
        var normalized = Normalize(kind, value);
        if (!Rules.Any(r => r.Kind == kind && r.NormalizedValue == normalized)) return false;
        _settings.Update(s => s.SenderRules.RemoveAll(r => r.Kind == kind && r.NormalizedValue == normalized));
        return true;
    }

    /// <summary>"example.com" also matches "mail.example.com".</summary>
    internal static bool DomainMatches(string domain, string blocked) =>
        domain == blocked || domain.EndsWith("." + blocked, StringComparison.Ordinal);

    internal static string Normalize(SenderRuleKind kind, string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (kind == SenderRuleKind.BlockedDomain)
        {
            var at = v.LastIndexOf('@');
            if (at >= 0) v = v[(at + 1)..];
            v = v.TrimStart('.');
        }

        return v;
    }
}
