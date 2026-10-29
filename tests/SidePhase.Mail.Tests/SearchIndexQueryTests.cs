using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Services;

namespace SidePhase.Mail.Tests;

public class SearchIndexQueryTests
{
    [Theory]
    [InlineData(null, "invoice", "\"invoice\"*")]
    [InlineData(null, "march 2026", "\"march\"* \"2026\"*")]
    [InlineData("body", "zebra", "body : (\"zebra\"*)")]
    [InlineData("filename", "budget.xlsx", "attachments : (\"budget\"* \"xlsx\"*)")]
    [InlineData(null, "NEAR(\"a\" OR b)*", "\"NEAR\"* \"a\"* \"OR\"* \"b\"*")] // FTS syntax typed by the user is just text
    public void Builds_safe_prefix_queries(string? op, string value, string expected) =>
        Assert.Equal(expected, SearchIndexQuery.For(new SearchTerm(op, value)));

    [Fact]
    public void Punctuation_only_searches_nothing() => Assert.Null(SearchIndexQuery.For(new SearchTerm(null, "--- !!")));

    [Fact]
    public void Keys_round_trip()
    {
        Assert.Equal(new SearchTerm("body", "x"), SearchIndexQuery.FromKey(new SearchTerm("body", "x").IndexKey));
        Assert.Equal(new SearchTerm(null, "from:x"), SearchIndexQuery.FromKey("from:x"));
    }
}

public class AdvancedOperatorTests
{
    private static readonly Guid Work = Guid.NewGuid();
    private static readonly Guid Projects = Guid.NewGuid();

    private static readonly SearchContext Context = new()
    {
        AccountNames = new Dictionary<Guid, string> { [Work] = "Work alice@work.example" },
        FolderNames = new Dictionary<Guid, string> { [Projects] = "Projects / 2026" },
        CategoriesOf = m => m.Subject.Contains("Q4") ? ["Finance"] : [],
    };

    private static MessageSummary M(string subject = "Hello", long size = 1000, FolderKind kind = FolderKind.Inbox, params AttachmentInfo[] attachments) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = Work,
        FolderId = Projects,
        FolderKind = kind,
        From = new EmailAddress("Bob", "bob@example.test"),
        Subject = subject,
        Date = DateTimeOffset.UtcNow,
        Size = size,
        HasAttachments = attachments.Length > 0,
        Attachments = attachments,
    };

    private static bool Matches(string query, MessageSummary m) => MessageSearch.Compile(query, Context)(m);

    [Fact]
    public void Account_folder_and_location_operators()
    {
        var m = M();
        Assert.True(Matches("account:work", m));
        Assert.True(Matches("account:alice@work", m));
        Assert.False(Matches("account:home", m));
        Assert.True(Matches("folder:projects", m));
        Assert.True(Matches("in:inbox", m));
        Assert.False(Matches("in:sent", m));
        Assert.True(Matches("in:sent", M(kind: FolderKind.Sent)));
    }

    [Fact]
    public void Size_and_attachment_operators()
    {
        var big = M(size: 6 * 1024 * 1024, attachments: [new AttachmentInfo("Holiday.JPG", "image/jpeg", 6_000_000)]);
        var small = M(size: 4_000, attachments: [new AttachmentInfo("Contract-final.pdf", "application/pdf", 3_000)]);
        Assert.True(Matches("larger:5mb", big));
        Assert.False(Matches("larger:5mb", small));
        Assert.True(Matches("smaller:10kb", small));
        Assert.True(Matches("filename:contract", small));
        Assert.False(Matches("filename:contract", big));
        Assert.True(Matches("filetype:image", big));
        Assert.True(Matches("filetype:pdf", small));
        Assert.True(Matches("filetype:jpg", big));
        Assert.False(Matches("filetype:pdf", big));

        // Plain search words find attachments by name too (the separate Attachments page was folded into search).
        Assert.True(Matches("contract", small));
        Assert.False(Matches("contract", big));
    }

    [Fact]
    public void Category_and_follow_up_operators()
    {
        Assert.True(Matches("category:finance", M("Q4 numbers")));
        Assert.False(Matches("category:finance", M("Lunch")));
        Assert.True(Matches("is:followup", M() with { FollowUpAt = DateTimeOffset.UtcNow }));
    }

    [Theory]
    [InlineData("5mb", 5L * 1024 * 1024)]
    [InlineData("200KB", 200L * 1024)]
    [InlineData("1.5m", 1572864L)]
    [InlineData("1000", 1000L)]
    public void Sizes_parse(string text, long expected)
    {
        Assert.True(MessageSearch.TryParseSize(text, out var bytes));
        Assert.Equal(expected, bytes);
    }
}

