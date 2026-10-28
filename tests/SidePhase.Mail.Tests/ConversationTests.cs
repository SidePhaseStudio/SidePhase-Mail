using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Services;

namespace SidePhase.Mail.Tests;

public class ConversationTests
{
    private static readonly Guid Account = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly EmailAddress Alice = new("Alice", "alice@example.test");
    private static readonly EmailAddress Bob = new("Bob", "bob@example.test");
    private static readonly EmailAddress Carol = new("Carol", "carol@example.test");

    private static MessageSummary Message(
        string subject,
        int hours,
        string? id = null,
        string[]? references = null,
        EmailAddress? from = null,
        EmailAddress? to = null,
        FolderKind folder = FolderKind.Inbox,
        Guid? account = null,
        bool read = true) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = account ?? Account,
        FolderId = Guid.NewGuid(),
        FolderKind = folder,
        From = from ?? Alice,
        To = [to ?? Bob],
        Subject = subject,
        Date = Start.AddHours(hours),
        MessageIdHeader = id,
        References = references ?? [],
        IsRead = read,
    };

    [Fact]
    public void Replies_are_grouped_through_their_headers()
    {
        var first = Message("Plans", 0, "<a@x>");
        var reply = Message("Re: Plans", 1, "<b@x>", ["<a@x>"], from: Bob, to: Alice);
        var other = Message("Plans", 2, "<c@x>", from: Carol);

        var groups = ConversationGrouper.Group([first, reply, other]);

        Assert.Equal(2, groups.Count);
        var thread = Assert.Single(groups, g => g.Count == 2);
        Assert.Equal(first.Id, thread.Key);
        Assert.Equal(reply.Id, thread.Latest.Id);
    }

    [Fact]
    public void Siblings_replying_to_a_missing_parent_are_grouped()
    {
        var a = Message("Re: Lunch", 1, "<b@x>", ["<root@x>"]);
        var b = Message("Re: Lunch", 2, "<c@x>", ["<ROOT@x>"], from: Carol);
        Assert.Single(ConversationGrouper.Group([a, b]));
    }

    [Fact]
    public void Subject_fallback_needs_a_reply_prefix_and_a_shared_participant()
    {
        var invoice1 = Message("Invoice", 0, from: Carol, to: Alice);
        var invoice2 = Message("Invoice", 24, from: Carol, to: Alice); // same subject, no "Re:" → separate
        var reply = Message("RE: Status", 3, to: Alice, from: Bob);
        var original = Message("Status", 0, from: Alice, to: Bob);
        var stranger = Message("Re: Status", 4, from: Carol, to: new EmailAddress(null, "dave@example.test"));

        var groups = ConversationGrouper.Group([invoice1, invoice2, reply, original, stranger]);

        Assert.Equal(4, groups.Count);
        Assert.Contains(groups, g => g.Count == 2 && g.Messages.Contains(reply) && g.Messages.Contains(original));
    }

    [Fact]
    public void Subject_fallback_does_not_join_messages_far_apart()
    {
        var old = Message("Report", 0);
        var reply = Message("Re: Report", 24 * 90, from: Bob, to: Alice);
        Assert.Equal(2, ConversationGrouper.Group([old, reply]).Count);
    }

    [Fact]
    public void Conversations_never_span_accounts()
    {
        var mine = Message("Plans", 0, "<a@x>");
        var elsewhere = Message("Re: Plans", 1, "<b@x>", ["<a@x>"], account: Guid.NewGuid());
        Assert.Equal(2, ConversationGrouper.Group([mine, elsewhere]).Count);
    }

    [Fact]
    public void The_list_shows_one_row_per_conversation_including_sent_replies()
    {
        var first = Message("Plans", 0, "<a@x>", read: false);
        var mySent = Message("Re: Plans", 1, "<b@x>", ["<a@x>"], from: Bob, to: Alice, folder: FolderKind.Sent);
        var answer = Message("Re: Plans", 2, "<c@x>", ["<a@x>", "<b@x>"]);
        var unrelated = Message("Hello", 3, "<d@x>");

        var sections = MessageListBuilder.Build([first, answer, unrelated], new MessageListOptions
        {
            Now = Start.AddDays(1),
            GroupConversations = true,
            ConversationContext = [mySent],
            GroupByDate = false,
        });

        var listed = Assert.Single(sections).Messages;
        Assert.Equal([unrelated.Id, answer.Id], listed.Select(m => m.Id));
        var entry = sections[0].Conversations[answer.Id];
        Assert.Equal([first.Id, mySent.Id, answer.Id], entry.Conversation.Messages.Select(m => m.Id));
        Assert.Equal([first.Id, answer.Id], entry.InView.Select(m => m.Id));
        Assert.False(sections[0].Conversations.ContainsKey(unrelated.Id));
    }

    [Fact]
    public void Conversations_made_only_of_context_messages_are_not_listed()
    {
        var sent = Message("Plans", 0, "<a@x>", folder: FolderKind.Sent);
        var inbox = Message("Hello", 1, "<z@x>");
        var sections = MessageListBuilder.Build([inbox], new MessageListOptions
        {
            Now = Start.AddDays(1),
            GroupConversations = true,
            ConversationContext = [sent],
            GroupByDate = false,
        });
        Assert.Equal([inbox.Id], Assert.Single(sections).Messages.Select(m => m.Id));
    }

    [Fact]
    public void A_message_stored_in_two_folders_appears_once_in_its_conversation()
    {
        var inbox = Message("Plans", 0, "<a@x>");
        var archived = inbox with { Id = Guid.NewGuid(), FolderKind = FolderKind.Archive };
        var reply = Message("Re: Plans", 1, "<b@x>", ["<a@x>"], from: Bob, to: Alice);

        var sections = MessageListBuilder.Build([inbox, reply], new MessageListOptions
        {
            Now = Start.AddDays(1),
            GroupConversations = true,
            ConversationContext = [archived],
            GroupByDate = false,
        });

        var entry = sections[0].Conversations[reply.Id];
        Assert.Equal([inbox.Id, reply.Id], entry.Conversation.Messages.Select(m => m.Id));
    }

    [Fact]
    public void Grouping_is_off_unless_asked_for()
    {
        var first = Message("Plans", 0, "<a@x>");
        var reply = Message("Re: Plans", 1, "<b@x>", ["<a@x>"]);
        var sections = MessageListBuilder.Build([first, reply], new MessageListOptions { Now = Start.AddDays(1), GroupByDate = false });
        Assert.Equal(2, sections[0].Messages.Count);
        Assert.Empty(sections[0].Conversations);
    }
}
