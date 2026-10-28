using Microsoft.Data.Sqlite;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Organisation;
using SidePhase.Mail.Infrastructure.Demo;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Tests;

public sealed class MailRuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-rules-" + Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = new(Samples.Now);
    private readonly DemoMailDataSource _mail;
    private readonly MailCache _cache;
    private readonly MailRuleService _rules;

    public MailRuleTests()
    {
        Directory.CreateDirectory(_dir);
        _mail = new DemoMailDataSource(_clock, TimeSpan.Zero);
        _cache = new MailCache(new MailDatabase(Path.Combine(_dir, "r.db")));
        _rules = new MailRuleService(new SqliteMailRuleStore(_cache), _mail, _clock);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static MessageSummary M(string from = "news@shop.example", string subject = "Weekly deals", string preview = "", bool attachments = false, double? score = null) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        FolderId = Guid.NewGuid(),
        FolderKind = FolderKind.Inbox,
        From = new EmailAddress("Shop", from),
        To = [new EmailAddress("Me", "me@home.example")],
        Subject = subject,
        Preview = preview,
        Date = Samples.Now,
        HasAttachments = attachments,
        Spam = score is null ? Core.Spam.SpamReport.None : new Core.Spam.SpamReport { Score = score, HasHeaders = true, Trust = Core.Spam.SpamHeaderTrust.AddedInTransit },
        MessageIdHeader = $"<{Guid.NewGuid():N}@x>",
    };

    [Theory]
    [InlineData(RuleField.Sender, "shop.example", true)]
    [InlineData(RuleField.Sender, "Shop", true)]
    [InlineData(RuleField.SenderDomain, "shop.example", true)]
    [InlineData(RuleField.SenderDomain, "example", true)]
    [InlineData(RuleField.SenderDomain, "hop.example", false)]
    [InlineData(RuleField.Recipient, "me@home", true)]
    [InlineData(RuleField.Subject, "deals", true)]
    [InlineData(RuleField.Keywords, "coupon, weekly", true)]
    [InlineData(RuleField.Keywords, "coupon, invoice", false)]
    [InlineData(RuleField.SpamScore, "4", true)]
    [InlineData(RuleField.SpamScore, "6", false)]
    [InlineData(RuleField.HasAttachment, "yes", false)]
    [InlineData(RuleField.HasAttachment, "no", true)]
    public void Conditions_match(RuleField field, string value, bool expected) =>
        Assert.Equal(expected, MailRuleConditions.Matches(new RuleCondition(field, value), M(score: 5.5)));

    [Fact]
    public void Unverified_spam_scores_never_trigger_rules()
    {
        var forged = M() with { Spam = new Core.Spam.SpamReport { Score = 99, HasHeaders = true, Trust = Core.Spam.SpamHeaderTrust.Untrusted } };
        Assert.False(MailRuleConditions.Matches(new RuleCondition(RuleField.SpamScore, "5"), forged));
    }

    [Fact]
    public void All_or_any_and_account_filters()
    {
        var m = M();
        var both = new MailRule { Id = Guid.NewGuid(), Name = "x", Conditions = [new(RuleField.Subject, "deals"), new(RuleField.Subject, "invoice")] };
        Assert.False(both.Matches(m));
        Assert.True((both with { MatchAll = false }).Matches(m));
        Assert.False((both with { MatchAll = false, AccountId = Guid.NewGuid() }).Matches(m));
    }

    [Fact]
    public async Task Rules_act_on_new_mail_once_even_after_the_message_moves()
    {
        var inbox = (await _mail.GetMessagesAsync()).First(m => m.FolderKind == FolderKind.Inbox && !m.IsRead);
        var archive = (await _mail.GetFoldersAsync(inbox.AccountId)).Single(f => f.Kind == FolderKind.Archive);
        await _rules.SaveAsync(new MailRule
        {
            Id = Guid.NewGuid(),
            Name = "File it",
            Conditions = [new(RuleField.Sender, inbox.From.Address)],
            Actions = [new(RuleActionKind.MarkRead), new(RuleActionKind.Star), new(RuleActionKind.AddCategory, "Filed"), new(RuleActionKind.SuppressNotification), new(RuleActionKind.MoveToFolder, archive.Id.ToString())],
        });

        var result = await _rules.RunOnNewMailAsync([inbox]);
        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Moved);
        Assert.Contains(inbox.Id, result.SuppressNotifications);
        var after = (await _mail.GetMessageAsync(inbox.Id))!;
        Assert.True(after.IsRead);
        Assert.True(after.IsStarred);
        Assert.Equal(FolderKind.Archive, after.FolderKind);
        Assert.Contains("Filed", after.Categories);

        // The same message seen again (another sync, or after being moved back) isn't processed a second time.
        await _mail.MoveToAsync([inbox.Id], FolderKind.Inbox);
        var again = await _rules.RunOnNewMailAsync([(await _mail.GetMessageAsync(inbox.Id))!]);
        Assert.Equal(0, again.Matched);
        Assert.Equal(FolderKind.Inbox, (await _mail.GetMessageAsync(inbox.Id))!.FolderKind);
    }

    [Fact]
    public async Task Disabled_rules_and_stop_processing_are_respected()
    {
        var message = (await _mail.GetMessagesAsync()).First(m => m.FolderKind == FolderKind.Inbox);
        await _rules.SaveAsync(new MailRule { Id = Guid.NewGuid(), Name = "first", StopProcessing = true, Conditions = [new(RuleField.Sender, message.From.Address)], Actions = [new(RuleActionKind.AddCategory, "One")] });
        await _rules.SaveAsync(new MailRule { Id = Guid.NewGuid(), Name = "second", Conditions = [new(RuleField.Sender, message.From.Address)], Actions = [new(RuleActionKind.AddCategory, "Two")] });
        await _rules.SaveAsync(new MailRule { Id = Guid.NewGuid(), Name = "off", Enabled = false, Conditions = [new(RuleField.Sender, message.From.Address)], Actions = [new(RuleActionKind.Star)] });

        await _rules.RunOnNewMailAsync([message]);
        var after = (await _mail.GetMessageAsync(message.Id))!;
        Assert.Equal(["One"], after.Categories);
        Assert.Equal(message.IsStarred, after.IsStarred);
    }

    [Fact]
    public async Task Run_now_handles_existing_messages_once()
    {
        var messages = (await _mail.GetMessagesAsync()).Where(m => m.FolderKind == FolderKind.Inbox).ToList();
        var rule = await _rules.SaveAsync(new MailRule { Id = Guid.NewGuid(), Name = "label all", Conditions = [new(RuleField.HasAttachment, "no")], Actions = [new(RuleActionKind.AddCategory, "Seen")] });
        var first = await _rules.RunNowAsync(rule, messages);
        Assert.True(first.Matched > 0);
        Assert.Equal(0, (await _rules.RunNowAsync(rule, messages)).Matched);
    }

    [Fact]
    public async Task Invalid_rules_are_refused_and_rules_persist()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _rules.SaveAsync(new MailRule { Id = Guid.NewGuid(), Name = " ", Conditions = [new(RuleField.Subject, "x")], Actions = [new(RuleActionKind.Star)] }));
        await Assert.ThrowsAsync<ArgumentException>(() => _rules.SaveAsync(new MailRule { Id = Guid.NewGuid(), Name = "x", Conditions = [new(RuleField.Subject, " ")], Actions = [new(RuleActionKind.Star)] }));
        await Assert.ThrowsAsync<ArgumentException>(() => _rules.SaveAsync(new MailRule { Id = Guid.NewGuid(), Name = "x", Conditions = [new(RuleField.Subject, "a")], Actions = [] }));
        await Assert.ThrowsAsync<ArgumentException>(() => _rules.SaveAsync(new MailRule { Id = Guid.NewGuid(), Name = "x", Conditions = [new(RuleField.Subject, "a")], Actions = [new(RuleActionKind.MoveToFolder, "nope")] }));

        var saved = await _rules.SaveAsync(new MailRule
        {
            Id = Guid.NewGuid(),
            Name = "Bills",
            MatchAll = false,
            StopProcessing = true,
            Conditions = [new(RuleField.SenderDomain, "bank.example"), new(RuleField.Keywords, "invoice, statement")],
            Actions = [new(RuleActionKind.AddCategory, "Bills"), new(RuleActionKind.MarkRead)],
        });
        var reloaded = Assert.Single(await new MailRuleService(new SqliteMailRuleStore(_cache), _mail, _clock).ListAsync());
        Assert.Equal(saved.Conditions, reloaded.Conditions);
        Assert.Equal(saved.Actions, reloaded.Actions);
        Assert.False(reloaded.MatchAll);
        Assert.True(reloaded.StopProcessing);

        await _rules.DeleteAsync(saved.Id);
        Assert.Empty(await _rules.ListAsync());
    }
}
