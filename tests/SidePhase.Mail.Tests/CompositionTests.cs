using System.Globalization;
using SidePhase.Mail.Core.Composition;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Tests;

public class CompositionTests
{
    private static readonly MailAccount Me = new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "Alex",
        EmailAddress = "alex@me.example",
        Provider = MailProviderKind.Demo,
    };

    [Theory]
    [InlineData("Hello", "Re: Hello")]
    [InlineData("Re: Hello", "Re: Hello")]
    [InlineData("RE: Hello", "RE: Hello")]
    [InlineData("AW: Hallo", "AW: Hallo")]
    [InlineData("", "Re:")]
    public void Reply_subject(string input, string expected) => Assert.Equal(expected, SubjectPrefixes.ForReply(input));

    [Theory]
    [InlineData("Hello", "Fwd: Hello")]
    [InlineData("FW: Hello", "FW: Hello")]
    [InlineData("Re: Hello", "Fwd: Re: Hello")]
    public void Forward_subject(string input, string expected) => Assert.Equal(expected, SubjectPrefixes.ForForward(input));

    [Fact]
    public void Parses_mixed_recipient_list()
    {
        var result = RecipientParser.Parse("jane@example.com; \"Doe, John\" <john@example.com>, Ann <ann@x.org>,");
        Assert.True(result.IsValid);
        Assert.Equal(3, result.Addresses.Count);
        Assert.Equal("Doe, John", result.Addresses[1].Name);
        Assert.Equal("ann@x.org", result.Addresses[2].Address);
    }

    [Fact]
    public void Reports_invalid_recipients_and_removes_duplicates()
    {
        var result = RecipientParser.Parse("good@example.com, not-an-address, GOOD@example.com");
        Assert.Single(result.Addresses);
        Assert.Equal(["not-an-address"], result.Invalid);
    }

    [Fact]
    public void Format_round_trips()
    {
        var text = RecipientParser.Format([new EmailAddress("Doe, John", "john@example.com"), new EmailAddress(null, "a@b.example")]);
        var parsed = RecipientParser.Parse(text);
        Assert.Equal(2, parsed.Addresses.Count);
        Assert.Equal("Doe, John", parsed.Addresses[0].Name);
    }

    private static MessageSummary Original() => new()
    {
        Id = Guid.NewGuid(),
        AccountId = Me.Id,
        FolderId = Guid.NewGuid(),
        FolderKind = FolderKind.Inbox,
        From = new EmailAddress("Jane", "jane@example.com"),
        To = [new EmailAddress("Alex", "ALEX@me.example"), new EmailAddress("Bob", "bob@example.com")],
        Cc = [new EmailAddress("Cara", "cara@example.com"), new EmailAddress("Bob", "bob@example.com")],
        Subject = "Plans",
        Date = Samples.Now,
    };

    [Fact]
    public void Reply_goes_to_sender_only()
    {
        var draft = ReplyBuilder.Create(ComposeMode.Reply, Original(), null, Me, CultureInfo.InvariantCulture);
        Assert.Equal("jane@example.com", Assert.Single(draft.To).Address);
        Assert.Empty(draft.Cc);
        Assert.Equal("Re: Plans", draft.Subject);
        Assert.Contains("<blockquote>", draft.QuotedHtml);
    }

    [Fact]
    public void Reply_all_excludes_me_and_duplicates()
    {
        var draft = ReplyBuilder.Create(ComposeMode.ReplyAll, Original(), null, Me, CultureInfo.InvariantCulture);
        Assert.Equal(["jane@example.com", "bob@example.com"], draft.To.Select(a => a.Address));
        Assert.Equal(["cara@example.com"], draft.Cc.Select(a => a.Address));
    }

    [Fact]
    public void Forward_has_no_recipients_and_keeps_attachments()
    {
        var body = new MessageBody
        {
            MessageId = Guid.NewGuid(),
            HtmlBody = "<p>Hi <script>alert(1)</script></p>",
            Attachments = [new AttachmentInfo("a.pdf", "application/pdf", 10)],
        };
        var draft = ReplyBuilder.Create(ComposeMode.Forward, Original(), body, Me, CultureInfo.InvariantCulture);
        Assert.Empty(draft.To);
        Assert.Equal("Fwd: Plans", draft.Subject);
        Assert.Single(draft.Attachments);
        Assert.Contains("Forwarded message", draft.QuotedHtml);
        Assert.DoesNotContain("<script", draft.QuotedHtml);
    }

    [Fact]
    public void Plain_text_reply_quotes_each_line()
    {
        var body = new MessageBody { MessageId = Guid.NewGuid(), TextBody = "line one\nline two" };
        var draft = ReplyBuilder.Create(ComposeMode.Reply, Original(), body, Me, CultureInfo.InvariantCulture);
        Assert.Contains("> line one\n> line two", draft.QuotedText);
    }
}
