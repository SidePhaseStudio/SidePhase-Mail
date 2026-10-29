using SidePhase.Mail.Core.Services;

namespace SidePhase.Mail.Tests;

public class MessageListTests
{
    private static MessageListOptions Options(Func<MessageListOptions, MessageListOptions>? configure = null)
    {
        var o = new MessageListOptions { Now = Samples.Now };
        return configure?.Invoke(o) ?? o;
    }

    [Fact]
    public void Pinned_messages_get_their_own_section_first()
    {
        var older = Samples.Message(subject: "old pinned", date: Samples.Now.AddDays(-30), pinned: true);
        var newer = Samples.Message(subject: "today", date: Samples.Now.AddHours(-1));
        var sections = MessageListBuilder.Build([newer, older], Options());

        Assert.Equal(MessageSectionKind.Pinned, sections[0].Kind);
        Assert.Same(older, sections[0].Messages.Single());
        Assert.True(sections[0].IsCollapsible);
        Assert.Equal(MessageSectionKind.Today, sections[1].Kind);
        Assert.DoesNotContain(older, sections[1].Messages);
    }

    [Fact]
    public void Collapsed_state_is_carried_on_the_pinned_section()
    {
        var pinned = Samples.Message(pinned: true);
        var normal = Samples.Message();
        var sections = MessageListBuilder.Build([pinned, normal], Options(o => o with { PinnedSectionCollapsed = true }));
        Assert.True(sections[0].IsCollapsed);
    }

    [Fact]
    public void No_pinned_section_when_everything_is_pinned()
    {
        var a = Samples.Message(pinned: true);
        var b = Samples.Message(pinned: true, date: Samples.Now.AddDays(-1));
        var sections = MessageListBuilder.Build([a, b], Options());
        Assert.DoesNotContain(sections, s => s.Kind == MessageSectionKind.Pinned);
    }

    [Fact]
    public void Pinned_section_can_be_disabled()
    {
        var pinned = Samples.Message(pinned: true);
        var normal = Samples.Message(date: Samples.Now.AddMinutes(-5));
        var sections = MessageListBuilder.Build([pinned, normal], Options(o => o with { ShowPinnedSection = false }));
        Assert.Single(sections);
        Assert.Equal(2, sections[0].Messages.Count);
    }

    [Theory]
    [InlineData(0, MessageSectionKind.Today)]
    [InlineData(1, MessageSectionKind.Yesterday)]
    [InlineData(5, MessageSectionKind.LastWeek)] // Thu of previous week (now is Tue)
    [InlineData(20, MessageSectionKind.EarlierThisMonth)]
    [InlineData(40, MessageSectionKind.Older)]
    public void Date_buckets(int daysAgo, MessageSectionKind expected)
    {
        Assert.Equal(expected, MessageListBuilder.Bucket(Samples.Now.AddDays(-daysAgo), Samples.Now));
    }

    [Fact]
    public void Earlier_this_week_bucket()
    {
        var wednesday = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(MessageSectionKind.EarlierThisWeek, MessageListBuilder.Bucket(wednesday.AddDays(-2), wednesday));
    }

    [Fact]
    public void Filters_apply()
    {
        var unread = Samples.Message(read: false);
        var read = Samples.Message(read: true, starred: true, attachments: true);
        Assert.Same(unread, MessageListBuilder.Build([unread, read], Options(o => o with { Filter = MessageFilter.Unread })).Single().Messages.Single());
        Assert.Same(read, MessageListBuilder.Build([unread, read], Options(o => o with { Filter = MessageFilter.Starred })).Single().Messages.Single());
        Assert.Same(read, MessageListBuilder.Build([unread, read], Options(o => o with { Filter = MessageFilter.WithAttachments })).Single().Messages.Single());
    }

    [Fact]
    public void The_open_message_stays_listed_under_Unread_after_it_is_marked_read()
    {
        // Regression: it used to vanish, the list auto-advanced to the next message and marked that read too, until
        // every message in the Unread view had been marked read.
        var open = Samples.Message(read: true);
        var other = Samples.Message(read: false);
        var alreadyRead = Samples.Message(read: true);
        var listed = MessageListBuilder.Build([open, other, alreadyRead], Options(o => o with { Filter = MessageFilter.Unread, KeepVisible = new HashSet<Guid> { open.Id } }))
            .SelectMany(s => s.Messages).ToList();
        Assert.Contains(open, listed);
        Assert.Contains(other, listed);
        Assert.DoesNotContain(alreadyRead, listed);
    }

    [Fact]
    public void Non_date_sorts_produce_a_single_untitled_section()
    {
        var b = Samples.Message("Bob <bob@x.example>");
        var a = Samples.Message("alice <alice@x.example>", date: Samples.Now.AddDays(-40));
        var sections = MessageListBuilder.Build([b, a], Options(o => o with { Sort = MessageSort.SenderAscending }));
        var section = Assert.Single(sections);
        Assert.Equal(MessageSectionKind.All, section.Kind);
        Assert.Equal([a, b], section.Messages);
    }

    [Fact]
    public void Subject_sort_ignores_reply_prefixes()
    {
        var reply = Samples.Message(subject: "Re: Zebra");
        var apple = Samples.Message(subject: "Fwd: RE: Apple");
        var sorted = MessageListBuilder.Sort([reply, apple], MessageSort.SubjectAscending);
        Assert.Equal([apple, reply], sorted);
    }

    [Theory]
    [InlineData("invoice", true)]
    [InlineData("from:jane", true)]
    [InlineData("from:bob", false)]
    [InlineData("subject:invoice is:unread", true)]
    [InlineData("is:read", false)]
    [InlineData("has:attachment", true)]
    [InlineData("\"march invoice\"", true)]
    [InlineData("\"invoice march\"", false)]
    public void Search_operators(string query, bool expected)
    {
        var m = Samples.Message("Jane Doe <jane@example.com>", subject: "Your March invoice", attachments: true);
        Assert.Equal(expected, MessageSearch.Compile(query)(m));
    }
}
