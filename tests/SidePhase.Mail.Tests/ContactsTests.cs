using Microsoft.Data.Sqlite;
using SidePhase.Mail.Core.Contacts;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Tests;

public class VCardParserTests
{
    [Fact]
    public void Reads_names_emails_labels_and_notes_from_vcard_3_and_4()
    {
        const string text = """
            BEGIN:VCARD
            VERSION:3.0
            FN:Alice Example
            N:Example;Alice;;;
            EMAIL;TYPE=INTERNET,WORK:alice@work.example
            item1.EMAIL;TYPE=HOME:mailto:alice@home.example
            NOTE:Met at the conference\, room 4\nCall first
            END:VCARD
            BEGIN:VCARD
            VERSION:4.0
            N:Builder;Bob;;;
            EMAIL:bob@example.test
            END:VCARD
            """;

        var cards = VCardParser.Parse(text);

        Assert.Equal(2, cards.Count);
        Assert.Equal("Alice Example", cards[0].DisplayName);
        Assert.Equal(["alice@work.example", "alice@home.example"], cards[0].Emails.Select(e => e.Address));
        Assert.Equal(["work", "home"], cards[0].Emails.Select(e => e.Label));
        Assert.Equal("Met at the conference, room 4\nCall first", cards[0].Notes);
        Assert.Equal("Bob Builder", cards[1].DisplayName);
    }

    [Fact]
    public void Handles_folded_lines_vcard_2_1_quoted_printable_and_bad_cards()
    {
        var text = "BEGIN:VCARD\r\nVERSION:2.1\r\nFN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:Ren=C3=A9e =\r\nDupont\r\n"
            + "EMAIL;INTERNET;HOME:renee@exam\r\n ple.test\r\nEND:VCARD\r\n"
            + "BEGIN:VCARD\r\nVERSION:3.0\r\nEMAIL:not-an-email\r\nEND:VCARD\r\n"
            + "BEGIN:VCARD\r\nFN:Unterminated\r\n";

        var cards = VCardParser.Parse(text);

        Assert.Equal("Renée Dupont", cards[0].DisplayName);
        Assert.Equal("renee@example.test", Assert.Single(cards[0].Emails).Address);
        Assert.Equal("home", cards[0].Emails[0].Label);
        Assert.Single(cards); // the card without a name or valid address, and the unterminated one, are skipped
    }

    [Fact]
    public void Refuses_absurdly_large_files() =>
        Assert.Throws<InvalidDataException>(() => VCardParser.Parse(new string('x', VCardParser.MaxFileCharacters + 1)));
}

public class ContactServiceTests
{
    private readonly InMemoryContactStore _store = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
    private readonly ContactService _contacts;

    public ContactServiceTests() => _contacts = new ContactService(_store, _clock);

    private Task<Contact> Add(string name, params string[] emails) =>
        _contacts.SaveAsync(new Contact { Id = Guid.NewGuid(), DisplayName = name, Emails = emails.Select(e => new ContactEmail(e)).ToList() });

    [Fact]
    public async Task Autocomplete_matches_name_words_and_addresses_and_ranks_frequent_recipients_first()
    {
        await Add("Alice Smith", "alice@example.test");
        await Add("Alan Jones", "alan@example.test");
        await _store.RecordRecipientsAsync([new EmailAddress("Alan Jones", "alan@example.test")], _clock.Now.AddDays(-1));
        await _store.RecordRecipientsAsync([new EmailAddress("Alan Jones", "alan@example.test")], _clock.Now.AddDays(-1));
        await _store.RecordRecipientsAsync([new EmailAddress(null, "albert@elsewhere.test")], _clock.Now.AddDays(-30));

        var al = await _contacts.SuggestAsync("al");
        Assert.Equal(["alan@example.test", "alice@example.test", "albert@elsewhere.test"], al.Select(s => s.Address.Address));

        var smith = Assert.Single(await _contacts.SuggestAsync("smi"));
        Assert.Equal("Alice Smith <alice@example.test>", smith.Text);
        Assert.True(smith.IsContact);

        Assert.Empty(await _contacts.SuggestAsync("al", exclude: ["alan@example.test", "alice@example.test", "albert@elsewhere.test"]));
        Assert.Empty(await _contacts.SuggestAsync("   "));
    }

