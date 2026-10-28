using Microsoft.Data.Sqlite;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Spam;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Tests;

public sealed class LocalSpamFilterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-bayes-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _account = Guid.NewGuid();

    public LocalSpamFilterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<(LocalSpamFilter Filter, MailCache Cache)> CreateAsync()
    {
        var cache = new MailCache(new MailDatabase(Path.Combine(_dir, "b.db")));
        await cache.InitializeAsync();
        var filter = new LocalSpamFilter(new SqliteSpamLearningStore(cache));
        await filter.LoadAsync();
        return (filter, cache);
    }

    private MessageSummary Mail(string from, string subject, string preview) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = _account,
        FolderId = Guid.NewGuid(),
        FolderKind = FolderKind.Inbox,
        MessageIdHeader = $"<{Guid.NewGuid():N}@x>",
        From = new EmailAddress(null, from),
        Subject = subject,
        Preview = preview,
        Date = DateTimeOffset.UtcNow,
    };

    private MessageSummary Spam(int i) => Mail($"promo{i}@cheap-deals.example", $"WIN a FREE prize now {i}", "Claim your free casino bonus today, limited offer, click to win cash");
    private MessageSummary Ham(int i) => Mail("jane@family.example", $"Dinner on Sunday {i}", "Shall we meet at the restaurant near the station? Mum is coming too");

    [Fact]
    public async Task Gives_no_opinion_until_it_has_learned_enough()
    {
        var (filter, _) = await CreateAsync();
        for (var i = 0; i < LocalSpamFilter.MinimumExamples; i++) await filter.TrainAsync(Spam(i), isSpam: true);
        Assert.False(filter.IsReady);
        Assert.Null(filter.Classify(Spam(99)));
    }

    [Fact]
    public async Task Learns_from_marked_messages_and_explains_its_opinion()
    {
        var (filter, _) = await CreateAsync();
        for (var i = 0; i < 8; i++)
        {
            await filter.TrainAsync(Spam(i), isSpam: true);
            await filter.TrainAsync(Ham(i), isSpam: false);
        }

        var junk = filter.Classify(Mail("offers@cheap-deals.example", "Free prize waiting", "Claim your casino bonus now"))!;
        var fine = filter.Classify(Mail("jane@family.example", "Sunday", "Meet at the station restaurant with Mum"))!;
        Assert.True(junk.LikelyJunk, $"junk scored {junk.Probability}");
        Assert.True(fine.LikelyFine, $"ham scored {fine.Probability}");
        Assert.NotEmpty(junk.Evidence);
        Assert.Equal(16, junk.LearnedFrom);

        // Unknown words carry no weight either way.
        Assert.Equal(0.5, filter.Classify(Mail("x@unknown.example", "Qwxz", "Zzqv plort"))!.Probability, 3);
    }

    [Fact]
    public async Task Re_marking_a_message_moves_its_words_instead_of_counting_twice()
    {
        var (filter, _) = await CreateAsync();
        var message = Ham(1);
        await filter.TrainAsync(message, isSpam: true);
        await filter.TrainAsync(message, isSpam: true); // same choice again: no change
        Assert.Equal(1, filter.SpamExamples);
        await filter.TrainAsync(message, isSpam: false); // changed mind
        Assert.Equal(0, filter.SpamExamples);
        Assert.Equal(1, filter.HamExamples);
    }

    [Fact]
    public async Task Learning_persists_and_can_be_reset()
    {
        var (filter, cache) = await CreateAsync();
        for (var i = 0; i < 6; i++)
        {
            await filter.TrainAsync(Spam(i), isSpam: true);
            await filter.TrainAsync(Ham(i), isSpam: false);
        }

        var reloaded = new LocalSpamFilter(new SqliteSpamLearningStore(cache));
        await reloaded.LoadAsync();
        Assert.True(reloaded.IsReady);
        Assert.True(reloaded.Classify(Spam(50))!.LikelyJunk);

        await reloaded.ResetAsync();
        Assert.False(reloaded.IsReady);
        var afterReset = new LocalSpamFilter(new SqliteSpamLearningStore(cache));
        await afterReset.LoadAsync();
        Assert.Equal(0, afterReset.SpamExamples + afterReset.HamExamples);
    }

    [Fact]
    public void Chi_square_tail_is_sane()
    {
        Assert.Equal(1.0, LocalSpamFilter.InverseChiSquare(0, 10), 6);
        Assert.InRange(LocalSpamFilter.InverseChiSquare(100, 10), 0, 1e-10);
    }

    [Fact]
    public void Confident_local_opinions_only_count_when_the_user_turned_that_on()
    {
        var settings = new SidePhase.Mail.Core.Settings.SettingsService(new InMemorySettingsStore());
        var rules = new SidePhase.Mail.Core.Services.SenderRulesService(settings, new FixedClock(DateTimeOffset.UtcNow));
        var policy = new SidePhase.Mail.Core.Services.JunkPolicy(rules, settings, new LocalSpamFilter(new NoLearning()));
        Assert.False(settings.Current.LocalFilterShowsJunk);
        Assert.False(policy.IsLocallyJunk(Spam(1))); // no model, and off by default
    }

    private sealed class NoLearning : ISpamLearningStore
    {
        public Task<SpamModel> LoadAsync(CancellationToken ct = default) => Task.FromResult(new SpamModel());
        public Task<(bool IsSpam, IReadOnlyList<string> Tokens)?> GetTrainingAsync(Guid a, string k, CancellationToken ct = default) => Task.FromResult<(bool, IReadOnlyList<string>)?>(null);
        public Task ApplyAsync(Guid a, string k, bool s, IReadOnlyList<string> t, (bool IsSpam, IReadOnlyList<string> Tokens)? p, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
