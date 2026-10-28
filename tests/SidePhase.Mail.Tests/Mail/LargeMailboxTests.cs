using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Services;
using SidePhase.Mail.Infrastructure.Mail;
using SidePhase.Mail.Infrastructure.Storage;
using Xunit.Abstractions;

namespace SidePhase.Mail.Tests.Mail;

/// <summary>
/// Realistic-size checks: a 20,000-message cache across two accounts with long conversations. The time budgets are
/// generous (CI machines vary) and only catch real regressions; the measured times are printed for the release notes.
/// </summary>
public sealed class LargeMailboxTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int Accounts = 2;
    private const int PerAccount = 10_000;
    private const int ThreadLength = 10;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-large-" + Guid.NewGuid().ToString("N"));
    private MailCache _cache = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _cache = new MailCache(new MailDatabase(Path.Combine(_dir, "large.db")));
        await _cache.InitializeAsync();

        var watch = Stopwatch.StartNew();
        var start = DateTimeOffset.UtcNow.AddDays(-400);
        for (var a = 0; a < Accounts; a++)
        {
            var accountId = Guid.NewGuid();
            await _cache.AddAccountAsync(MailCacheTests.Account(accountId, $"user{a}@example.test"));
            var inbox = new StoredFolder
            {
                Id = MessageIdentity.ForFolder(accountId, "INBOX"), AccountId = accountId, Path = "INBOX", Name = "Inbox", Kind = FolderKind.Inbox,
            };
            await _cache.UpsertFolderAsync(inbox);

            var batch = new List<StoredMessage>();
            for (var i = 0; i < PerAccount; i++)
            {
                var thread = i / ThreadLength;
                var messageId = $"<m{a}-{i}@example.test>";
                var from = new EmailAddress($"Sender {i % 300}", $"sender{i % 300}@corp{i % 40}.example");
                var date = start.AddMinutes(i * 50);
                batch.Add(new StoredMessage
                {
                    Id = MessageIdentity.ForImapMessage(accountId, "INBOX", 1, (uint)(i + 1)),
                    AccountId = accountId,
                    FolderId = inbox.Id,
                    Uid = (uint)(i + 1),
                    UidValidity = 1,
                    MessageIdHeader = messageId,
                    InReplyTo = i % ThreadLength == 0 ? null : $"<m{a}-{i - 1}@example.test>",
                    References = i % ThreadLength == 0 ? [] : [$"<m{a}-{thread * ThreadLength}@example.test>"],
                    IdentityKey = MessageIdentity.IdentityKey(messageId, from, date, $"Project {thread}"),
                    From = from,
                    To = [new EmailAddress(null, $"user{a}@example.test")],
                    Subject = (i % ThreadLength == 0 ? "" : "Re: ") + $"Project {thread} quarterly planning",
                    Preview = $"Notes about budget item {i} and the roadmap for team {i % 25}",
                    Date = date,
                    IsRead = i % 3 != 0,
                    IsStarred = i % 97 == 0,
                    HasAttachments = i % 11 == 0,
                    Size = 4_000 + (i % 50) * 30_000,
                });
                if (batch.Count == 1000)
                {
                    await _cache.UpsertMessagesAsync(batch);
                    batch.Clear();
                }
            }

            if (batch.Count > 0) await _cache.UpsertMessagesAsync(batch);
        }

        output.WriteLine($"Seeded {Accounts * PerAccount:N0} messages in {watch.ElapsedMilliseconds:N0} ms");
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private async Task<T> TimeAsync<T>(string what, Func<Task<T>> action, TimeSpan budget)
    {
        var watch = Stopwatch.StartNew();
        var result = await action();
        watch.Stop();
        output.WriteLine($"{what}: {watch.ElapsedMilliseconds:N0} ms");
        Assert.True(watch.Elapsed < budget, $"{what} took {watch.ElapsedMilliseconds} ms (budget {budget.TotalMilliseconds} ms)");
        return result;
    }

    [Fact]
    public async Task Large_mailbox_lists_searches_and_groups_within_budget()
    {
        var all = await TimeAsync("Load all summaries", () => _cache.GetSummariesAsync(), TimeSpan.FromSeconds(10));
        Assert.Equal(Accounts * PerAccount, all.Count);

        var hits = await TimeAsync("Full-text search (prefix, 2 words)",
            () => _cache.SearchIndexAsync("\"quarter\"* AND \"roadmap\"*"), TimeSpan.FromSeconds(3));
        Assert.Equal(Accounts * PerAccount, hits.Count);

        var narrow = await TimeAsync("Full-text search (rare word)", () => _cache.SearchIndexAsync("\"item\" AND \"9999\""), TimeSpan.FromSeconds(3));
        Assert.Equal(Accounts, narrow.Count);

        var sections = await TimeAsync("Build list with conversations", () => Task.FromResult(MessageListBuilder.Build(all, new MessageListOptions
        {
            Now = DateTimeOffset.UtcNow,
            GroupConversations = true,
        })), TimeSpan.FromSeconds(5));
        var rows = sections.Sum(s => s.Messages.Count);
        output.WriteLine($"Conversation rows: {rows:N0}");
        Assert.InRange(rows, Accounts * PerAccount / ThreadLength, Accounts * PerAccount / ThreadLength + 10);

        var smart = await TimeAsync("Smart folder query (unread with attachments, larger than 500 KB)", () => Task.FromResult(
            all.Where(m => !m.IsRead && m.HasAttachments && m.Size > 500_000).ToList()), TimeSpan.FromSeconds(1));
        Assert.NotEmpty(smart);
    }
}
