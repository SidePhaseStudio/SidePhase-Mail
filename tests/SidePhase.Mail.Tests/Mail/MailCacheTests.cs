using Microsoft.Data.Sqlite;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Infrastructure.Mail;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Tests.Mail;

public sealed class MailCacheTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-cache-" + Guid.NewGuid().ToString("N"));
    private MailCache _cache = null!;
    private readonly Guid _account = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _cache = new MailCache(new MailDatabase(Path.Combine(_dir, "test.db")));
        await _cache.InitializeAsync();
        await _cache.AddAccountAsync(Account(_account));
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    internal static StoredAccount Account(Guid id, string email = "alice@example.test") => new(
        new MailAccount { Id = id, DisplayName = "Alice", EmailAddress = email, Provider = MailProviderKind.Imap, ColorHex = "#4F8EF7" },
        new AccountConnectionSettings
        {
            Imap = new ServerEndpoint("imap.example.test", 993, ConnectionSecurity.SslOnConnect),
            Smtp = new ServerEndpoint("smtp.example.test", 587, ConnectionSecurity.StartTls),
            Username = email,
            AuthMethod = AuthMethod.Password,
        });

    private StoredFolder Folder(string path, FolderKind kind) => new()
    {
        Id = MessageIdentity.ForFolder(_account, path),
        AccountId = _account,
        Path = path,
        Name = path,
        Kind = kind,
    };

    private static StoredMessage Message(StoredFolder folder, uint uidValidity, uint uid, string messageId, bool read = false, string subject = "Hello") => new()
    {
        Id = MessageIdentity.ForImapMessage(folder.AccountId, folder.Path, uidValidity, uid),
        AccountId = folder.AccountId,
        FolderId = folder.Id,
        Uid = uid,
        UidValidity = uidValidity,
        MessageIdHeader = messageId,
        IdentityKey = MessageIdentity.IdentityKey(messageId, new EmailAddress(null, "bob@example.test"), DateTimeOffset.UnixEpoch, subject),
        From = new EmailAddress("Bob", "bob@example.test"),
        To = [new EmailAddress(null, "alice@example.test")],
        Subject = subject,
        Preview = "Preview text",
        Date = DateTimeOffset.UtcNow.AddMinutes(-uid),
        IsRead = read,
    };

    [Fact]
    public async Task Migration_is_idempotent_and_records_the_schema_version()
    {
        Assert.Equal(MailDatabase.LatestVersion, await _cache.InitializeAsync());
        Assert.Equal(MailDatabase.LatestVersion, await _cache.InitializeAsync());
    }

    [Fact]
    public async Task Newer_database_than_the_app_is_refused()
    {
        await using (var c = await _cache.Database.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA user_version = {MailDatabase.LatestVersion + 5}";
            await cmd.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAnyAsync<Exception>(() => _cache.InitializeAsync());
    }

    [Fact]
    public async Task Accounts_round_trip_without_secrets()
    {
        var accounts = await _cache.GetAccountsAsync();
        var stored = Assert.Single(accounts);
        Assert.Equal("alice@example.test", stored.Account.EmailAddress);
        Assert.Equal(ConnectionSecurity.StartTls, stored.Settings.Smtp.Security);
        Assert.Equal(993, stored.Settings.Imap.Port);

        await using var c = await _cache.Database.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT group_concat(name) FROM pragma_table_info('accounts')";
        var columns = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.DoesNotContain("password", columns);
        Assert.DoesNotContain("token", columns);
    }

    [Fact]
    public async Task Upserting_the_same_message_twice_never_duplicates()
    {
        var inbox = Folder("INBOX", FolderKind.Inbox);
        await _cache.UpsertFolderAsync(inbox);
        var message = Message(inbox, 1, 10, "<a@x>");
        await _cache.UpsertMessagesAsync([message]);
        await _cache.UpsertMessagesAsync([message with { IsRead = true }]);

        var summary = Assert.Single(await _cache.GetSummariesAsync());
        Assert.True(summary.IsRead);
        Assert.Equal(FolderKind.Inbox, summary.FolderKind);
    }

    [Fact]
    public async Task Uidvalidity_reset_clears_messages_but_keeps_pins()
    {
        var inbox = Folder("INBOX", FolderKind.Inbox);
        await _cache.UpsertFolderAsync(inbox);
        var original = Message(inbox, 1, 10, "<keep@x>");
        await _cache.UpsertMessagesAsync([original]);
        await _cache.SetPinnedAsync(_account, original.IdentityKey, DateTimeOffset.UtcNow);
        await _cache.UpdateFolderSyncStateAsync(inbox.Id, 1, 10, 0);

        await _cache.ResetFolderAsync(inbox.Id);
        Assert.Empty(await _cache.GetSummariesAsync());
        var folder = Assert.Single(await _cache.GetFoldersAsync(_account));
        Assert.Equal(0u, folder.UidValidity);

        // Re-downloaded under a new UIDVALIDITY: the pin reattaches by identity.
        await _cache.UpsertMessagesAsync([Message(inbox, 2, 1, "<keep@x>")]);
        var summary = Assert.Single(await _cache.GetSummariesAsync());
        Assert.True(summary.IsPinned);
        Assert.NotEqual(original.Id, summary.Id);
    }

    [Fact]
    public async Task Pins_and_local_stars_follow_a_message_to_another_folder()
    {
        var inbox = Folder("INBOX", FolderKind.Inbox);
        var archive = Folder("Archive", FolderKind.Archive);
        await _cache.UpsertFolderAsync(inbox);
        await _cache.UpsertFolderAsync(archive);
        var message = Message(inbox, 1, 5, "<moved@x>");
        await _cache.UpsertMessagesAsync([message]);
        await _cache.SetPinnedAsync(_account, message.IdentityKey, DateTimeOffset.UtcNow);
        await _cache.SetLocalStarAsync(_account, message.IdentityKey, true);

        await _cache.DeleteMessageAsync(message.Id);
        await _cache.UpsertMessagesAsync([Message(archive, 3, 77, "<moved@x>")]);

        var summary = Assert.Single(await _cache.GetSummariesAsync());
        Assert.Equal(archive.Id, summary.FolderId);
        Assert.True(summary.IsPinned);
        Assert.True(summary.IsStarred);

        await _cache.SetPinnedAsync(_account, message.IdentityKey, null);
        Assert.False(Assert.Single(await _cache.GetSummariesAsync()).IsPinned);
    }

    [Fact]
    public async Task Flag_updates_and_removals_only_touch_the_given_folder()
    {
        var inbox = Folder("INBOX", FolderKind.Inbox);
        await _cache.UpsertFolderAsync(inbox);
        await _cache.UpsertMessagesAsync([Message(inbox, 1, 1, "<1@x>"), Message(inbox, 1, 2, "<2@x>"), Message(inbox, 1, 3, "<3@x>")]);

        var changed = await _cache.UpdateFlagsAsync(inbox.Id, 1, [(1u, true, true), (2u, false, false)]);
        Assert.Equal(1, changed);
        Assert.Equal(2, await _cache.DeleteMessagesAsync(inbox.Id, 1, [2u, 3u]));
        var summary = Assert.Single(await _cache.GetSummariesAsync());
        Assert.True(summary.IsRead);
        Assert.True(summary.IsStarred);
        Assert.Equal([1u], await _cache.GetUidsAsync(inbox.Id, 1));
    }

    [Fact]
    public async Task Bodies_and_drafts_persist()
    {
        var inbox = Folder("INBOX", FolderKind.Inbox);
        await _cache.UpsertFolderAsync(inbox);
        var message = Message(inbox, 1, 1, "<b@x>");
        await _cache.UpsertMessagesAsync([message]);
        await _cache.SaveBodyAsync(new MessageBody { MessageId = message.Id, HtmlBody = "<p>Hi</p>", TextBody = "Hi", Attachments = [new AttachmentInfo("a.pdf", "application/pdf", 10)] });
        var body = await _cache.GetBodyAsync(message.Id);
        Assert.Equal("<p>Hi</p>", body!.HtmlBody);
        Assert.Equal("a.pdf", Assert.Single(body.Attachments).FileName);

        var draftId = Guid.NewGuid();
        await _cache.SaveDraftAsync(draftId, new DraftMessage { AccountId = _account, Subject = "Draft", To = [new EmailAddress(null, "bob@example.test")] });
        var draft = await _cache.GetDraftAsync(draftId);
        Assert.Equal("Draft", draft!.Subject);
        await _cache.DeleteDraftAsync(draftId);
        Assert.Null(await _cache.GetDraftAsync(draftId));
    }

    [Fact]
    public async Task Deleting_an_account_removes_all_of_its_cached_data()
    {
        var inbox = Folder("INBOX", FolderKind.Inbox);
        await _cache.UpsertFolderAsync(inbox);
        var message = Message(inbox, 1, 1, "<z@x>");
        await _cache.UpsertMessagesAsync([message]);
        await _cache.SetPinnedAsync(_account, message.IdentityKey, DateTimeOffset.UtcNow);

        await _cache.DeleteAccountAsync(_account);
        Assert.Empty(await _cache.GetAccountsAsync());
        Assert.Empty(await _cache.GetFoldersAsync());
        Assert.Empty(await _cache.GetSummariesAsync());
    }

    [Fact]
    public async Task Snoozes_follow_the_message_across_folders_and_expire()
    {
        var inbox = Folder("INBOX", FolderKind.Inbox);
        var archive = Folder("Archive", FolderKind.Archive);
        await _cache.UpsertFolderAsync(inbox);
        await _cache.UpsertFolderAsync(archive);
        var message = Message(inbox, 1, 1, "<snooze@x>");
        await _cache.UpsertMessagesAsync([message]);
        var now = DateTimeOffset.UtcNow;
        await _cache.SetSnoozeAsync(_account, message.IdentityKey, now.AddHours(2), now);

        var snoozed = Assert.Single(await _cache.GetSummariesAsync());
        Assert.True(snoozed.IsSnoozed);
        Assert.Equal(now.AddHours(2).ToUnixTimeMilliseconds(), snoozed.SnoozedUntil!.Value.ToUnixTimeMilliseconds());

        // Moved elsewhere (new id): still snoozed.
        await _cache.DeleteMessageAsync(message.Id);
        var moved = Message(archive, 2, 9, "<snooze@x>");
        await _cache.UpsertMessagesAsync([moved]);
        Assert.True(Assert.Single(await _cache.GetSummariesAsync()).IsSnoozed);

        Assert.Empty(await _cache.ReleaseExpiredSnoozesAsync(now.AddHours(1)));
        var released = await _cache.ReleaseExpiredSnoozesAsync(now.AddHours(2));
        Assert.Equal((_account, moved.IdentityKey), Assert.Single(released));
        Assert.False(Assert.Single(await _cache.GetSummariesAsync()).IsSnoozed);
        Assert.Equal([moved.Id], await _cache.FindAllByIdentityAsync(_account, moved.IdentityKey));
    }

    [Fact]
    public async Task Reminders_persist_are_announced_once_and_re_arm_when_changed()
    {
        var inbox = Folder("INBOX", FolderKind.Inbox);
        await _cache.UpsertFolderAsync(inbox);
        var message = Message(inbox, 1, 1, "<remind@x>");
        await _cache.UpsertMessagesAsync([message]);
        var now = DateTimeOffset.UtcNow;
        await _cache.SetFollowUpAsync(_account, message.IdentityKey, now.AddDays(1), now);
        Assert.NotNull(Assert.Single(await _cache.GetSummariesAsync()).FollowUpAt);

        Assert.Empty(await _cache.TakeDueRemindersAsync(now));
        Assert.Equal([message.Id], await _cache.TakeDueRemindersAsync(now.AddDays(1)));
        Assert.Empty(await _cache.TakeDueRemindersAsync(now.AddDays(2))); // already announced

        // Editing the time re-arms the reminder.
        await _cache.SetFollowUpAsync(_account, message.IdentityKey, now.AddDays(3), now);
        Assert.Equal([message.Id], await _cache.TakeDueRemindersAsync(now.AddDays(3)));

        // Done: removed, and still listed for overdue display until then.
        await _cache.SetFollowUpAsync(_account, message.IdentityKey, null, now);
        Assert.Null(Assert.Single(await _cache.GetSummariesAsync()).FollowUpAt);
    }

    [Fact]
    public async Task Migration_from_schema_1_keeps_accounts_and_adds_new_tables()
    {
        // Simulate a Milestone 2/3 database: schema 1 with an account in it.
        var file = Path.Combine(_dir, "old.db");
        var db = new MailDatabase(file);
        await using (var c = await db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = MailDatabase.Migrations[0] + "\nPRAGMA user_version = 1;";
            await cmd.ExecuteNonQueryAsync();
        }

        var old = new MailCache(db);
        await old.AddAccountAsync(Account(Guid.NewGuid(), "kept@example.test"));
        Assert.Equal(MailDatabase.LatestVersion, await old.InitializeAsync());
        Assert.Equal("kept@example.test", Assert.Single(await old.GetAccountsAsync()).Account.EmailAddress);

        await using var c2 = await db.OpenAsync();
        await using var check = c2.CreateCommand();
        check.CommandText = "SELECT count(*) FROM sqlite_master WHERE name IN ('snoozes','reminders','outbox','outbox_attempts','contacts','contact_emails','recent_recipients')";
        Assert.Equal(7L, (long)(await check.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Search_index_covers_subjects_people_text_and_attachment_names_and_follows_deletes()
    {
        var inbox = Folder("INBOX", FolderKind.Inbox);
        await _cache.UpsertFolderAsync(inbox);
        var report = Message(inbox, 1, 1, "<r@x>", subject: "Quarterly résumé") with
        {
            HasAttachments = true,
            Attachments = [new AttachmentInfo("Budget-2026.xlsx", "application/vnd.ms-excel", 12_000)],
            To = [new EmailAddress("Carol Jones", "carol@example.test")],
        };
        var other = Message(inbox, 1, 2, "<o@x>", subject: "Lunch");
        await _cache.UpsertMessagesAsync([report, other]);
        await _cache.SaveBodyAsync(new MessageBody { MessageId = other.Id, TextBody = "The zebra crossing is closed." });

        Assert.Equal([report.Id], await _cache.SearchIndexAsync("\"resume\"*")); // diacritics folded
        Assert.Equal([report.Id], await _cache.SearchIndexAsync("\"carol\"*"));
        Assert.Equal([report.Id], await _cache.SearchIndexAsync("attachments : (\"budget\"*)"));
        Assert.Equal([other.Id], await _cache.SearchIndexAsync("body : (\"zeb\"*)"));
        Assert.Empty(await _cache.SearchIndexAsync("body : (\"budget\"*)"));

        var summary = (await _cache.GetSummaryAsync(report.Id))!;
        Assert.Equal("Budget-2026.xlsx", Assert.Single(summary.Attachments).FileName);

        // Recording details again (e.g. when the body is downloaded) doesn't duplicate them.
        await _cache.SaveBodyAsync(new MessageBody { MessageId = report.Id, TextBody = "x", Attachments = summary.Attachments });
        Assert.Single((await _cache.GetSummaryAsync(report.Id))!.Attachments);

        await _cache.DeleteMessagesAsync(inbox.Id, 1, [1u]);
        Assert.Empty(await _cache.SearchIndexAsync("\"carol\"*"));
        Assert.Empty(await _cache.GetMessagesMissingAttachmentDetailsAsync(inbox.Id, 1, 10));
    }

    [Fact]
    public async Task Older_messages_without_attachment_details_are_listed_for_backfill()
    {
        var inbox = Folder("INBOX", FolderKind.Inbox);
        await _cache.UpsertFolderAsync(inbox);
        var old = Message(inbox, 1, 7, "<a@x>") with { HasAttachments = true };
        await _cache.UpsertMessagesAsync([old]);
        Assert.Equal((old.Id, 7u), Assert.Single(await _cache.GetMessagesMissingAttachmentDetailsAsync(inbox.Id, 1, 10)));
        await _cache.SaveAttachmentDetailsAsync(old.Id, [new AttachmentInfo("scan.pdf", "application/pdf", 10)]);
        Assert.Empty(await _cache.GetMessagesMissingAttachmentDetailsAsync(inbox.Id, 1, 10));
        Assert.Equal([old.Id], await _cache.SearchIndexAsync("attachments : (\"scan\"*)"));
    }

    [Fact]
    public async Task Migration_from_schema_2_indexes_mail_that_is_already_cached()
    {
        var file = Path.Combine(_dir, "v2.db");
        var db = new MailDatabase(file);
        await using (var c = await db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = MailDatabase.Migrations[0] + "\n" + MailDatabase.Migrations[1] + "\nPRAGMA user_version = 2;";
            await cmd.ExecuteNonQueryAsync();
        }

        var cache = new MailCache(db);
        await cache.AddAccountAsync(Account(_account));
        var inbox = Folder("INBOX", FolderKind.Inbox);
        await cache.UpsertFolderAsync(inbox);
        await using (var c = await db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            // Rows written by the Milestone 4 app (no search index yet).
            cmd.CommandText = """
                INSERT INTO messages (id, account_id, folder_id, uid, uid_validity, identity_key, from_address, to_json, cc_json, subject, preview, date, is_read, is_starred, has_attachments)
                VALUES ($id, $a, $f, 1, 1, 'k', 'bob@example.test', '[]', '[]', 'Invoice for October', 'Please pay', 0, 0, 0, 0);
                INSERT INTO bodies (message_id, text, attachments_json, cached_at) VALUES ($id, 'The amount due is large', '[]', 0);
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("$a", _account.ToString());
            cmd.Parameters.AddWithValue("$f", inbox.Id.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        Assert.Equal(MailDatabase.LatestVersion, await cache.InitializeAsync());
        Assert.Single(await cache.SearchIndexAsync("\"invoice\"*"));
        Assert.Single(await cache.SearchIndexAsync("body : (\"amount\"*)"));
        Assert.Equal("alice@example.test", Assert.Single(await cache.GetAccountsAsync()).Account.EmailAddress);
    }

    [Fact]
    public async Task Categories_follow_the_message_and_appear_on_summaries()
    {
        var inbox = Folder("INBOX", FolderKind.Inbox);
        var archive = Folder("Archive", FolderKind.Archive);
        await _cache.UpsertFolderAsync(inbox);
        await _cache.UpsertFolderAsync(archive);
        var m = Message(inbox, 1, 1, "<c@x>");
        await _cache.UpsertMessagesAsync([m]);
        await _cache.SetCategoryAsync(_account, m.IdentityKey, "Bills", add: true);
        await _cache.SetCategoryAsync(_account, m.IdentityKey, "Home", add: true);
        await _cache.SetCategoryAsync(_account, m.IdentityKey, "bills", add: true); // same category, any case
        Assert.Equal(["Bills", "Home"], (await _cache.GetSummaryAsync(m.Id))!.Categories.Order());

        var moved = MessageIdentity.ForImapMessage(_account, "Archive", 1, 9);
        await _cache.MoveCachedMessageAsync(m.Id, moved, archive.Id, 1, 9);
        Assert.Equal(2, (await _cache.GetSummaryAsync(moved))!.Categories.Count);

        await _cache.SetCategoryAsync(_account, m.IdentityKey, "Home", add: false);
        Assert.Equal(["Bills"], (await _cache.GetSummaryAsync(moved))!.Categories);
    }

    [Fact]
    public async Task Smart_folder_definitions_persist()
    {
        var store = new SqliteSmartFolderStore(_cache);
        var folder = new SidePhase.Mail.Core.Organisation.SmartFolder { Id = Guid.NewGuid(), Name = "Bank", Query = "from:acme", Position = 3, CreatedAt = DateTimeOffset.UnixEpoch };
        await store.SaveAsync(folder);
        await store.SaveAsync(folder with { Name = "Bank mail" });
        var saved = Assert.Single(await new SqliteSmartFolderStore(_cache).ListAsync());
        Assert.Equal("Bank mail", saved.Name);
        Assert.Equal("from:acme", saved.Query);
        await store.DeleteAsync(folder.Id);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task Folder_upsert_keeps_sync_state()
    {
        var inbox = Folder("INBOX", FolderKind.Inbox);
        await _cache.UpsertFolderAsync(inbox);
        await _cache.UpdateFolderSyncStateAsync(inbox.Id, 9, 99, 5);
        await _cache.UpsertFolderAsync(inbox with { Name = "Inbox" });
        var folder = Assert.Single(await _cache.GetFoldersAsync(_account));
        Assert.Equal("Inbox", folder.Name);
        Assert.Equal(9u, folder.UidValidity);
        Assert.Equal(99u, folder.HighestUid);
        Assert.NotNull(folder.LastSync);
    }
}
