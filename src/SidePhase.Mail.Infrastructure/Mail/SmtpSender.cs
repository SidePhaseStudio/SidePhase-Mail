using MailKit;
using MailKit.Net.Smtp;
using MimeKit;

namespace SidePhase.Mail.Infrastructure.Mail;

/// <summary>Sends a message over an authenticated SMTP connection and reports upload progress.</summary>
public static class SmtpSender
{
    /// <summary>
    /// Sends <paramref name="message"/>. Returns only after the server has accepted it (250 after DATA); any rejection
    /// throws, so callers never report success for a message the server refused.
    /// </summary>
    public static async Task<string> SendAsync(SmtpClient client, MimeMessage message, IProgress<double>? progress, CancellationToken ct)
    {
        if (!message.To.Mailboxes.Any() && !message.Cc.Mailboxes.Any() && !message.Bcc.Mailboxes.Any())
        {
            throw new InvalidOperationException("Add at least one recipient before sending.");
        }

        var response = await client.SendAsync(message, ct, progress is null ? null : new TransferProgress(progress)).ConfigureAwait(false);
        progress?.Report(1.0);
        return response;
    }

    private sealed class TransferProgress(IProgress<double> progress) : ITransferProgress
    {
        public void Report(long bytesTransferred, long totalSize) =>
            progress.Report(totalSize <= 0 ? 0 : Math.Clamp((double)bytesTransferred / totalSize, 0, 0.99));

        public void Report(long bytesTransferred)
        {
        }
    }
}