public class AdvancedSearchFormTests
{
    [Fact]
    public void Form_writes_a_query_and_reads_it_back()
    {
        var form = new AdvancedSearch
        {
            Words = "budget \"next year\"",
            From = "Jane Doe",
            Account = "work",
            In = "inbox",
            After = new DateTime(2026, 1, 31),
            HasAttachment = true,
            Unread = true,
            FileType = "pdf",
            LargerThan = "2mb",
        };

        var query = form.ToQuery();
        Assert.Equal("budget \"next year\" from:\"Jane Doe\" account:work in:inbox after:2026-01-31 has:attachment is:unread filetype:pdf larger:2mb", query);
        Assert.Equal(form, AdvancedSearch.FromQuery(query));
    }

    [Fact]
    public void Unknown_operators_are_kept_as_words()
    {
        var form = AdvancedSearch.FromQuery("category:finance hello is:followup");
        Assert.Equal("category:finance hello is:followup", form.Words);
        Assert.Equal("category:finance hello is:followup", form.ToQuery());
    }
}

public class SmartFolderTests
{
    private static MessageSummary M(FolderKind kind = FolderKind.Inbox, bool read = false, string from = "bank@acme.example", long size = 100, DateTimeOffset? date = null) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        FolderId = Guid.NewGuid(),
        FolderKind = kind,
        From = new EmailAddress(null, from),
        Subject = "Statement",
        Date = date ?? DateTimeOffset.UtcNow,
        IsRead = read,
        Size = size,
    };

    private static Func<MessageSummary, bool> Filter(string query) =>
        SidePhase.Mail.Core.Organisation.SmartFolderFilter.Build(
            new SidePhase.Mail.Core.Organisation.SmartFolder { Id = Guid.NewGuid(), Name = "x", Query = query },
            new SearchContext(),
            _ => false);

    [Fact]
    public void Smart_folders_filter_without_touching_junk_trash_or_snoozed_mail()
    {
        var f = Filter("from:acme.example is:unread");
        Assert.True(f(M()));
        Assert.False(f(M(read: true)));
        Assert.False(f(M(from: "friend@example.test")));
        Assert.False(f(M(kind: FolderKind.Junk)));
        Assert.False(f(M(kind: FolderKind.Trash)));
        Assert.False(f(M() with { SnoozedUntil = DateTimeOffset.UtcNow.AddHours(1) }));
        Assert.True(Filter("in:junk from:acme")(M(kind: FolderKind.Junk)));
    }

    [Fact]
    public void Built_in_definitions_work()
    {
        Assert.True(Filter("larger:5mb")(M(size: 8L * 1024 * 1024)));
        Assert.False(Filter("larger:5mb")(M(size: 1024)));
        Assert.True(Filter("in:inbox newer_than:3d")(M()));
        Assert.False(Filter("in:inbox newer_than:3d")(M(date: DateTimeOffset.UtcNow.AddDays(-10))));
    }

    [Fact]
    public async Task Defaults_are_created_once_and_deleting_them_is_respected()
    {
        var store = new MemorySmartFolderStore();
        var settings = new SidePhase.Mail.Core.Settings.SettingsService(new InMemorySettingsStore());
        await settings.LoadAsync();
        var service = new SidePhase.Mail.Core.Organisation.SmartFolderService(store, settings, new FixedClock(DateTimeOffset.UtcNow));

        var first = await service.ListAsync();
        Assert.Equal(SidePhase.Mail.Core.Organisation.SmartFolderService.Defaults.Count, first.Count);
        await service.DeleteAsync(first[0].Id);
        Assert.Equal(first.Count - 1, (await service.ListAsync()).Count);

        var custom = await service.SaveAsync(new SidePhase.Mail.Core.Organisation.SmartFolder { Id = Guid.NewGuid(), Name = "  Bank  ", Query = "from:acme" });
        Assert.Equal("Bank", custom.Name);
        Assert.Equal(custom.Id, (await service.ListAsync())[^1].Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(custom with { Query = " " }));
    }

    private sealed class MemorySmartFolderStore : SidePhase.Mail.Core.Organisation.ISmartFolderStore
    {
        private readonly Dictionary<Guid, SidePhase.Mail.Core.Organisation.SmartFolder> _items = [];

        public Task<IReadOnlyList<SidePhase.Mail.Core.Organisation.SmartFolder>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SidePhase.Mail.Core.Organisation.SmartFolder>>(_items.Values.ToList());

        public Task SaveAsync(SidePhase.Mail.Core.Organisation.SmartFolder folder, CancellationToken ct = default)
        {
            _items[folder.Id] = folder;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            _items.Remove(id);
            return Task.CompletedTask;
        }
    }
}
