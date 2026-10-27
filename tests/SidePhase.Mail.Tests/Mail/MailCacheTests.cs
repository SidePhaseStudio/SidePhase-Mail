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