    [Fact]
    public async Task Search_finds_contacts_by_any_word_or_address()
    {
        await Add("Alice Smith", "alice@example.test", "a.smith@work.test");
        await Add("Bob", "bob@example.test");
        Assert.Equal("Alice Smith", Assert.Single(await _contacts.SearchAsync("work.test")).DisplayName);
        Assert.Equal(2, (await _contacts.SearchAsync("")).Count);
    }

    [Fact]
    public async Task Saving_validates_addresses_and_removes_duplicates()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Add("Bad", "nope"));
        await Assert.ThrowsAsync<ArgumentException>(() => Add("  "));
        var saved = await Add("", "x@example.test", "X@EXAMPLE.test");
        Assert.Equal("x@example.test", saved.DisplayName);
        Assert.Single(saved.Emails);
    }

    [Fact]
    public async Task Importing_a_vcard_merges_people_already_in_the_address_book()
    {
        await Add("Alice", "alice@example.test");
        const string text = """
            BEGIN:VCARD
            FN:Alice Smith
            EMAIL:ALICE@example.test
            EMAIL:alice@work.test
            END:VCARD
            BEGIN:VCARD
            FN:Carol
            EMAIL:carol@example.test
            END:VCARD
            BEGIN:VCARD
            FN:No Email
            END:VCARD
            """;

        var result = await _contacts.ImportVCardAsync(text);

        Assert.Equal(new VCardImportResult(1, 1, 1), result);
        var all = await _contacts.ListAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(["alice@example.test", "alice@work.test"], all.Single(c => c.DisplayName == "Alice").Emails.Select(e => e.Address));
        Assert.Equal(ContactSources.VCard, all.Single(c => c.DisplayName == "Carol").Source);

        // Importing the same file again changes nothing.
        Assert.Equal(new VCardImportResult(0, 0, 3), await _contacts.ImportVCardAsync(text));
    }

    [Fact]
    public async Task Sent_messages_are_remembered_for_autocomplete()
    {
        await _contacts.RecordSentAsync(new DraftMessage
        {
            To = [new EmailAddress("Dana", "dana@example.test")],
            Bcc = [new EmailAddress(null, "eve@example.test")],
        });
        Assert.Equal("Dana <dana@example.test>", Assert.Single(await _contacts.SuggestAsync("dan")).Text);
        await _contacts.ForgetRecentAsync("dana@example.test");
        Assert.Empty(await _contacts.SuggestAsync("dan"));
    }
}

