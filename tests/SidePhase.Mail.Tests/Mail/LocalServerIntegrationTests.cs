using MailKit;
using MailKit.Net.Imap;
using Microsoft.Data.Sqlite;
using MimeKit;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Infrastructure.Accounts;
using SidePhase.Mail.Infrastructure.Mail;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Tests.Mail;

/// <summary>
/// Settings for a disposable test mail server (Dovecot/Postfix locally, GreenMail in CI). Never real accounts.
/// </summary>
internal static class TestMailServer
{
    public static string? ImapHost => Environment.GetEnvironmentVariable("SIDEPHASE_TEST_IMAP_HOST");

    public static bool IsConfigured => !string.IsNullOrEmpty(ImapHost);

    public static ServerEndpoint Imap => new(ImapHost!, Port("SIDEPHASE_TEST_IMAP_PORT", 993), Security("SIDEPHASE_TEST_IMAP_SECURITY"));

    public static ServerEndpoint Smtp => new(Environment.GetEnvironmentVariable("SIDEPHASE_TEST_SMTP_HOST") ?? ImapHost!, Port("SIDEPHASE_TEST_SMTP_PORT", 465), Security("SIDEPHASE_TEST_SMTP_SECURITY"));

    public static (string User, string Password) User1 => (Env("SIDEPHASE_TEST_USER1", "alice@example.test"), Env("SIDEPHASE_TEST_PASSWORD1", "alice-secret"));

    public static (string User, string Password) User2 => (Env("SIDEPHASE_TEST_USER2", "bob@example.test"), Env("SIDEPHASE_TEST_PASSWORD2", "bob-secret"));

    public static MailConnector Connector() => new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        // Test servers use throwaway self-signed certificates. The app itself never sets this.
        CertificateValidationOverride = (_, _, _, _) => true,
    };

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    private static int Port(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var p) ? p : fallback;

    private static ConnectionSecurity Security(string name) =>
        string.Equals(Environment.GetEnvironmentVariable(name), "starttls", StringComparison.OrdinalIgnoreCase) ? ConnectionSecurity.StartTls : ConnectionSecurity.SslOnConnect;
}

internal sealed class MailServerFactAttribute : FactAttribute
{
    public MailServerFactAttribute()
    {
        if (!TestMailServer.IsConfigured) Skip = "Set SIDEPHASE_TEST_IMAP_HOST (and related variables) to run against a test mail server.";
    }
}

