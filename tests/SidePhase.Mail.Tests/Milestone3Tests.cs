using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Services;
using SidePhase.Mail.Core.Settings;
using SidePhase.Mail.Infrastructure.Demo;

namespace SidePhase.Mail.Tests;

public class SearchTests
{
    private static readonly DateTimeOffset Now = Samples.Now;

    private static bool Matches(string query, MessageSummary m, SearchContext? context = null) =>
        MessageSearch.Compile(query, (context ?? new SearchContext()) with { Now = Now })(m);

    [Fact]
    public void Date_operators()
    {
        var m = Samples.Message(date: new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        Assert.True(Matches("after:2026-10-19", m));
        Assert.False(Matches("after:2026-10-20", m));
        Assert.True(Matches("before:2026-10-21", m));
        Assert.False(Matches("before:2026-10-20", m));
        Assert.True(Matches("on:2026-10-20", m));
        Assert.True(Matches("newer_than:2w", m));
        Assert.False(Matches("newer_than:3d", m));
        Assert.True(Matches("older_than:3d", m));
    }

    [Fact]
    public void Unparseable_operators_fall_back_to_text()
    {
        var m = Samples.Message(subject: "Meet after:lunch");
        Assert.True(Matches("after:lunch", m));
    }

    [Fact]
    public void Free_text_also_matches_downloaded_message_text()
    {
        var m = Samples.Message(subject: "Hello");
        Assert.False(Matches("invoice", m));
        var context = new SearchContext { BodyMatches = new Dictionary<string, IReadOnlySet<Guid>> { ["invoice"] = new HashSet<Guid> { m.Id } } };
        Assert.True(Matches("invoice", m, context));
        Assert.True(Matches("body:invoice", m, context));
        Assert.False(Matches("subject:invoice", m, context));
    }

    [Fact]
    public void Server_matches_satisfy_text_terms_but_not_local_conditions()
    {
        var m = Samples.Message(subject: "Old thread", read: true);
        var context = new SearchContext { ServerMatches = new HashSet<Guid> { m.Id } };
        Assert.True(Matches("contract", m, context));
        Assert.True(Matches("from:lawyer contract", m, context));
        Assert.False(Matches("contract is:unread", m, context));
        Assert.False(Matches("contract is:pinned", m, context));
    }

    [Fact]
    public void Text_terms_for_body_lookup()
    {
        Assert.Equal(["invoice", "march 2026"], MessageSearch.TextTerms("invoice from:jane \"march 2026\" is:unread body:invoice"));
    }

    [Fact]
    public async Task Demo_source_searches_message_text_and_has_no_server()
    {
        var demo = new DemoMailDataSource(new FixedClock(Now), TimeSpan.Zero);
        var body = (await demo.GetMessageBodyAsync((await demo.GetMessagesAsync()).First().Id))!;
        var word = body.TextBody!.Split(' ', StringSplitOptions.RemoveEmptyEntries).First(w => w.Length > 5 && w.All(char.IsLetter));
        var matches = await demo.SearchDownloadedTextAsync([word]);
        Assert.Contains(body.MessageId, matches[word]);
        Assert.False((await demo.SearchServerAsync("anything", MailboxScope.UnifiedInbox)).Supported);
    }
}

public class JunkRuleAndNotificationTests
{
    private readonly FixedClock _clock = new(Samples.Now);

    private (SettingsService Settings, SenderRulesService Rules) Services()
    {
        var settings = new SettingsService(new InMemorySettingsStore());
        return (settings, new SenderRulesService(settings, _clock));
    }

    [Fact]
    public async Task Auto_junk_is_off_by_default_and_never_moves_trusted_or_unblocked_senders()
    {
        var (settings, rules) = Services();
        var demo = new DemoMailDataSource(_clock, TimeSpan.Zero);
        var service = new AutoJunkService(demo, rules, settings);
        var blocked = Samples.Message("Spammer <spam@bad.example>");
        var normal = Samples.Message("Friend <friend@good.example>");
        rules.AddRule(SenderRuleKind.BlockedDomain, "bad.example");

        Assert.Empty(service.SelectForJunk([blocked, normal]));

        settings.Update(s => s.MoveBlockedMailToServerJunk = true);
        // The demo mailbox never changes anything automatically.
        Assert.Empty(service.SelectForJunk([blocked, normal]));
    }

    [Fact]
    public void Auto_junk_selects_only_blocked_inbox_mail()
    {
        var (settings, rules) = Services();
        settings.Update(s => s.MoveBlockedMailToServerJunk = true);
        var service = new AutoJunkService(new LiveLikeSource(), rules, settings);
        rules.AddRule(SenderRuleKind.BlockedSender, "spam@bad.example");
        rules.AddRule(SenderRuleKind.BlockedDomain, "worse.example");
        rules.AddRule(SenderRuleKind.TrustedSender, "boss@worse.example");

        var selected = service.SelectForJunk(
        [
            Samples.Message("S <spam@bad.example>", subject: "a"),
            Samples.Message("W <x@worse.example>", subject: "b"),
            Samples.Message("Boss <boss@worse.example>", subject: "c"),
            Samples.Message("S <spam@bad.example>", subject: "d", folder: FolderKind.Archive),
            Samples.Message("F <friend@good.example>", subject: "e"),
        ]);
        Assert.Equal(["a", "b"], selected.Select(m => m.Subject));
    }

