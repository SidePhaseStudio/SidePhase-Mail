using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Organisation;

public enum RuleField
{
    /// <summary>Sender name or address contains the value.</summary>
    Sender,

    /// <summary>Sender's domain is the value or a subdomain of it (example.com matches mail.example.com).</summary>
    SenderDomain,

    /// <summary>A To or Cc name or address contains the value.</summary>
    Recipient,

    /// <summary>Subject contains the value.</summary>
    Subject,

    /// <summary>Subject or preview contains any of the comma-separated words.</summary>
    Keywords,

    /// <summary>A verified SpamAssassin score of at least the value.</summary>
    SpamScore,

    /// <summary>Has attachments ("yes") or not ("no").</summary>
    HasAttachment,
}

public enum RuleActionKind
{
    MoveToFolder,
    MarkRead,
    Star,
    AddCategory,
    SuppressNotification,
}

public sealed record RuleCondition(RuleField Field, string Value);

/// <param name="Value">The folder id for <see cref="RuleActionKind.MoveToFolder"/>, the category name for
/// <see cref="RuleActionKind.AddCategory"/>; otherwise unused.</param>
public sealed record RuleAction(RuleActionKind Kind, string? Value = null);

/// <summary>
/// A user-defined rule: when new mail matches the conditions (all of them, or any), the actions run once per message.
/// </summary>
public sealed record MailRule
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public bool MatchAll { get; init; } = true;

    /// <summary>Only for this account (null = every account).</summary>
    public Guid? AccountId { get; init; }

    public IReadOnlyList<RuleCondition> Conditions { get; init; } = [];
    public IReadOnlyList<RuleAction> Actions { get; init; } = [];

    /// <summary>Don't run later rules on a message this rule matched.</summary>
    public bool StopProcessing { get; init; }

    public int Position { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public bool Matches(MessageSummary message)
    {
        if (AccountId is { } account && message.AccountId != account) return false;
        if (Conditions.Count == 0) return false;
        return MatchAll ? Conditions.All(c => MailRuleConditions.Matches(c, message)) : Conditions.Any(c => MailRuleConditions.Matches(c, message));
    }
}

public static class MailRuleConditions
{
    public static bool Matches(RuleCondition condition, MessageSummary m)
    {
        var value = condition.Value.Trim();
        return condition.Field switch
        {
            RuleField.Sender => SenderMatches(m.From, value),
            RuleField.SenderDomain => DomainMatches(m.From.Domain, value.TrimStart('@').ToLowerInvariant()),
            RuleField.Recipient => value.Length > 0 && m.To.Concat(m.Cc).Any(a => Contains(a.Address, value) || Contains(a.Name, value)),
            RuleField.Subject => value.Length > 0 && Contains(m.Subject, value),
            RuleField.Keywords => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(k => Contains(m.Subject, k) || Contains(m.Preview, k)),
            RuleField.SpamScore => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var min)
                && m.Spam.Trust != Spam.SpamHeaderTrust.Untrusted && m.Spam.Score is { } score && score >= min,
            RuleField.HasAttachment => value.Equals("no", StringComparison.OrdinalIgnoreCase) ? !m.HasAttachments : m.HasAttachments,
            _ => false,
        };
    }

    /// <summary>
    /// A full address ("ceo@corp.example") must be the sender's exact address: never part of a longer address
    /// ("ceo@corp.example.evil.example") or the display name, which anyone can set to anything. "@corp.example" means
    /// that domain. Other text matches part of the address or the name (convenient, but easy for a sender to imitate,
    /// so the rule editor suggests full addresses).
    /// </summary>
    private static bool SenderMatches(EmailAddress from, string value)
    {
        if (value.Length == 0) return false;
        if (value.StartsWith('@')) return DomainMatches(from.Domain, value.TrimStart('@').ToLowerInvariant());
        if (value.Contains('@')) return string.Equals(from.Address.Trim(), value, StringComparison.OrdinalIgnoreCase);
        return Contains(from.Address, value) || Contains(from.Name, value);
    }

    private static bool DomainMatches(string domain, string wanted) =>
        wanted.Length > 0 && (domain == wanted || domain.EndsWith("." + wanted, StringComparison.Ordinal));

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
}

