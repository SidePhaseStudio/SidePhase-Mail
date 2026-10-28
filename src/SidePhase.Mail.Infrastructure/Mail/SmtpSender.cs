using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using SidePhase.Mail.Core.Scheduling;
using SidePhase.Mail.Infrastructure.OAuth;

namespace SidePhase.Mail.Infrastructure.Mail;

/// <summary>Records how much of the message data reached the server, to judge whether a failure is ambiguous.</summary>
public sealed class SmtpSendTracker
{
    private long _bytesSent;

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>True once any message data (after the DATA/BDAT command) has been written to the server.</summary>
    public bool DataStarted => BytesSent > 0;

    internal void Record(long bytes) => Interlocked.Exchange(ref _bytesSent, Math.Max(bytes, BytesSent));
}

/// <summary>Sends a message over an authenticated SMTP connection and reports upload progress.</summary>
public static class SmtpSender
{
    /// <summary>
    /// Sends <paramref name="message"/>. Returns only after the server has accepted it (250 after DATA); any rejection
    /// throws, so callers never report success for a message the server refused.
    /// </summary>
    public static async Task<string> SendAsync(SmtpClient client, MimeMessage message, IProgress<double>? progress, CancellationToken ct, SmtpSendTracker? tracker = null)
    {
        if (!message.To.Mailboxes.Any() && !message.Cc.Mailboxes.Any() && !message.Bcc.Mailboxes.Any())
        {
            throw new InvalidOperationException("Add at least one recipient before sending.");
        }

        var response = await client.SendAsync(message, ct, new TransferProgress(progress, tracker)).ConfigureAwait(false);
        progress?.Report(1.0);
        return response;
    }

    /// <summary>
    /// Classifies a failed send. A server error reply means the message definitely wasn't accepted; a broken
    /// connection after message data started flowing means it might have been, so it must not be retried
    /// automatically.
    /// </summary>
    public static SendOutcome Classify(Exception exception, bool dataStarted) => exception switch
    {
        // The server answered: not accepted. 4xx is worth retrying later, 5xx needs the user.
        SmtpCommandException command => (int)command.StatusCode is >= 400 and < 500 ? SendOutcome.TemporaryFailure : SendOutcome.Rejected,
        AuthenticationException or SslHandshakeException or OAuthException => SendOutcome.Rejected,
        InvalidOperationException or IOException when !IsTransport(exception) => SendOutcome.Rejected, // e.g. missing attachment
        _ when dataStarted => SendOutcome.Uncertain,
        _ when IsTransport(exception) => SendOutcome.TemporaryFailure,
        _ => SendOutcome.Rejected,
    };

    private static bool IsTransport(Exception exception) =>
        exception is SocketException or ServiceNotConnectedException or SmtpProtocolException or TimeoutException or OperationCanceledException
            || (exception is IOException io && (io.InnerException is SocketException || io is not FileNotFoundException and not DirectoryNotFoundException));

    private sealed class TransferProgress(IProgress<double>? progress, SmtpSendTracker? tracker) : ITransferProgress
    {
        public void Report(long bytesTransferred, long totalSize)
        {
            tracker?.Record(bytesTransferred);
            progress?.Report(totalSize <= 0 ? 0 : Math.Clamp((double)bytesTransferred / totalSize, 0, 0.99));
        }

        public void Report(long bytesTransferred) => tracker?.Record(bytesTransferred);
    }
}
