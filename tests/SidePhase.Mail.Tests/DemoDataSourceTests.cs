using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Services;
using SidePhase.Mail.Core.Settings;
using SidePhase.Mail.Infrastructure.Demo;

namespace SidePhase.Mail.Tests;

public class DemoDataSourceTests
{
    private readonly FixedClock _clock = new(Samples.Now);
    private readonly DemoMailDataSource _source;

    public DemoDataSourceTests()
    {
        _source = new DemoMailDataSource(_clock, TimeSpan.Zero);
    }

    [Fact]
    public async Task Demo_data_is_clearly_identified_and_uses_reserved_domains()
    {
        Assert.True(_source.IsDemo);
        var accounts = await _source.GetAccountsAsync();
        Assert.All(accounts, a => Assert.True(a.IsDemo));
        Assert.All(accounts, a => Assert.Contains("demo", a.DisplayName, StringComparison.OrdinalIgnoreCase));

        var messages = await _source.GetMessagesAsync();
        Assert.True(messages.Count > 20);
        Assert.All(messages, m => Assert.EndsWith(".example", m.From.Domain));
        Assert.All(messages.SelectMany(m => m.To), a => Assert.EndsWith(".example", a.Domain));
    }

    [Fact]
    public async Task Every_account_has_the_standard_folders()
    {
        foreach (var account in await _source.GetAccountsAsync())
        {
            var kinds = (await _source.GetFoldersAsync(account.Id)).Select(f => f.Kind).ToHashSet();
            foreach (var required in new[] { FolderKind.Inbox, FolderKind.Drafts, FolderKind.Sent, FolderKind.Archive, FolderKind.Junk, FolderKind.Trash })
            {
                Assert.Contains(required, kinds);
            }
        }
    }

    [Fact]
    public async Task Demo_data_exercises_pins_stars_attachments_and_spam()
    {
        var messages = await _source.GetMessagesAsync();
        Assert.Contains(messages, m => m.IsPinned);
        Assert.Contains(messages, m => m.IsStarred);
        Assert.Contains(messages, m => m.HasAttachments);
        Assert.Contains(messages, m => m.Spam.HasHeaders && m.FolderKind == FolderKind.Inbox && m.Spam.Score >= 5);
    }

    [Fact]
    public async Task Bodies_are_loaded_separately()
    {
        var first = (await _source.GetMessagesAsync())[0];
        var body = await _source.GetMessageBodyAsync(first.Id);
        Assert.NotNull(body);
        Assert.Equal(first.Id, body.MessageId);
    }

    [Fact]
    public async Task Flags_update_and_raise_change_events()
    {
        var message = (await _source.GetMessagesAsync()).First(m => !m.IsPinned);
        var raised = 0;
        _source.DataChanged += (_, _) => raised++;

        await _source.SetPinnedAsync([message.Id], true);
        await _source.SetStarredAsync([message.Id], true);
        await _source.SetReadAsync([message.Id], !message.IsRead);

        var updated = await _source.GetMessageAsync(message.Id);
        Assert.NotNull(updated);
        Assert.True(updated.IsPinned);
        Assert.Equal(Samples.Now, updated.PinnedAt);
        Assert.True(updated.IsStarred);
        Assert.Equal(!message.IsRead, updated.IsRead);
        Assert.Equal(3, raised);
    }

    [Fact]
    public async Task Pin_survives_folder_moves_and_undo_restores_folder()
    {
        var message = (await _source.GetMessagesAsync()).First(m => m.FolderKind == FolderKind.Inbox);
        await _source.SetPinnedAsync([message.Id], true);

        var moves = await _source.MoveToAsync([message.Id], FolderKind.Archive);
        var archived = await _source.GetMessageAsync(message.Id);
        Assert.Equal(FolderKind.Archive, archived!.FolderKind);
        Assert.True(archived.IsPinned);

        await _source.RestoreAsync(moves);
        var restored = await _source.GetMessageAsync(message.Id);
        Assert.Equal(message.FolderId, restored!.FolderId);
        Assert.True(restored.IsPinned);
    }

    [Fact]
    public async Task Permanent_delete_only_applies_to_trash()
    {
        var inbox = (await _source.GetMessagesAsync()).First(m => m.FolderKind == FolderKind.Inbox);
        await _source.DeletePermanentlyAsync([inbox.Id]);
        Assert.NotNull(await _source.GetMessageAsync(inbox.Id));

        await _source.MoveToAsync([inbox.Id], FolderKind.Trash);
        await _source.DeletePermanentlyAsync([inbox.Id]);
        Assert.Null(await _source.GetMessageAsync(inbox.Id));
    }

    [Fact]
    public async Task Drafts_can_be_saved_updated_and_sent()
    {
        var account = (await _source.GetAccountsAsync())[0];
        var draft = new DraftMessage
        {
            AccountId = account.Id,
            To = [new EmailAddress("Friend", "friend@somewhere.example")],
            Subject = "Draft subject",
            TextBody = "Hello",
        };

        var id = await _source.SaveDraftAsync(draft);
        var saved = await _source.GetMessageAsync(id);
        Assert.Equal(FolderKind.Drafts, saved!.FolderKind);

        var id2 = await _source.SaveDraftAsync(draft with { DraftId = id, Subject = "Updated" });
        Assert.Equal(id, id2);
        Assert.Equal("Updated", (await _source.GetMessageAsync(id))!.Subject);

        var progress = new List<double>();
        var result = await _source.SendAsync(draft with { DraftId = id }, new SyncProgress(progress));
        Assert.True(result.Succeeded);
        Assert.Contains("not delivered", result.Message);
        Assert.Null(await _source.GetMessageAsync(id));
        var sent = await _source.GetMessageAsync(result.SentMessageId!.Value);
        Assert.Equal(FolderKind.Sent, sent!.FolderKind);
        Assert.Equal(1.0, progress[^1]);
    }

    [Fact]
    public async Task Sending_without_recipients_fails()
    {
        var account = (await _source.GetAccountsAsync())[0];
        var result = await _source.SendAsync(new DraftMessage { AccountId = account.Id, Subject = "x" });
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Clearing_local_data_restores_the_sample_mailbox()
    {
        var before = (await _source.GetMessagesAsync()).Count;
        var trash = (await _source.GetMessagesAsync()).First(m => m.FolderKind == FolderKind.Trash);
        await _source.DeletePermanentlyAsync([trash.Id]);
        await _source.ClearLocalDataAsync();
        Assert.Equal(before, (await _source.GetMessagesAsync()).Count);
    }

    private sealed class SyncProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }
}
