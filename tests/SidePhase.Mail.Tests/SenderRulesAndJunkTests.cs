using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Services;
using SidePhase.Mail.Core.Settings;
using SidePhase.Mail.Core.Spam;

namespace SidePhase.Mail.Tests;

public class SenderRulesAndJunkTests
{
    private readonly SettingsService _settings = new(new InMemorySettingsStore());
    private readonly SenderRulesService _rules;
    private readonly JunkPolicy _junk;

    public SenderRulesAndJunkTests()
    {
        _rules = new SenderRulesService(_settings, new FixedClock(Samples.Now));
        _junk = new JunkPolicy(_rules, _settings);
    }

    [Fact]
    public void Blocked_sender_is_matched_case_insensitively()
    {
        Assert.True(_rules.AddRule(SenderRuleKind.BlockedSender, "Spammer@Example.COM"));
        Assert.True(_rules.IsBlocked(new EmailAddress(null, "spammer@example.com")));
        Assert.False(_rules.IsBlocked(new EmailAddress(null, "friend@example.com")));
    }

    [Fact]
    public void Duplicate_rules_are_not_added()
    {
        Assert.True(_rules.AddRule(SenderRuleKind.BlockedSender, "a@b.com"));
        Assert.False(_rules.AddRule(SenderRuleKind.BlockedSender, " A@B.com "));
        Assert.Single(_rules.Rules);
    }

    [Fact]
    public void Blocked_domain_matches_subdomains_but_not_lookalikes()
    {
        _rules.AddRule(SenderRuleKind.BlockedDomain, "@spam.example");
        Assert.True(_rules.IsBlocked(new EmailAddress(null, "x@spam.example")));
        Assert.True(_rules.IsBlocked(new EmailAddress(null, "x@mail.spam.example")));
        Assert.False(_rules.IsBlocked(new EmailAddress(null, "x@notspam.example")));
    }

    [Fact]
    public void Trusted_sender_overrides_blocked_domain()
    {
        _rules.AddRule(SenderRuleKind.BlockedDomain, "corp.example");
        _rules.AddRule(SenderRuleKind.TrustedSender, "boss@corp.example");
        Assert.False(_rules.IsBlocked(new EmailAddress(null, "boss@corp.example")));
        Assert.True(_rules.IsBlocked(new EmailAddress(null, "other@corp.example")));
    }

    [Fact]
    public void Trusting_a_blocked_sender_removes_the_block()
    {
        _rules.AddRule(SenderRuleKind.BlockedSender, "x@y.example");
        _rules.AddRule(SenderRuleKind.TrustedSender, "x@y.example");
        Assert.DoesNotContain(_rules.Rules, r => r.Kind == SenderRuleKind.BlockedSender);
    }

    [Fact]
    public void High_spam_score_in_inbox_is_locally_junk_unless_trusted()
    {
        var message = Samples.Message() with
        {
            Spam = new SpamReport { HasHeaders = true, Score = 9, ServerFlaggedAsSpam = false },
        };
        Assert.True(_junk.IsLocallyJunk(message));
        Assert.Equal(FolderKind.Junk, _junk.EffectiveFolder(message));

        _rules.AddRule(SenderRuleKind.TrustedSender, message.From.Address);
        Assert.False(_junk.IsLocallyJunk(message));
    }

    [Fact]
    public void Junk_filtering_can_be_turned_off()
    {
        var message = Samples.Message() with { Spam = new SpamReport { HasHeaders = true, Score = 9 } };
        _settings.Update(s => s.TreatHighScoresAsJunk = false);
        Assert.False(_junk.IsLocallyJunk(message));
    }

    [Fact]
    public void Messages_outside_the_inbox_are_never_reclassified()
    {
        var message = Samples.Message(folder: FolderKind.Archive) with { Spam = new SpamReport { HasHeaders = true, Score = 50 } };
        Assert.False(_junk.IsLocallyJunk(message));
    }

    [Fact]
    public void Scope_matching_routes_local_junk_to_junk_views()
    {
        var account = Guid.NewGuid();
        var inbox = Guid.NewGuid();
        var junkFolder = Guid.NewGuid();
        var spam = Samples.Message(accountId: account, folderId: inbox) with { Spam = new SpamReport { HasHeaders = true, Score = 20 } };
        var ham = Samples.Message(accountId: account, folderId: inbox);

        var inboxScope = new MailboxScope(MailboxScopeKind.AccountFolder, FolderKind.Inbox, account, inbox);
        var junkScope = new MailboxScope(MailboxScopeKind.AccountFolder, FolderKind.Junk, account, junkFolder);

        Assert.False(MailboxScopeMatcher.Matches(spam, inboxScope, _junk.IsLocallyJunk));
        Assert.True(MailboxScopeMatcher.Matches(spam, junkScope, _junk.IsLocallyJunk));
        Assert.True(MailboxScopeMatcher.Matches(spam, MailboxScope.Unified(FolderKind.Junk), _junk.IsLocallyJunk));
        Assert.False(MailboxScopeMatcher.Matches(spam, MailboxScope.UnifiedInbox, _junk.IsLocallyJunk));
        Assert.True(MailboxScopeMatcher.Matches(ham, MailboxScope.UnifiedInbox, _junk.IsLocallyJunk));
    }

    [Fact]
    public void Pinned_and_favourite_scopes_span_folders_but_exclude_trash()
    {
        var pinnedArchive = Samples.Message(folder: FolderKind.Archive, pinned: true);
        var pinnedTrash = Samples.Message(folder: FolderKind.Trash, pinned: true);
        var starredSent = Samples.Message(folder: FolderKind.Sent, starred: true);

        Assert.True(MailboxScopeMatcher.Matches(pinnedArchive, MailboxScope.Pinned, _junk.IsLocallyJunk));
        Assert.False(MailboxScopeMatcher.Matches(pinnedTrash, MailboxScope.Pinned, _junk.IsLocallyJunk));
        Assert.True(MailboxScopeMatcher.Matches(starredSent, MailboxScope.Favourites, _junk.IsLocallyJunk));
        Assert.True(MailboxScopeMatcher.Matches(starredSent, MailboxScope.AllAccounts, _junk.IsLocallyJunk));
        Assert.False(MailboxScopeMatcher.Matches(pinnedTrash, MailboxScope.AllAccounts, _junk.IsLocallyJunk));
    }

    [Fact]
    public void Notifications_are_suppressed_for_blocked_senders()
    {
        var policy = new NotificationPolicy(_settings, _rules, _junk);
        var message = Samples.Message("Pest <pest@annoying.example>");
        Assert.True(policy.ShouldNotify(message));

        _rules.AddRule(SenderRuleKind.BlockedSender, "pest@annoying.example");
        Assert.False(policy.ShouldNotify(message));
    }
}
