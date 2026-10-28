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
    public async Task Outbox_sends_once_through_smtp_and_undo_prevents_sending()
    {
        var alice = await _app.Accounts.AddAsync(Request(TestMailServer.User1, "Alice"));
        var bob = await _app.Accounts.AddAsync(Request(TestMailServer.User2, "Bob"));
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var outbox = new Core.Scheduling.OutboxService(
            new SqliteOutboxStore(_app.Cache),
            new OutboxTransport(new DemoMailDataSource(clock), _app.Live),
            clock);
        var sentSubject = "Outbox " + Guid.NewGuid().ToString("N")[..6];
        var undoneSubject = "Undone " + Guid.NewGuid().ToString("N")[..6];
        DraftMessage Draft(string subject) => new() { AccountId = alice.Id, To = [new EmailAddress(null, bob.EmailAddress)], Subject = subject, TextBody = "queued" };

        var keep = await outbox.QueueAsync(Draft(sentSubject), clock.Now.AddSeconds(10), Core.Scheduling.OutboxKind.UndoWindow, isDemo: false);
        var undo = await outbox.QueueAsync(Draft(undoneSubject), clock.Now.AddSeconds(10), Core.Scheduling.OutboxKind.UndoWindow, isDemo: false);
        Assert.NotNull(await outbox.CancelAsync(undo.Id));

        Assert.Equal(0, await outbox.ProcessDueAsync());
        clock.Now = clock.Now.AddSeconds(10);
        Assert.Equal(1, await outbox.ProcessDueAsync());
        Assert.Equal(Core.Scheduling.OutboxState.Accepted, (await outbox.GetItemsAsync(false)).Single(i => i.Id == keep.Id).State);

        await WaitForAsync(_app.Mail, m => m.Subject == sentSubject && m.AccountId == bob.Id);
        await _app.Mail.SyncAsync();
        Assert.Single(await _app.Mail.GetMessagesAsync(), m => m.Subject == sentSubject && m.AccountId == bob.Id);
        Assert.DoesNotContain(await _app.Mail.GetMessagesAsync(), m => m.Subject == undoneSubject);
    }

    [MailServerFact]
    public async Task Large_attachments_are_streamed_to_disk_intact()
    {
        var content = new byte[3 * 1024 * 1024 + 123];
        new Random(42).NextBytes(content);
        using (var imap = await TestMailServer.Connector().ConnectImapAsync(TestMailServer.Imap, new MailCredential(TestMailServer.User1.User, TestMailServer.User1.Password, null)))
        {
            var m = new MimeKit.MimeMessage();
            m.From.Add(new MimeKit.MailboxAddress("Sender", "sender@example.test"));
            m.Subject = "Big attachment";
            var builder = new MimeKit.BodyBuilder { TextBody = "See attached." };
            builder.Attachments.Add("data.bin", content);
            m.Body = builder.ToMessageBody();
            await Infrastructure.Mail.ImapOperations.AppendAsync(imap, "INBOX", m, MailKit.MessageFlags.None, CancellationToken.None);
            await imap.DisconnectAsync(true);
        }

        await _app.Accounts.AddAsync(Request(TestMailServer.User1, "Alice"));
        await _app.Mail.SyncAsync();
        var message = (await _app.Mail.GetMessagesAsync()).Single(m => m.Subject == "Big attachment");

        var reports = new List<double>();
        using var saved = new MemoryStream();
        await _app.Mail.SaveAttachmentAsync(message.Id, 0, saved, new SyncProgress(reports.Add));

        Assert.Equal(content, saved.ToArray());
        Assert.Equal(1.0, reports[^1]);
        Assert.True(reports.Count > 2, "progress is reported while downloading");
        Assert.False(message.IsRead || (await _app.Mail.GetMessageAsync(message.Id))!.IsRead, "saving an attachment doesn't mark the message as read");
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [MailServerFact]
    public async Task Search_finds_downloaded_text_and_older_mail_on_the_server()
    {
        var word = "zebra" + Guid.NewGuid().ToString("N")[..8];
        using (var imap = await TestMailServer.Connector().ConnectImapAsync(TestMailServer.Imap, new MailCredential(TestMailServer.User1.User, TestMailServer.User1.Password, null)))
        {
            for (var i = 0; i < 3; i++)
            {
                var m = new MimeKit.MimeMessage();
                m.From.Add(new MimeKit.MailboxAddress("Archivist", "archive@example.test"));
                m.Subject = $"Search test {i}";
                m.Body = new MimeKit.TextPart("plain") { Text = $"The secret word is {word}." };
                await Infrastructure.Mail.ImapOperations.AppendAsync(imap, "INBOX", m, MailKit.MessageFlags.None, CancellationToken.None);
            }

            await imap.DisconnectAsync(true);
        }

        // Only the newest message is downloaded by the first sync.
        _app.Live.InitialMessageLimit = 1;
        await _app.Accounts.AddAsync(Request(TestMailServer.User1, "Alice"));
        await _app.Mail.SyncAsync();
        var cached = (await _app.Mail.GetMessagesAsync()).Where(m => m.Subject.StartsWith("Search test")).ToList();
        Assert.Single(cached);

        // The word is only in the body: not found until the body has been downloaded.
        Assert.Empty((await _app.Mail.SearchDownloadedTextAsync([word]))[word]);
        await _app.Mail.GetMessageBodyAsync(cached[0].Id);
        Assert.Equal([cached[0].Id], (await _app.Mail.SearchDownloadedTextAsync([word]))[word]);

        // The server finds all three and the two older ones are downloaded so they can be shown.
        var result = await _app.Mail.SearchServerAsync(word, MailboxScope.UnifiedInbox);
        Assert.True(result.Supported, result.Message);
        Assert.Equal(3, result.MessageIds.Count);
        var all = (await _app.Mail.GetMessagesAsync()).Where(m => result.MessageIds.Contains(m.Id)).ToList();
        Assert.Equal(3, all.Count);

        // Server search isn't offered for local-only views.
        Assert.False((await _app.Mail.SearchServerAsync(word, MailboxScope.Pinned)).Supported);

        // A later sync keeps them and doesn't duplicate anything.
        await _app.Mail.SyncAsync();
        Assert.Equal(3, (await _app.Mail.GetMessagesAsync()).Count(m => result.MessageIds.Contains(m.Id)));
        lock (_errors) Assert.Empty(_errors.Select(e => e.Message));
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

        // Exactly one Sent copy, even after the background follow-up work and another sync.
        await Task.Delay(2500);
        await _app.Mail.SyncAsync();
        Assert.Single(await _app.Mail.GetMessagesAsync(), m => m.Subject == subject && m.AccountId == alice.Id && m.FolderKind == FolderKind.Sent);

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
