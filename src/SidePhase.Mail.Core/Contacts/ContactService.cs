using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Contacts;

public sealed record VCardImportResult(int Added, int Updated, int Skipped);

/// <summary>
/// The local address book: contacts you've added or imported plus people you've recently sent mail to. Everything
/// stays on this PC; nothing is uploaded anywhere.
/// </summary>
public sealed class ContactService(IContactStore store, IClock clock)
{
    /// <summary>Raised after contacts are added, changed or removed.</summary>
    public event EventHandler? Changed;

    public Task<IReadOnlyList<Contact>> ListAsync(CancellationToken ct = default) => store.ListAsync(ct);

    /// <summary>Contacts whose name or any address contains every word of <paramref name="query"/>, sorted by name.</summary>
    public async Task<IReadOnlyList<Contact>> SearchAsync(string? query, CancellationToken ct = default)
    {
        var all = await store.ListAsync(ct).ConfigureAwait(false);
        var words = Words(query);
        return all.Where(c => words.All(w => c.DisplayName.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                || c.Emails.Any(e => e.Address.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Suggestions for a recipient field as the user types. Contacts and recent recipients whose name or address
    /// starts with what was typed come first; people you write to often and recently rank higher.
    /// </summary>
    public async Task<IReadOnlyList<RecipientSuggestion>> SuggestAsync(string typed, int limit = 8, IEnumerable<string>? exclude = null, CancellationToken ct = default)
    {
        var text = typed.Trim();
        if (text.Length == 0) return [];
        var excluded = new HashSet<string>(exclude ?? [], StringComparer.OrdinalIgnoreCase);
        var now = clock.Now;

        var recent = (await store.GetRecentAsync(500, ct).ConfigureAwait(false)).ToDictionary(r => r.Email, StringComparer.OrdinalIgnoreCase);
        var candidates = new Dictionary<string, (RecipientSuggestion Suggestion, double Score)>(StringComparer.OrdinalIgnoreCase);

        void Consider(string email, string? name, bool isContact)
        {
            if (excluded.Contains(email)) return;
            var match = MatchScore(text, email, name);
            if (match == 0) return;

            var score = match + (isContact ? 1.5 : 0);
            if (recent.TryGetValue(email, out var r))
            {
                var ageDays = Math.Max(0, (now - r.LastUsed).TotalDays);
                score += Math.Log(1 + r.UseCount) + 2.0 / (1 + ageDays / 7);
            }

            if (!candidates.TryGetValue(email, out var existing) || existing.Score < score)
            {
                candidates[email] = (new RecipientSuggestion(new EmailAddress(string.IsNullOrWhiteSpace(name) ? null : name, email), isContact), score);
            }
        }

        foreach (var contact in await store.ListAsync(ct).ConfigureAwait(false))
        {
            foreach (var email in contact.Emails) Consider(email.Address, contact.DisplayName, isContact: true);
        }

        foreach (var r in recent.Values) Consider(r.Email, r.DisplayName, isContact: false);

        return candidates.Values.OrderByDescending(c => c.Score).ThenBy(c => c.Suggestion.Text, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit).Select(c => c.Suggestion).ToList();
    }

    public async Task<Contact> SaveAsync(Contact contact, CancellationToken ct = default)
    {
        var now = clock.Now;
        var cleaned = contact with
        {
            DisplayName = contact.DisplayName.Trim(),
            Emails = contact.Emails
                .Select(e => e with { Address = e.Address.Trim() })
                .Where(e => e.Address.Length > 0)
                .DistinctBy(e => e.Address, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Notes = string.IsNullOrWhiteSpace(contact.Notes) ? null : contact.Notes.Trim(),
            CreatedAt = contact.CreatedAt == default ? now : contact.CreatedAt,
            UpdatedAt = now,
        };

        if (cleaned.DisplayName.Length == 0 && cleaned.PrimaryEmail is { } email) cleaned = cleaned with { DisplayName = email };
        if (cleaned.DisplayName.Length == 0) throw new ArgumentException("A contact needs a name or an email address.");
        var invalid = cleaned.Emails.FirstOrDefault(e => !VCardParser.LooksLikeEmail(e.Address));
        if (invalid is not null) throw new ArgumentException($"\"{invalid.Address}\" isn't a valid email address.");

        await store.SaveAsync(cleaned, ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return cleaned;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await store.DeleteAsync(id, ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Remembers who a message was sent to, for autocomplete.</summary>
    public Task RecordSentAsync(DraftMessage draft, CancellationToken ct = default) =>
        store.RecordRecipientsAsync(draft.To.Concat(draft.Cc).Concat(draft.Bcc), clock.Now, ct);

    public Task ForgetRecentAsync(string email, CancellationToken ct = default) => store.ForgetRecentAsync(email, ct);

    /// <summary>
    /// Adds the people in a vCard file. Someone whose address is already in the address book is merged into that
    /// contact (new addresses and notes are added) rather than duplicated.
    /// </summary>
    public async Task<VCardImportResult> ImportVCardAsync(string text, CancellationToken ct = default)
    {
        var cards = VCardParser.Parse(text);
        var existing = (await store.ListAsync(ct).ConfigureAwait(false)).ToList();
        int added = 0, updated = 0, skipped = 0;
        foreach (var card in cards)
        {
            if (card.Emails.Count == 0)
            {
                skipped++; // a mail app's address book is for people you can email
                continue;
            }

            var match = existing.FirstOrDefault(c => c.Emails.Any(e => card.Emails.Any(n => n.Address.Equals(e.Address, StringComparison.OrdinalIgnoreCase))));
            if (match is null)
            {
                var contact = await SaveCoreAsync(new Contact
                {
                    Id = Guid.NewGuid(),
                    DisplayName = card.DisplayName,
                    Emails = card.Emails,
                    Notes = card.Notes,
                    Source = ContactSources.VCard,
                }, ct).ConfigureAwait(false);
                existing.Add(contact);
                added++;
                continue;
            }

            var newEmails = card.Emails.Where(n => !match.Emails.Any(e => e.Address.Equals(n.Address, StringComparison.OrdinalIgnoreCase))).ToList();
            var notes = string.IsNullOrEmpty(match.Notes) ? card.Notes : match.Notes;
            if (newEmails.Count == 0 && notes == match.Notes)
            {
                skipped++;
                continue;
            }

            var merged = await SaveCoreAsync(match with { Emails = match.Emails.Concat(newEmails).ToList(), Notes = notes }, ct).ConfigureAwait(false);
            existing[existing.IndexOf(match)] = merged;
            updated++;
        }

        if (added + updated > 0) Changed?.Invoke(this, EventArgs.Empty);
        return new VCardImportResult(added, updated, skipped);
    }

    private async Task<Contact> SaveCoreAsync(Contact contact, CancellationToken ct)
    {
        var now = clock.Now;
        var stored = contact with { CreatedAt = contact.CreatedAt == default ? now : contact.CreatedAt, UpdatedAt = now };
        await store.SaveAsync(stored, ct).ConfigureAwait(false);
        return stored;
    }

    /// <summary>3 = address or name starts with the text, 2 = a word of the name starts with it, 1 = contains it.</summary>
    private static double MatchScore(string text, string email, string? name)
    {
        if (email.StartsWith(text, StringComparison.OrdinalIgnoreCase)) return 3;
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (name.StartsWith(text, StringComparison.CurrentCultureIgnoreCase)) return 3;
            if (name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(text, StringComparison.CurrentCultureIgnoreCase))) return 2;
        }

        var at = email.IndexOf('@');
        if (at > 0 && email[(at + 1)..].StartsWith(text, StringComparison.OrdinalIgnoreCase)) return 1.5;
        return text.Length >= 3 && (email.Contains(text, StringComparison.OrdinalIgnoreCase) || (name?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false)) ? 1 : 0;
    }

    private static string[] Words(string? query) => (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
