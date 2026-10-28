using SidePhase.Mail.Core.Formatting;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Services;

namespace SidePhase.Mail.Tests;

public class LogRedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer ya29.a0AfH6SMBx-very-long-token", "Bearer [redacted]")]
    [InlineData("POST /token?code=4/0AY0e-g7abc&code_verifier=xyz123", "code=[redacted]")]
    [InlineData("refresh_token=1//0gAbCdEf failed", "refresh_token=[redacted]")]
    [InlineData("{\"access_token\":\"abc.def\",\"expires_in\":3600}", "\"access_token\":\"[redacted]\"")]
    [InlineData("password: hunter2", "password=[redacted]")]
    [InlineData("AUTH XOAUTH2 dXNlcj1hbGljZUBleGFtcGxlLmNvbQFhdXRo", "XOAUTH2 [redacted]")]
    public void Secrets_never_reach_the_logs(string input, string expectedFragment)
    {
        var redacted = LogRedactor.Redact(input);
        Assert.Contains(expectedFragment, redacted);
        Assert.DoesNotContain("hunter2", redacted);
        Assert.DoesNotContain("ya29", redacted);
        Assert.DoesNotContain("1//0gAbCdEf", redacted);
    }

    [Fact]
    public void Email_addresses_and_jwts_are_masked_but_hosts_are_kept()
    {
        var redacted = LogRedactor.Redact("Login failed for alice.smith@example.com on imap.example.com token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U");
        Assert.Contains("***@example.com", redacted);
        Assert.DoesNotContain("alice.smith", redacted);
        Assert.Contains("imap.example.com", redacted);
        Assert.DoesNotContain("eyJhbGciOi", redacted);
    }

    [Fact]
    public void Ordinary_text_is_untouched() =>
        Assert.Equal("Couldn't connect: The operation timed out.", LogRedactor.Redact("Couldn't connect: The operation timed out."));
}

public class AttachmentFinderTests
{
    private static MessageSummary M(string from, int daysAgo, FolderKind kind = FolderKind.Inbox, params AttachmentInfo[] attachments) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        FolderId = Guid.NewGuid(),
        FolderKind = kind,
        From = new EmailAddress(null, from),
        Subject = "x",
        Date = Samples.Now.AddDays(-daysAgo),
        HasAttachments = attachments.Length > 0,
        Attachments = attachments,
    };

    [Fact]
    public void Finds_attachments_by_name_type_size_sender_and_date_without_downloading()
    {
        var invoice = new AttachmentInfo("Invoice-0042.pdf", "application/pdf", 120_000);
        var photo = new AttachmentInfo("Beach.jpg", "image/jpeg", 4_000_000);
        var sheet = new AttachmentInfo("Budget.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 30_000);
        var messages = new[]
        {
            M("billing@shop.example", 1, attachments: invoice),
            M("mum@family.example", 40, attachments: [photo, sheet]),
            M("spam@bad.example", 2, FolderKind.Junk, new AttachmentInfo("Invoice.pdf.exe", "application/octet-stream", 1000)),
            M("nobody@example.test", 3),
        };

        Assert.Equal([invoice, photo, sheet], AttachmentFinder.Find(messages, new AttachmentQuery()).Select(h => h.Attachment)); // newest first, junk skipped
        Assert.Equal([invoice], AttachmentFinder.Find(messages, new AttachmentQuery { FileName = "invoice" }).Select(h => h.Attachment));
        Assert.Equal([photo], AttachmentFinder.Find(messages, new AttachmentQuery { Type = "image" }).Select(h => h.Attachment));
        Assert.Equal([sheet], AttachmentFinder.Find(messages, new AttachmentQuery { Type = "spreadsheet" }).Select(h => h.Attachment));
        Assert.Equal([photo], AttachmentFinder.Find(messages, new AttachmentQuery { MinimumSize = 1_000_000 }).Select(h => h.Attachment));
        Assert.Equal(2, AttachmentFinder.Find(messages, new AttachmentQuery { Sender = "family" }).Count);
        Assert.Equal([invoice], AttachmentFinder.Find(messages, new AttachmentQuery { After = Samples.Now.AddDays(-7) }).Select(h => h.Attachment));
    }
}
