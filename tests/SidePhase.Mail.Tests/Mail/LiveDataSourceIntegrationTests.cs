using Microsoft.Data.Sqlite;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Settings;
using SidePhase.Mail.Infrastructure.Accounts;
using SidePhase.Mail.Infrastructure.Demo;
using SidePhase.Mail.Infrastructure.Live;
using SidePhase.Mail.Infrastructure.OAuth;
using SidePhase.Mail.Infrastructure.Security;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Tests.Mail;

/// <summary>
/// The whole engine as the app uses it (account setup, router, cache, IMAP, SMTP) against a disposable test server.
/// </summary>
[Collection("Mail server")]
public sealed class LiveDataSourceIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sp-live-" + Guid.NewGuid().ToString("N"));
    private readonly List<MailErrorEventArgs> _errors = [];
    private App _app = null!;

    private sealed record App(
        SettingsService Settings,
        FileSecretStore Secrets,
        MailCache Cache,
        LiveMailDataSource Live,
        MailDataSourceRouter Mail,
        AccountService Accounts) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Live.DisposeAsync();
    }

    private async Task<App> StartAppAsync(InMemorySettingsStore store)
    {
        var paths = new AppDataPaths(_root);
        paths.EnsureCreated();
        var settings = new SettingsService(store);
        await settings.LoadAsync();
        var secrets = new FileSecretStore(paths, new XorProtector());
        var oauthConfig = new OAuthConfiguration(settings, secrets);
        var oauth = new OAuthClient(new HttpClient(), new NoBrowser());
        var credentials = new CredentialProvider(secrets, oauth, oauthConfig);
        var cache = new MailCache(new MailDatabase(paths));
        var connector = TestMailServer.Connector();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var live = new LiveMailDataSource(cache, connector, credentials, clock);
        var router = new MailDataSourceRouter(new DemoMailDataSource(clock), live, settings);
        router.ErrorOccurred += (_, e) => { lock (_errors) _errors.Add(e); };
        var accounts = new AccountService(cache, connector, credentials, new AccountDiscovery(new HttpClient()), oauth, oauthConfig, live, settings);
        return new App(settings, secrets, cache, live, router, accounts);
    }

    private readonly InMemorySettingsStore _store = new();

    public async Task InitializeAsync() => _app = await StartAppAsync(_store);

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static AccountSetupRequest Request((string User, string Password) user, string name) => new()
    {
        EmailAddress = user.User,
        DisplayName = name,
        Provider = MailProviderKind.Imap,
        Password = user.Password,
        Settings = new AccountConnectionSettings
        {
            Imap = TestMailServer.Imap,
            Smtp = TestMailServer.Smtp,
            Username = user.User,
            AuthMethod = AuthMethod.Password,
        },
    };

    private static async Task<MessageSummary> WaitForAsync(IMailDataSource mail, Func<MessageSummary, bool> match)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await mail.SyncAsync();
            if ((await mail.GetMessagesAsync()).FirstOrDefault(match) is { } found) return found;
            await Task.Delay(500);
        }

        throw new TimeoutException("The message didn't arrive.");
    }

    [MailServerFact]
    public async Task Failed_connection_test_saves_nothing()
    {
        var request = Request((TestMailServer.User1.User, "wrong-password"), "Alice");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _app.Accounts.AddAsync(request));
        Assert.DoesNotContain("wrong-password", ex.Message);
        Assert.Empty(await _app.Cache.GetAccountsAsync());
        Assert.False(Directory.Exists(Path.Combine(_root, "Secrets")) && Directory.EnumerateFiles(Path.Combine(_root, "Secrets")).Any());
        Assert.Equal(DataMode.Demo, _app.Settings.Current.DataMode);
    }

    [MailServerFact]
    public async Task Real_accounts_end_to_end()
    {
        // Demo mode first: the router serves only sample data.
        Assert.True(_app.Mail.IsDemo);
        var demoIds = (await _app.Mail.GetMessagesAsync()).Select(m => m.Id).ToHashSet();
        Assert.NotEmpty(demoIds);

        // Add two real accounts. The first switches the app to real accounts; no demo message remains visible.
        var alice = await _app.Accounts.AddAsync(Request(TestMailServer.User1, "Alice"));
        var bob = await _app.Accounts.AddAsync(Request(TestMailServer.User2, "Bob"));
        Assert.False(_app.Mail.IsDemo);
        Assert.Equal(2, (await _app.Mail.GetAccountsAsync()).Count);
        Assert.DoesNotContain(await _app.Mail.GetMessagesAsync(), m => demoIds.Contains(m.Id));
        Assert.Null(await _app.Secrets.GetAsync("account/" + Guid.NewGuid().ToString("N") + "/password"));

        // Folders discovered for both accounts.
        await _app.Mail.SyncAsync();
        Assert.Contains(await _app.Mail.GetFoldersAsync(bob.Id), f => f.Kind == FolderKind.Inbox);
        Assert.Contains(await _app.Mail.GetFoldersAsync(alice.Id), f => f.Kind == FolderKind.Sent);

        // Send from Alice to Bob.
        var subject = "Live " + Guid.NewGuid().ToString("N")[..8];
        var sent = await _app.Mail.SendAsync(new DraftMessage
        {
            AccountId = alice.Id,
            To = [new EmailAddress("Bob", bob.EmailAddress)],
            Subject = subject,
            TextBody = "First message",
        });
        Assert.True(sent.Succeeded, sent.Message);

        // Bob receives it in the unified inbox; Alice's Sent folder has a copy.
        var received = await WaitForAsync(_app.Mail, m => m.Subject == subject && m.AccountId == bob.Id && m.FolderKind == FolderKind.Inbox);
        Assert.False(received.IsRead);
        await WaitForAsync(_app.Mail, m => m.Subject == subject && m.AccountId == alice.Id && m.FolderKind == FolderKind.Sent);

        var body = await _app.Mail.GetMessageBodyAsync(received.Id);
        Assert.Contains("First message", body!.TextBody);

        // Read state is written to the server (it survives a resync).
        await _app.Mail.SetReadAsync([received.Id], true);
        await Task.Delay(1500);
        await _app.Mail.SyncAsync();
        Assert.True((await _app.Mail.GetMessageAsync(received.Id))!.IsRead);

        // Star (IMAP \Flagged) round-trips too.
        await _app.Mail.SetStarredAsync([received.Id], true);
        await Task.Delay(1500);
        await _app.Mail.SyncAsync();
        Assert.True((await _app.Mail.GetMessageAsync(received.Id))!.IsStarred);

        // Bob replies; Alice gets a threaded reply.
        var reply = await _app.Mail.SendAsync(new DraftMessage
        {
            AccountId = bob.Id,
            Mode = ComposeMode.Reply,
            SourceMessageId = received.Id,
            To = [new EmailAddress(null, alice.EmailAddress)],
            Subject = "Re: " + subject,
            TextBody = "Got it",
        });
        Assert.True(reply.Succeeded, reply.Message);
        var replyReceived = await WaitForAsync(_app.Mail, m => m.Subject == "Re: " + subject && m.AccountId == alice.Id && m.FolderKind == FolderKind.Inbox);
        var (_, references) = await _app.Cache.GetThreadHeadersAsync(replyReceived.Id);
        Assert.Contains(received.MessageIdHeader!, references);

        // Pin, then archive: the pin follows the message, and undo brings it back.
        await _app.Mail.SetPinnedAsync([received.Id], true);
        var moves = await _app.Mail.MoveToAsync([received.Id], FolderKind.Archive);
        var move = Assert.Single(moves);
        var archived = (await _app.Mail.GetMessageAsync(move.MessageId))!;
        Assert.Equal(FolderKind.Archive, archived.FolderKind);
        Assert.True(archived.IsPinned);
        Assert.Null(await _app.Mail.GetMessageAsync(received.Id));
        Assert.NotNull(await _app.Mail.GetMessageBodyAsync(move.MessageId));

        await _app.Mail.RestoreAsync(moves);
        var restored = (await _app.Mail.GetMessagesAsync()).Single(m => m.Subject == subject && m.AccountId == bob.Id);
        Assert.Equal(FolderKind.Inbox, restored.FolderKind);
        Assert.True(restored.IsPinned);

        // Drafts are saved to the server and replaced when saved again.
        var draftId = await _app.Mail.SaveDraftAsync(new DraftMessage { AccountId = alice.Id, Subject = "Draft A", TextBody = "one" });
        Assert.Equal(FolderKind.Drafts, (await _app.Mail.GetMessageAsync(draftId))!.FolderKind);
        var draftId2 = await _app.Mail.SaveDraftAsync(new DraftMessage { DraftId = draftId, AccountId = alice.Id, Subject = "Draft B", TextBody = "two" });
        Assert.NotEqual(draftId, draftId2);
        Assert.Null(await _app.Mail.GetMessageAsync(draftId));
        Assert.Equal("Draft B", (await _app.Mail.GetDraftAsync(draftId2))!.Subject);
        await _app.Mail.SyncAsync();
        Assert.Single(await _app.Mail.GetMessagesAsync(), m => m.AccountId == alice.Id && m.FolderKind == FolderKind.Drafts && m.Subject.StartsWith("Draft "));

        // Trash then permanent delete (both explicit).
        var trashed = Assert.Single(await _app.Mail.MoveToAsync([draftId2], FolderKind.Trash));
        await _app.Mail.DeletePermanentlyAsync([trashed.MessageId]);
        Assert.Null(await _app.Mail.GetMessageAsync(trashed.MessageId));

        // "Restart": a new engine on the same database and secret store. Accounts, pins and credentials persist.
        await _app.DisposeAsync();
        _app = await StartAppAsync(_store);
        _app.Mail.ErrorOccurred += (_, e) => { lock (_errors) _errors.Add(e); };
        Assert.False(_app.Mail.IsDemo);
        Assert.Equal(2, (await _app.Mail.GetAccountsAsync()).Count);
        var afterRestart = (await _app.Mail.GetMessagesAsync()).Single(m => m.Subject == subject && m.AccountId == bob.Id);
        Assert.True(afterRestart.IsPinned);
        var sync = await _app.Mail.SyncAsync();
        Assert.True(sync.Succeeded, sync.Message);

        // Removing an account deletes its cache and credentials, never server mail.
        await _app.Accounts.RemoveAsync(alice.Id);
        Assert.Single(await _app.Mail.GetAccountsAsync());
        Assert.Null(await _app.Secrets.GetAsync(SecretKeys.Password(alice.Id)));
        Assert.NotNull(await _app.Secrets.GetAsync(SecretKeys.Password(bob.Id)));
        Assert.DoesNotContain(await _app.Mail.GetMessagesAsync(), m => m.AccountId == alice.Id);

        lock (_errors) Assert.Empty(_errors.Select(e => e.Message));
    }
}