public interface IMailRuleStore
{
    Task<IReadOnlyList<MailRule>> ListAsync(CancellationToken ct = default);
    Task SaveAsync(MailRule rule, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Records that the rule acted on these messages and returns only those it hadn't acted on before (atomic, so
    /// a message is never processed twice by the same rule, even after it moves).
    /// </summary>
    Task<IReadOnlyList<MessageSummary>> ClaimAsync(Guid ruleId, IReadOnlyList<(MessageSummary Message, string IdentityKey)> messages, DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>What a run of the rules did.</summary>
public sealed record RuleRunResult(int Matched, int Moved, IReadOnlySet<Guid> SuppressNotifications, IReadOnlyList<string> Problems)
{
    public static readonly RuleRunResult Empty = new(0, 0, new HashSet<Guid>(), []);
}

/// <summary>
/// Runs rules on new mail (each rule acts on a message at most once) and, on request, on an existing folder.
/// Rules only use actions you choose; nothing is deleted.
/// </summary>
public sealed class MailRuleService(IMailRuleStore store, IMailDataSource mail, IClock clock)
{
    public event EventHandler? Changed;

    public async Task<IReadOnlyList<MailRule>> ListAsync(CancellationToken ct = default) =>
        (await store.ListAsync(ct).ConfigureAwait(false)).OrderBy(r => r.Position).ThenBy(r => r.CreatedAt).ToList();

    public async Task<MailRule> SaveAsync(MailRule rule, CancellationToken ct = default)
    {
        var name = rule.Name.Trim();
        if (name.Length == 0) throw new ArgumentException("Give the rule a name.");
        var conditions = rule.Conditions.Where(c => c.Field is RuleField.HasAttachment || c.Value.Trim().Length > 0).ToList();
        if (conditions.Count == 0) throw new ArgumentException("Add at least one condition.");
        if (rule.Actions.Count == 0) throw new ArgumentException("Choose at least one action.");
        if (rule.Actions.Any(a => a.Kind == RuleActionKind.MoveToFolder && !Guid.TryParse(a.Value, out _))) throw new ArgumentException("Choose the folder to move to.");
        if (rule.Actions.Any(a => a.Kind == RuleActionKind.AddCategory && string.IsNullOrWhiteSpace(a.Value))) throw new ArgumentException("Name the category to add.");

        var existing = await store.ListAsync(ct).ConfigureAwait(false);
        var now = clock.Now;
        var saved = rule with
        {
            Name = name,
            Conditions = conditions,
            CreatedAt = rule.CreatedAt == default ? now : rule.CreatedAt,
            UpdatedAt = now,
            Position = existing.Any(r => r.Id == rule.Id) ? rule.Position : existing.Select(r => r.Position).DefaultIfEmpty(-1).Max() + 1,
        };
        await store.SaveAsync(saved, ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await store.DeleteAsync(id, ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs enabled rules on newly arrived messages. Messages a rule already handled are skipped.</summary>
    public async Task<RuleRunResult> RunOnNewMailAsync(IReadOnlyList<MessageSummary> messages, CancellationToken ct = default)
    {
        var rules = (await ListAsync(ct).ConfigureAwait(false)).Where(r => r.Enabled).ToList();
        if (rules.Count == 0 || messages.Count == 0) return RuleRunResult.Empty;
        return await RunAsync(rules, messages, ct).ConfigureAwait(false);
    }

    /// <summary>Runs one rule now on messages already in a folder (it still never handles the same message twice).</summary>
    public Task<RuleRunResult> RunNowAsync(MailRule rule, IReadOnlyList<MessageSummary> messages, CancellationToken ct = default) =>
        RunAsync([rule], messages, ct);

    private async Task<RuleRunResult> RunAsync(IReadOnlyList<MailRule> rules, IReadOnlyList<MessageSummary> messages, CancellationToken ct)
    {
        var suppressed = new HashSet<Guid>();
        var problems = new List<string>();
        var stopped = new HashSet<Guid>();
        var matched = new HashSet<Guid>();
        var moved = 0;

        foreach (var rule in rules)
        {
            var candidates = messages.Where(m => !stopped.Contains(m.Id) && m.FolderKind is not (FolderKind.Sent or FolderKind.Drafts or FolderKind.Trash or FolderKind.Junk) && rule.Matches(m)).ToList();
            if (candidates.Count == 0) continue;

            var fresh = await store.ClaimAsync(rule.Id, candidates.Select(m => (m, IdentityKeyOf(m))).ToList(), clock.Now, ct).ConfigureAwait(false);
            if (fresh.Count == 0) continue;
            var ids = fresh.Select(m => m.Id).ToList();
            foreach (var id in ids) matched.Add(id);
            if (rule.StopProcessing) foreach (var id in ids) stopped.Add(id);

            foreach (var action in rule.Actions)
            {
                try
                {
                    switch (action.Kind)
                    {
                        case RuleActionKind.MarkRead:
                            await mail.SetReadAsync(fresh.Where(m => !m.IsRead).Select(m => m.Id).ToList(), true, ct).ConfigureAwait(false);
                            break;
                        case RuleActionKind.Star:
                            await mail.SetStarredAsync(fresh.Where(m => !m.IsStarred).Select(m => m.Id).ToList(), true, ct).ConfigureAwait(false);
                            break;
                        case RuleActionKind.AddCategory when !string.IsNullOrWhiteSpace(action.Value):
                            await mail.SetCategoryAsync(ids, action.Value.Trim(), add: true, ct).ConfigureAwait(false);
                            break;
                        case RuleActionKind.SuppressNotification:
                            foreach (var id in ids) suppressed.Add(id);
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    problems.Add($"Rule \"{rule.Name}\": {ex.Message}");
                }
            }

            // Moving comes last, because the message gets a new id in its new folder.
            if (rule.Actions.FirstOrDefault(a => a.Kind == RuleActionKind.MoveToFolder) is { } move && Guid.TryParse(move.Value, out var folderId))
            {
                try
                {
                    moved += (await mail.MoveToFolderAsync(ids, folderId, null, ct).ConfigureAwait(false)).Count;
                    foreach (var id in ids) stopped.Add(id); // later rules see the old copy; don't act on it again
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    problems.Add($"Rule \"{rule.Name}\": {ex.Message}");
                }
            }
        }

        return new RuleRunResult(matched.Count, moved, suppressed, problems);
    }

    /// <summary>A stable key for a message across folders (Message-ID when it has one).</summary>
    public static string IdentityKeyOf(MessageSummary m) => m.MessageIdHeader?.Trim() is { Length: > 0 } id ? id : m.Id.ToString("N");
}