    [Fact]
    public void Muted_accounts_and_read_or_blocked_mail_never_notify()
    {
        var (settings, rules) = Services();
        var policy = new NotificationPolicy(settings, rules, new JunkPolicy(rules, settings));
        var account = Guid.NewGuid();
        var message = Samples.Message(accountId: account);
        Assert.True(policy.ShouldNotify(message));

        settings.Update(s => s.MutedNotificationAccounts.Add(account));
        Assert.False(policy.ShouldNotify(message));
        Assert.True(policy.ShouldNotify(Samples.Message(accountId: Guid.NewGuid())));
        Assert.False(policy.ShouldNotify(Samples.Message(read: true)));
        Assert.False(policy.ShouldNotify(Samples.Message(folder: FolderKind.Junk)));
    }

    [Fact]
    public async Task Bugger_back_options_are_respected_and_undo_restores_trust()
    {
        var (settings, rules) = Services();
        var demo = new DemoMailDataSource(_clock, TimeSpan.Zero);
        var service = new SendTheBuggerBackService(demo, rules, _clock);
        var message = (await demo.GetMessagesAsync()).First(m => m.FolderKind == FolderKind.Inbox);
        rules.AddRule(SenderRuleKind.TrustedSender, message.From.Address);

        // This message only: no rules, just moved to Junk.
        var once = await service.ExecuteAsync(message, new SendTheBuggerBackOptions(ApplyToFuture: false));
        Assert.Empty(once.AddedRules);
        Assert.True(rules.IsTrusted(message.From));
        Assert.Equal(FolderKind.Junk, (await demo.GetMessageAsync(message.Id))!.FolderKind);
        await service.UndoAsync(once);
        Assert.Equal(FolderKind.Inbox, (await demo.GetMessageAsync(message.Id))!.FolderKind);

        // Block without moving: trust is replaced by a block, and undo puts the trust back.
        var block = await service.ExecuteAsync(message, new SendTheBuggerBackOptions(MoveToJunk: false));
        Assert.True(rules.IsBlocked(message.From));
        Assert.False(rules.IsTrusted(message.From));
        Assert.Empty(block.Moves);
        Assert.Equal(FolderKind.Inbox, (await demo.GetMessageAsync(message.Id))!.FolderKind);
        await service.UndoAsync(block);
        Assert.False(rules.IsBlocked(message.From));
        Assert.True(rules.IsTrusted(message.From));
    }

    /// <summary>Minimal non-demo source for policy tests (no real server).</summary>
    private sealed class LiveLikeSource : Core.Abstractions.IMailDataSource
    {
        private readonly DemoMailDataSource _inner = new(new FixedClock(Samples.Now), TimeSpan.Zero);
        public bool IsDemo => false;
        public event EventHandler<Core.Abstractions.MailDataChangedEventArgs>? DataChanged { add { } remove { } }
        public event EventHandler<Core.Abstractions.MailErrorEventArgs>? ErrorOccurred { add { } remove { } }
        public Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken c = default) => _inner.GetAccountsAsync(c);
        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid a, CancellationToken c = default) => _inner.GetFoldersAsync(a, c);
        public Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(CancellationToken c = default) => _inner.GetMessagesAsync(c);
        public Task<MessageSummary?> GetMessageAsync(Guid id, CancellationToken c = default) => _inner.GetMessageAsync(id, c);
        public Task<MessageBody?> GetMessageBodyAsync(Guid id, CancellationToken c = default) => _inner.GetMessageBodyAsync(id, c);
        public Task<byte[]> GetAttachmentContentAsync(Guid id, int i, CancellationToken c = default) => _inner.GetAttachmentContentAsync(id, i, c);
        public Task SetReadAsync(IReadOnlyCollection<Guid> ids, bool v, CancellationToken c = default) => _inner.SetReadAsync(ids, v, c);
        public Task SetStarredAsync(IReadOnlyCollection<Guid> ids, bool v, CancellationToken c = default) => _inner.SetStarredAsync(ids, v, c);
        public Task SetPinnedAsync(IReadOnlyCollection<Guid> ids, bool v, CancellationToken c = default) => _inner.SetPinnedAsync(ids, v, c);
        public Task<IReadOnlyList<Core.Abstractions.MoveRecord>> MoveToAsync(IReadOnlyCollection<Guid> ids, FolderKind t, CancellationToken c = default) => _inner.MoveToAsync(ids, t, c);
        public Task RestoreAsync(IReadOnlyCollection<Core.Abstractions.MoveRecord> m, CancellationToken c = default) => _inner.RestoreAsync(m, c);
        public Task DeletePermanentlyAsync(IReadOnlyCollection<Guid> ids, CancellationToken c = default) => _inner.DeletePermanentlyAsync(ids, c);
        public Task<Guid> SaveDraftAsync(DraftMessage d, CancellationToken c = default) => _inner.SaveDraftAsync(d, c);
        public Task<DraftMessage?> GetDraftAsync(Guid id, CancellationToken c = default) => _inner.GetDraftAsync(id, c);
        public Task<Core.Abstractions.SendResult> SendAsync(DraftMessage d, IProgress<double>? p = null, CancellationToken c = default) => _inner.SendAsync(d, p, c);
        public Task<Core.Abstractions.SyncResult> SyncAsync(CancellationToken c = default) => _inner.SyncAsync(c);
        public Task<IReadOnlyDictionary<string, IReadOnlySet<Guid>>> SearchDownloadedTextAsync(IReadOnlyCollection<string> t, CancellationToken c = default) => _inner.SearchDownloadedTextAsync(t, c);
        public Task<Core.Abstractions.ServerSearchResult> SearchServerAsync(string q, MailboxScope s, CancellationToken c = default) => _inner.SearchServerAsync(q, s, c);
        public Task ClearLocalDataAsync(CancellationToken c = default) => _inner.ClearLocalDataAsync(c);
    }
}
