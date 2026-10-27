using MimeKit;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Infrastructure.Mail;

namespace SidePhase.Mail.Tests.Mail;

public class MimeTests
{
    private static readonly MailAccount Alice = new() { Id = Guid.NewGuid(), DisplayName = "Alice Example", EmailAddress = "alice@example.test", Provider = MailProviderKind.Imap };

    [Fact]
    public void Builds_html_and_text_alternatives_with_recipients()
    {
        var draft = new DraftMessage
        {
            AccountId = Alice.Id,
            To = [new EmailAddress("Bob", "bob@example.test")],
            Cc = [new EmailAddress(null, "carol@example.test")],
            Bcc = [new EmailAddress(null, "dave@example.test")],
            Subject = "Plans",
            TextBody = "Hello <Bob> & friends",
        };

        var message = MimeMessageBuilder.Build(draft, Alice, null, null);
        Assert.Equal("alice@example.test", ((MailboxAddress)message.From[0]).Address);
        Assert.Equal("Alice Example", message.From[0].Name);
        Assert.Equal("bob@example.test", message.To.Mailboxes.Single().Address);
        Assert.Equal("carol@example.test", message.Cc.Mailboxes.Single().Address);
        Assert.Equal("dave@example.test", message.Bcc.Mailboxes.Single().Address);
        Assert.EndsWith("@example.test", message.MessageId);
        Assert.Contains("Hello <Bob> & friends", message.TextBody);
        Assert.Contains("Hello &lt;Bob&gt; &amp; friends", message.HtmlBody); // escaped, not injected
    }

    [Fact]
    public void Plain_text_messages_have_no_html_part()
    {
        var message = MimeMessageBuilder.Build(new DraftMessage { To = [new EmailAddress(null, "b@x.test")], TextBody = "Hi", IsPlainText = true }, Alice, null, null);
        Assert.Null(message.HtmlBody);
        Assert.Equal("Hi", message.TextBody?.Trim());
    }

    [Fact]
    public void Replies_carry_threading_headers_and_quoted_text()
    {
        var draft = new DraftMessage { Mode = ComposeMode.Reply, To = [new EmailAddress(null, "b@x.test")], Subject = "Re: Plans", TextBody = "Sure", QuotedText = "> Original" };
        var message = MimeMessageBuilder.Build(draft, Alice, "<orig@x.test>", ["<root@x.test>"]);
        Assert.Equal("orig@x.test", message.InReplyTo);
        Assert.Equal(["root@x.test", "orig@x.test"], message.References.ToArray());
        Assert.Contains("> Original", message.TextBody);
    }

    [Fact]
    public void Attachments_round_trip_through_the_extractor()
    {
        var file = Path.Combine(Path.GetTempPath(), $"sp-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "attachment body");
        try
        {
            var draft = new DraftMessage
            {
                To = [new EmailAddress(null, "b@x.test")],
                TextBody = "See attached",
                Attachments = [new DraftAttachment("notes.txt", 15, file, "text/plain")],
            };
            var message = Reparse(MimeMessageBuilder.Build(draft, Alice, null, null));
            var body = MessageBodyExtractor.Extract(Guid.NewGuid(), message);
            var info = Assert.Single(body.Attachments);
            Assert.Equal("notes.txt", info.FileName);
            Assert.Equal(15, info.SizeBytes);
            Assert.Equal("attachment body", System.Text.Encoding.UTF8.GetString(MessageBodyExtractor.GetAttachmentContent(message, 0)));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Missing_attachment_file_fails_before_sending()
    {
        var draft = new DraftMessage { To = [new EmailAddress(null, "b@x.test")], Attachments = [new DraftAttachment("gone.pdf", 1, Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()))] };
        Assert.ThrowsAny<IOException>(() => MimeMessageBuilder.Build(draft, Alice, null, null));
    }

    [Fact]
    public void Inline_cid_images_become_data_uris()
    {
        var builder = new BodyBuilder();
        var image = builder.LinkedResources.Add("logo.png", [0x89, 0x50, 0x4E, 0x47, 1, 2, 3], ContentType.Parse("image/png"));
        image.ContentId = "logo@x";
        builder.HtmlBody = "<p><img src=\"cid:logo@x\"></p>";
        var message = new MimeMessage { Body = builder.ToMessageBody() };

        var body = MessageBodyExtractor.Extract(Guid.NewGuid(), Reparse(message));
        Assert.DoesNotContain("cid:", body.HtmlBody);
        Assert.Contains("data:image/png;base64,", body.HtmlBody);
    }

    [Fact]
    public void Html_only_messages_get_a_plain_text_fallback_and_spam_headers_are_read()
    {
        var message = new MimeMessage { Body = new TextPart("html") { Text = "<p>Hello <b>there</b></p>" } };
        message.Headers.Add("X-Spam-Status", "Yes, score=8.1 required=5.0 tests=BAYES_99");
        message.Headers.Add("X-Spam-Flag", "YES");
        var body = MessageBodyExtractor.Extract(Guid.NewGuid(), Reparse(message));
        Assert.Contains("Hello", body.TextBody);
        Assert.DoesNotContain("<b>", body.TextBody);
        var spam = MessageBodyExtractor.ReadSpamHeaders(message.Headers);
        Assert.True(spam.ServerFlaggedAsSpam);
        Assert.Equal(8.1, spam.Score);
    }

    private static MimeMessage Reparse(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        return MimeMessage.Load(stream);
    }
}