public sealed class SqliteContactStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-contacts-" + Guid.NewGuid().ToString("N"));

    public SqliteContactStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private SqliteContactStore Store() => new(new MailCache(new MailDatabase(Path.Combine(_dir, "c.db"))));

    [Fact]
    public async Task Contacts_and_recent_recipients_persist_across_restarts()
    {
        var id = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
        var store = Store();
        await store.SaveAsync(new Contact
        {
            Id = id,
            DisplayName = "Alice",
            Emails = [new ContactEmail("alice@example.test", "work"), new ContactEmail("alice@home.test")],
            Notes = "Friend",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await store.RecordRecipientsAsync([new EmailAddress("Bob", "bob@example.test")], now);
        await store.RecordRecipientsAsync([new EmailAddress(null, "BOB@example.test")], now.AddHours(1));

        var reopened = Store();
        var contact = Assert.Single(await reopened.ListAsync());
        Assert.Equal("Alice", contact.DisplayName);
        Assert.Equal(["alice@example.test", "alice@home.test"], contact.Emails.Select(e => e.Address));
        Assert.Equal("work", contact.Emails[0].Label);
        Assert.Equal("Friend", contact.Notes);

        var recent = Assert.Single(await reopened.GetRecentAsync(10));
        Assert.Equal(2, recent.UseCount);
        Assert.Equal("Bob", recent.DisplayName);
        Assert.Equal(now.AddHours(1), recent.LastUsed);

        await reopened.SaveAsync(contact with { Emails = [new ContactEmail("alice@new.test")] });
        Assert.Equal("alice@new.test", Assert.Single((await reopened.GetAsync(id))!.Emails).Address);

        await reopened.DeleteAsync(id);
        Assert.Empty(await reopened.ListAsync());
    }
}

internal sealed class InMemoryContactStore : IContactStore
{
    private readonly Dictionary<Guid, Contact> _contacts = [];
    private readonly Dictionary<string, RecentRecipient> _recent = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<Contact>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Contact>>(_contacts.Values.ToList());
    public Task<Contact?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_contacts.GetValueOrDefault(id));

    public Task SaveAsync(Contact contact, CancellationToken ct = default)
    {
        _contacts[contact.Id] = contact;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        _contacts.Remove(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RecentRecipient>> GetRecentAsync(int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecentRecipient>>(_recent.Values.OrderByDescending(r => r.LastUsed).Take(limit).ToList());

    public Task RecordRecipientsAsync(IEnumerable<EmailAddress> recipients, DateTimeOffset when, CancellationToken ct = default)
    {
        foreach (var r in recipients)
        {
            _recent[r.Address] = _recent.TryGetValue(r.Address, out var e)
                ? e with { DisplayName = r.Name ?? e.DisplayName, LastUsed = when > e.LastUsed ? when : e.LastUsed, UseCount = e.UseCount + 1 }
                : new RecentRecipient(r.Address, r.Name, when, 1);
        }

        return Task.CompletedTask;
    }

    public Task ForgetRecentAsync(string email, CancellationToken ct = default)
    {
        _recent.Remove(email);
        return Task.CompletedTask;
    }
}

public class RecipientTokenTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("al", "al")]
    [InlineData("bob@example.test; al", "al")]
    [InlineData("\"Smith, Bob\" <bob@example.test>, ca", "ca")]
    [InlineData("bob@example.test; ", "")]
    public void The_last_token_is_what_is_being_typed(string text, string expected) =>
        Assert.Equal(expected, SidePhase.Mail.Core.Composition.RecipientParser.LastToken(text));

    [Fact]
    public void Choosing_a_suggestion_replaces_only_the_last_token()
    {
        var alice = new EmailAddress("Alice Smith", "alice@example.test");
        Assert.Equal("Alice Smith <alice@example.test>; ", SidePhase.Mail.Core.Composition.RecipientParser.ReplaceLastToken("ali", alice));
        Assert.Equal(
            "\"Smith, Bob\" <bob@example.test>, Alice Smith <alice@example.test>; ",
            SidePhase.Mail.Core.Composition.RecipientParser.ReplaceLastToken("\"Smith, Bob\" <bob@example.test>, al", alice));
    }
}

public sealed class DraftRecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-recovery-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Copies_survive_a_restart_until_deleted_and_damaged_copies_are_skipped()
    {
        var store = new FileDraftRecoveryStore(new AppDataPaths(_dir));
        var session = Guid.NewGuid();
        var draft = new DraftMessage { AccountId = Guid.NewGuid(), Subject = "Half written", TextBody = "Dear Bob,", To = [new EmailAddress(null, "bob@example.test")] };

        await store.SaveAsync(session, draft);
        await store.SaveAsync(session, draft with { TextBody = "Dear Bob, more" });
        File.WriteAllText(Path.Combine(_dir, "Recovery", Guid.NewGuid().ToString("N") + ".json"), "{ not json");

        var reopened = new FileDraftRecoveryStore(new AppDataPaths(_dir));
        var recovered = Assert.Single(await reopened.ListAsync());
        Assert.Equal(session, recovered.SessionId);
        Assert.Equal("Dear Bob, more", recovered.Draft.TextBody);
        Assert.Equal("bob@example.test", recovered.Draft.To[0].Address);

        await reopened.DeleteAsync(session);
        Assert.Empty(await reopened.ListAsync());
    }
}