[Collection("Mail server")]
public sealed class LocalServerIntegrationTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-int-" + Guid.NewGuid().ToString("N"));
    private MailCache _cache = null!;
    private readonly Guid _bob = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _cache = new MailCache(new MailDatabase(Path.Combine(_dir, "int.db")));
        await _cache.InitializeAsync();
        if (TestMailServer.IsConfigured)
        {
            await _cache.AddAccountAsync(MailCacheTests.Account(_bob, TestMailServer.User2.User));
        }
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private static Task<ImapClient> ImapAsync((string User, string Password) user) =>
        TestMailServer.Connector().ConnectImapAsync(TestMailServer.Imap, new MailCredential(user.User, user.Password, null));

    [MailServerFact]
    public async Task Wrong_password_is_reported_as_a_sign_in_problem()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ImapAsync((TestMailServer.User1.User, "definitely-wrong")));
        var friendly = MailErrorTranslator.Describe(ex, TestMailServer.Imap.Host);
        Assert.True(friendly.RequiresSignIn, friendly.Message);
        Assert.DoesNotContain("definitely-wrong", friendly.Message);
    }

    [MailServerFact]
    public async Task Send_receive_read_flag_move_and_delete_round_trip()
    {
        var subject = "SidePhase integration " + Guid.NewGuid().ToString("N")[..8];
        var alice = new MailAccount { Id = Guid.NewGuid(), DisplayName = "Alice", EmailAddress = TestMailServer.User1.User, Provider = MailProviderKind.Imap };
        var attachment = Path.Combine(_dir, "report.txt");
        await File.WriteAllTextAsync(attachment, "quarterly numbers");

        // 1. Send with SMTP (authenticated, encrypted) from Alice to Bob.
        var draft = new DraftMessage
        {
            To = [new EmailAddress("Bob", TestMailServer.User2.User)],
            Subject = subject,
            TextBody = "Hello Bob",
            Attachments = [new DraftAttachment("report.txt", 17, attachment, "text/plain")],
        };
        var mime = MimeMessageBuilder.Build(draft, alice, null, null);
        var progress = new List<double>();
        using (var smtp = await TestMailServer.Connector().ConnectSmtpAsync(TestMailServer.Smtp, new MailCredential(TestMailServer.User1.User, TestMailServer.User1.Password, null)))
        {
            await SmtpSender.SendAsync(smtp, mime, new SyncProgress(progress), CancellationToken.None);
            await smtp.DisconnectAsync(true);
        }

        Assert.Equal(1.0, progress[^1]);

        // 2. Bob syncs: folder discovery, then the inbox until the message arrives.
        using var imap = await ImapAsync(TestMailServer.User2);
        var sync = new ImapSynchronizer(_cache);
        var folders = await sync.SyncFolderListAsync(imap, _bob);
        Assert.Contains(folders, f => f.Kind == FolderKind.Inbox);
        Assert.Contains(folders, f => f.Kind == FolderKind.Sent);
        Assert.Contains(folders, f => f.Kind == FolderKind.Trash);
        var archiveFolder = folders.FirstOrDefault(f => f.Kind == FolderKind.Archive) ?? folders.First(f => f.Kind == FolderKind.Trash);
        var inbox = folders.Single(f => f.Kind == FolderKind.Inbox);

        Core.Models.MessageSummary? received = null;
        for (var attempt = 0; attempt < 30 && received is null; attempt++)
        {
            inbox = (await _cache.GetFoldersAsync(_bob)).Single(f => f.Id == inbox.Id);
            await sync.SyncFolderAsync(imap, inbox);
            received = (await _cache.GetSummariesAsync()).FirstOrDefault(m => m.Subject == subject);
            if (received is null) await Task.Delay(500);
        }

        Assert.NotNull(received);
        Assert.False(received.IsRead);
        Assert.True(received.HasAttachments);
        Assert.Equal(TestMailServer.User1.User, received.From.Address);
        Assert.Equal(mime.MessageId, received.MessageIdHeader);

        // A second sync is incremental and doesn't duplicate.
        inbox = (await _cache.GetFoldersAsync(_bob)).Single(f => f.Id == inbox.Id);
        var again = await sync.SyncFolderAsync(imap, inbox);
        Assert.Empty(again.NewMessageIds);
        Assert.Single(await _cache.GetSummariesAsync(), m => m.Subject == subject);

        // 3. Body on demand (BODY.PEEK: still unread on the server) with the attachment.
        var reference = (await _cache.GetMessageRefAsync(received.Id))!;
        var full = await ImapOperations.FetchMessageAsync(imap, reference.FolderPath, reference.Uid, CancellationToken.None);
        var body = MessageBodyExtractor.Extract(received.Id, full);
        Assert.Contains("Hello Bob", body.TextBody);
        Assert.Equal("report.txt", Assert.Single(body.Attachments).FileName);
        Assert.Equal("quarterly numbers", System.Text.Encoding.UTF8.GetString(MessageBodyExtractor.GetAttachmentContent(full, 0)));

        inbox = (await _cache.GetFoldersAsync(_bob)).Single(f => f.Id == inbox.Id);
        await sync.SyncFolderAsync(imap, inbox);
        Assert.False((await _cache.GetSummaryAsync(received.Id))!.IsRead);

        // 4. Read and starred flags are written to the server and come back on the next sync.
        await ImapOperations.SetFlagsAsync(imap, reference.FolderPath, reference.Uid, MessageFlags.Seen | MessageFlags.Flagged, add: true, CancellationToken.None);
        await _cache.SetFlagsAsync(received.Id, isRead: false, isStarred: false); // pretend the cache is stale
        inbox = (await _cache.GetFoldersAsync(_bob)).Single(f => f.Id == inbox.Id);
        var flagSync = await sync.SyncFolderAsync(imap, inbox);
        Assert.True(flagSync.FlagChanges >= 1);
        var flagged = (await _cache.GetSummaryAsync(received.Id))!;
        Assert.True(flagged.IsRead);
        Assert.True(flagged.IsStarred);

        // 5. Pin it, move it to the archive: the pin follows the message.
        await _cache.SetPinnedAsync(_bob, reference.IdentityKey, DateTimeOffset.UtcNow);
        var move = await ImapOperations.MoveAsync(imap, reference.FolderPath, reference.Uid, archiveFolder.Path, CancellationToken.None);
        Assert.NotNull(move.NewUid);
        inbox = (await _cache.GetFoldersAsync(_bob)).Single(f => f.Id == inbox.Id);
        await sync.SyncFolderAsync(imap, inbox);
        Assert.Null(await _cache.GetSummaryAsync(received.Id));
        archiveFolder = (await _cache.GetFoldersAsync(_bob)).Single(f => f.Id == archiveFolder.Id);
        await sync.SyncFolderAsync(imap, archiveFolder);
        var moved = (await _cache.GetSummariesAsync()).Single(m => m.Subject == subject);
        Assert.Equal(archiveFolder.Id, moved.FolderId);
        Assert.True(moved.IsPinned);

        // 6. Delete permanently (explicit action): gone from the server and, after sync, from the cache.
        await ImapOperations.DeleteAsync(imap, archiveFolder.Path, move.NewUid!.Value, CancellationToken.None);
        archiveFolder = (await _cache.GetFoldersAsync(_bob)).Single(f => f.Id == archiveFolder.Id);
        var deleteSync = await sync.SyncFolderAsync(imap, archiveFolder);
        Assert.Equal(1, deleteSync.Removed);
        Assert.DoesNotContain(await _cache.GetSummariesAsync(), m => m.Subject == subject);

        await imap.DisconnectAsync(true);
    }

    [MailServerFact]
    public async Task Drafts_and_sent_copies_can_be_appended()
    {
        using var imap = await ImapAsync(TestMailServer.User1);
        var folders = await new ImapSynchronizer(_cache).SyncFolderListAsync(imap, _bob);
        var drafts = folders.Single(f => f.Kind == FolderKind.Drafts);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Alice", TestMailServer.User1.User));
        message.Subject = "Draft " + Guid.NewGuid().ToString("N")[..6];
        message.Body = new TextPart("plain") { Text = "unfinished" };

        var appended = await ImapOperations.AppendAsync(imap, drafts.Path, message, MessageFlags.Draft | MessageFlags.Seen, CancellationToken.None);
        Assert.NotNull(appended.Uid);
        var fetched = await ImapOperations.FetchMessageAsync(imap, drafts.Path, appended.Uid!.Value, CancellationToken.None);
        Assert.Equal(message.Subject, fetched.Subject);
        await ImapOperations.DeleteAsync(imap, drafts.Path, appended.Uid.Value, CancellationToken.None);
        await imap.DisconnectAsync(true);
    }

    [MailServerFact]
    public async Task Sent_copy_already_filed_by_the_server_is_detected()
    {
        using var imap = await ImapAsync(TestMailServer.User1);
        var folders = await new ImapSynchronizer(_cache).SyncFolderListAsync(imap, _bob);
        var sent = folders.Single(f => f.Kind == FolderKind.Sent);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Alice", TestMailServer.User1.User));
        message.To.Add(new MailboxAddress("Bob", TestMailServer.User2.User));
        message.Subject = "Server-filed copy";
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("example.test");
        message.Body = new TextPart("plain") { Text = "hi" };

        Assert.False(await Infrastructure.Live.LiveMailDataSource.SentCopyExistsAsync(imap, sent.Path, message.MessageId));
        var appended = await ImapOperations.AppendAsync(imap, sent.Path, message, MessageFlags.Seen, CancellationToken.None);
        Assert.True(await Infrastructure.Live.LiveMailDataSource.SentCopyExistsAsync(imap, sent.Path, message.MessageId));

        await ImapOperations.DeleteAsync(imap, sent.Path, appended.Uid!.Value, CancellationToken.None);
        await imap.DisconnectAsync(true);
    }

    [MailServerFact]
    public async Task Junk_keyword_is_stored_on_the_server()
    {
        using var imap = await ImapAsync(TestMailServer.User1);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Spammer", "spam@example.test"));
        message.Subject = "Junk keyword " + Guid.NewGuid().ToString("N")[..6];
        message.Body = new TextPart("plain") { Text = "buy now" };
        var appended = await ImapOperations.AppendAsync(imap, "INBOX", message, MessageFlags.None, CancellationToken.None);

        await ImapOperations.SetJunkKeywordAsync(imap, "INBOX", appended.Uid!.Value, isJunk: true, CancellationToken.None);
        var inbox = await imap.GetFolderAsync("INBOX");
        var summary = (await inbox.FetchAsync([new UniqueId(inbox.UidValidity, appended.Uid.Value)], MessageSummaryItems.Flags)).Single();
        Assert.Contains("$Junk", summary.Keywords);

        await ImapOperations.SetJunkKeywordAsync(imap, "INBOX", appended.Uid.Value, isJunk: false, CancellationToken.None);
        summary = (await inbox.FetchAsync([new UniqueId(inbox.UidValidity, appended.Uid.Value)], MessageSummaryItems.Flags)).Single();
        Assert.Contains("$NotJunk", summary.Keywords);
        Assert.DoesNotContain("$Junk", summary.Keywords);

        await ImapOperations.DeleteAsync(imap, "INBOX", appended.Uid.Value, CancellationToken.None);
        await imap.DisconnectAsync(true);
    }

    private sealed class SyncProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value)
        {
            lock (values) values.Add(value);
        }
    }
}
