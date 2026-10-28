using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Data.Sqlite;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Scheduling;
using SidePhase.Mail.Infrastructure.Mail;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Tests.Mail;

public sealed class OutboxStorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-outbox-" + Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));

    public OutboxStorageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private SqliteOutboxStore Store() => new(new MailCache(new MailDatabase(Path.Combine(_dir, "o.db"))));

    [Fact]
    public async Task Sqlite_store_persists_state_attempts_and_rejects_stale_transitions()
    {
        var transport = new ScriptedTransport();
        transport.Outcomes.Enqueue(SendOutcome.Uncertain);
        var outbox = new OutboxService(Store(), transport, _clock);
        var draft = new DraftMessage
        {
            AccountId = Guid.NewGuid(),
            To = [new EmailAddress("Bob", "bob@example.test")],
            Subject = "Persisted",
            Attachments = [new DraftAttachment("a.txt", 3, null, "text/plain")],
        };
        var item = await outbox.QueueAsync(draft, _clock.Now.AddMinutes(5), OutboxKind.Scheduled, isDemo: false);

        // A fresh store (app restart) sees the same item.
        var reopened = Store();
        var loaded = (await reopened.GetAsync(item.Id))!;
        Assert.Equal("Persisted", loaded.Draft.Subject);
        Assert.Equal("bob@example.test", loaded.Draft.To[0].Address);
        Assert.Equal(item.SendAt.ToUnixTimeMilliseconds(), loaded.SendAt.ToUnixTimeMilliseconds());
        Assert.Equal(OutboxState.Queued, loaded.State);

        Assert.False(await reopened.TryTransitionAsync(item.Id, OutboxState.Sending, loaded with { State = OutboxState.Accepted }));

        _clock.Now += TimeSpan.FromMinutes(5);
        await new OutboxService(reopened, transport, _clock).ProcessDueAsync();
        var after = (await Store().GetAsync(item.Id))!;
        Assert.Equal(OutboxState.Uncertain, after.State);
        Assert.Equal(1, after.Attempts);
        Assert.Equal(SendOutcome.Uncertain, Assert.Single(await Store().GetAttemptsAsync(item.Id)).Outcome);
    }

    [Fact]
    public async Task Attachments_are_kept_until_the_item_is_pruned()
    {
        var paths = new AppDataPaths(_dir);
        var store = new FileOutboxAttachmentStore(paths);
        var source = Path.Combine(_dir, "report.pdf");
        await File.WriteAllTextAsync(source, "pdf");
        var draft = new DraftMessage { Attachments = [new DraftAttachment("report.pdf", 3, source)] };

        var id = Guid.NewGuid();
        var kept = await store.KeepAsync(id, draft);
        File.Delete(source); // the user deletes the original before the scheduled time
        var copy = kept.Attachments[0].SourcePath!;
        Assert.NotEqual(source, copy);
        Assert.Equal("pdf", await File.ReadAllTextAsync(copy));

        // Editing the queued message keeps using the same copy.
        Assert.Equal(copy, (await store.KeepAsync(id, kept)).Attachments[0].SourcePath);

        await store.ReleaseAsync(id);
        Assert.False(File.Exists(copy));

        var missing = new DraftMessage { Attachments = [new DraftAttachment("gone.txt", 1, source)] };
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.KeepAsync(Guid.NewGuid(), missing));
    }

    [Theory]
    [InlineData(false, SendOutcome.NotConnected)]
    [InlineData(true, SendOutcome.Uncertain)]
    public void Broken_connections_are_not_connected_before_data_and_uncertain_after(bool dataStarted, SendOutcome expected)
    {
        Assert.Equal(expected, SmtpSender.Classify(new IOException("reset", new SocketException()), dataStarted));
        Assert.Equal(expected, SmtpSender.Classify(new SocketException((int)SocketError.ConnectionReset), dataStarted));
    }

    [Fact]
    public void Server_replies_and_sign_in_problems_are_definite()
    {
        var mailbox = new MimeKit.MailboxAddress("", "x@example.test");
        Assert.Equal(SendOutcome.Rejected, SmtpSender.Classify(new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, mailbox, "550"), dataStarted: true));
        Assert.Equal(SendOutcome.TemporaryFailure, SmtpSender.Classify(new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.MailboxBusy, mailbox, "450"), dataStarted: true));
        Assert.Equal(SendOutcome.Rejected, SmtpSender.Classify(new AuthenticationException("bad"), dataStarted: false));
        Assert.Equal(SendOutcome.Rejected, SmtpSender.Classify(new FileNotFoundException("attachment"), dataStarted: false));
    }
}
