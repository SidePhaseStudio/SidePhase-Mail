using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Contacts;

public sealed record ContactEmail(string Address, string? Label = null);

/// <summary>
/// A person in the local address book. Contacts are stored only on this PC. <see cref="Source"/> records where a
/// contact came from ("local", "vcard", and later sync sources such as "google", "microsoft" or "carddav"), so
/// synced contacts can be kept apart from ones you created.
/// </summary>
public sealed record Contact
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public IReadOnlyList<ContactEmail> Emails { get; init; } = [];
    public string? Notes { get; init; }
    public string Source { get; init; } = ContactSources.Local;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public string? PrimaryEmail => Emails.Count > 0 ? Emails[0].Address : null;
}

public static class ContactSources
{
    public const string Local = "local";
    public const string VCard = "vcard";
}

/// <summary>Someone you've sent mail to, remembered for autocomplete.</summary>
public sealed record RecentRecipient(string Email, string? DisplayName, DateTimeOffset LastUsed, int UseCount);

/// <summary>An autocomplete suggestion for a recipient field.</summary>
public sealed record RecipientSuggestion(EmailAddress Address, bool IsContact)
{
    public string Text => string.IsNullOrWhiteSpace(Address.Name) ? Address.Address : $"{Address.Name} <{Address.Address}>";
}

public interface IContactStore
{
    Task<IReadOnlyList<Contact>> ListAsync(CancellationToken ct = default);
    Task<Contact?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds or replaces a contact (and its addresses).</summary>
    Task SaveAsync(Contact contact, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<RecentRecipient>> GetRecentAsync(int limit, CancellationToken ct = default);
    Task RecordRecipientsAsync(IEnumerable<EmailAddress> recipients, DateTimeOffset when, CancellationToken ct = default);
    Task ForgetRecentAsync(string email, CancellationToken ct = default);
}

/// <summary>
/// A place contacts can be synced from in the future (Google, Microsoft, CardDAV). Nothing implements it yet; the
/// store keeps each contact's source so a sync can update only its own contacts.
/// </summary>
public interface IContactSyncSource
{
    string SourceId { get; }
    Task<IReadOnlyList<Contact>> FetchAsync(CancellationToken ct = default);
}
