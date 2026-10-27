using MailKit;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Infrastructure.Mail;

namespace SidePhase.Mail.Tests.Mail;

public class MessageIdentityTests
{
    private static readonly Guid Account = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Message_ids_are_deterministic_and_distinct()
    {
        var a = MessageIdentity.ForImapMessage(Account, "INBOX", 7, 42);
        Assert.Equal(a, MessageIdentity.ForImapMessage(Account, "INBOX", 7, 42));
        Assert.NotEqual(a, MessageIdentity.ForImapMessage(Account, "INBOX", 7, 43));
        Assert.NotEqual(a, MessageIdentity.ForImapMessage(Account, "INBOX", 8, 42));
        Assert.NotEqual(a, MessageIdentity.ForImapMessage(Account, "Archive", 7, 42));
        Assert.NotEqual(a, MessageIdentity.ForImapMessage(Guid.NewGuid(), "INBOX", 7, 42));
        Assert.NotEqual(a, MessageIdentity.ForFolder(Account, "INBOX"));
    }

    [Fact]
    public void Identity_key_uses_the_message_id_header_without_brackets()
    {
        var from = new EmailAddress("Jane", "jane@example.com");
        var date = DateTimeOffset.UtcNow;
        Assert.Equal("mid:abc@example.com", MessageIdentity.IdentityKey("<abc@example.com>", from, date, "Hi"));
        Assert.Equal("mid:abc@example.com", MessageIdentity.IdentityKey(" abc@example.com ", from, date, "Other subject"));
    }

    [Fact]
    public void Identity_key_falls_back_to_a_stable_fingerprint()
    {
        var from = new EmailAddress("Jane", "Jane@Example.com");
        var date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var key = MessageIdentity.IdentityKey(null, from, date, "Hi");
        Assert.StartsWith("h:", key);
        Assert.Equal(key, MessageIdentity.IdentityKey("", new EmailAddress(null, "jane@example.com"), date, "Hi"));
        Assert.NotEqual(key, MessageIdentity.IdentityKey(null, from, date, "Hello"));
    }
}

public class SyncPlannerTests
{
    [Fact]
    public void First_sync_downloads_only_the_most_recent_messages()
    {
        var plan = SyncPlanner.Plan(0, 0, [], 100, [1, 2, 3, 4, 5, 6], initialLimit: 3);
        Assert.False(plan.ResetFolder);
        Assert.Equal([4u, 5u, 6u], plan.FetchNew);
        Assert.Empty(plan.RefreshFlags);
        Assert.Empty(plan.RemoveFromCache);
    }

    [Fact]
    public void Incremental_sync_fetches_new_uids_refreshes_known_and_removes_missing()
    {
        var plan = SyncPlanner.Plan(100, 6, [4, 5, 6], 100, [1, 2, 3, 5, 6, 7, 8], initialLimit: 3);
        Assert.False(plan.ResetFolder);
        Assert.Equal([7u, 8u], plan.FetchNew);          // older 1-3 are not back-filled
        Assert.Equal([5u, 6u], plan.RefreshFlags);
        Assert.Equal([4u], plan.RemoveFromCache);       // only removed from the cache
    }

    [Fact]
    public void Uidvalidity_change_resets_the_folder()
    {
        var plan = SyncPlanner.Plan(100, 6, [4, 5, 6], 200, [1, 2, 3], initialLimit: 2);
        Assert.True(plan.ResetFolder);
        Assert.Equal([2u, 3u], plan.FetchNew);
        Assert.Empty(plan.RefreshFlags);
        Assert.Empty(plan.RemoveFromCache);
    }

    [Fact]
    public void Nothing_to_do_when_up_to_date()
    {
        var plan = SyncPlanner.Plan(5, 3, [1, 2, 3], 5, [1, 2, 3], 10);
        Assert.Empty(plan.FetchNew);
        Assert.Equal([1u, 2u, 3u], plan.RefreshFlags);
        Assert.Empty(plan.RemoveFromCache);
    }

    [Fact]
    public void Empty_folder_after_messages_were_cached_removes_them_but_fetches_nothing()
    {
        var plan = SyncPlanner.Plan(5, 9, [8, 9], 5, [], 10);
        Assert.Empty(plan.FetchNew);
        Assert.Equal([8u, 9u], plan.RemoveFromCache);
    }
}

public class FolderClassifierTests
{
    [Theory]
    [InlineData(FolderAttributes.Sent, "Whatever", FolderKind.Sent)]
    [InlineData(FolderAttributes.Drafts, "Whatever", FolderKind.Drafts)]
    [InlineData(FolderAttributes.Trash, "Whatever", FolderKind.Trash)]
    [InlineData(FolderAttributes.Junk, "Whatever", FolderKind.Junk)]
    [InlineData(FolderAttributes.Archive, "Whatever", FolderKind.Archive)]
    [InlineData(FolderAttributes.None, "Sent Items", FolderKind.Sent)]
    [InlineData(FolderAttributes.None, "Deleted Items", FolderKind.Trash)]
    [InlineData(FolderAttributes.None, "Junk E-mail", FolderKind.Junk)]
    [InlineData(FolderAttributes.None, "Gesendet", FolderKind.Sent)]
    [InlineData(FolderAttributes.None, "Projects", FolderKind.Custom)]
    public void Classifies_by_special_use_then_name(FolderAttributes attributes, string name, FolderKind expected)
    {
        var result = FolderClassifier.Classify(attributes, name, name, isInbox: false);
        Assert.Equal(expected, result.Kind);
        Assert.True(result.Synchronize);
    }

    [Fact]
    public void Inbox_is_recognised_case_insensitively()
    {
        Assert.Equal(FolderKind.Inbox, FolderClassifier.Classify(FolderAttributes.None, "inbox", "inbox", false).Kind);
    }

    [Fact]
    public void Gmail_all_mail_is_the_archive_target_but_not_downloaded()
    {
        var result = FolderClassifier.Classify(FolderAttributes.All | FolderAttributes.HasNoChildren, "[Gmail]/All Mail", "All Mail", false);
        Assert.Equal(FolderKind.Archive, result.Kind);
        Assert.False(result.Synchronize);
    }

    [Theory]
    [InlineData(FolderAttributes.NoSelect)]
    [InlineData(FolderAttributes.Flagged)]
    [InlineData(FolderAttributes.Important)]
    public void Virtual_and_non_selectable_folders_are_not_synchronised(FolderAttributes attributes)
    {
        Assert.False(FolderClassifier.Classify(attributes, "[Gmail]", "[Gmail]", false).Synchronize);
    }
}
