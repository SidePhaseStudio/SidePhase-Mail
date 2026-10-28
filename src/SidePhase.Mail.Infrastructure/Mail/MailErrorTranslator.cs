using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using SidePhase.Mail.Infrastructure.OAuth;

namespace SidePhase.Mail.Infrastructure.Mail;

public sealed record FriendlyError(string Message, bool RequiresSignIn);

/// <summary>Turns technical failures into short, actionable messages. Never includes secrets or message content.</summary>
public static class MailErrorTranslator
{
    public static FriendlyError Describe(Exception exception, string? server = null)
    {
        var where = string.IsNullOrEmpty(server) ? "the mail server" : server;
        return exception switch
        {
            OAuthException { IsTransient: true } oauth => new(oauth.Message, false),
            OAuthException oauth => new(oauth.Message, true),
            Security.SecretStoreUnavailableException => new(
                "SidePhase Mail couldn't read this account's saved sign-in details (the file may be locked by another program). It will try again.",
                false),
            AuthenticationException => new(
                $"{Capitalise(where)} didn't accept the sign-in. Check the username and password (some providers need an app password), or sign in again.",
                true),
            SslHandshakeException => new(
                $"A secure connection to {where} couldn't be verified. Its security certificate isn't trusted or doesn't match the server name, so SidePhase Mail didn't connect.",
                false),
            SmtpCommandException smtp => new(DescribeSmtp(smtp), smtp.StatusCode is SmtpStatusCode.AuthenticationRequired or SmtpStatusCode.AuthenticationChallenge),
            NotSupportedException ns when ns.Message.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase) => new(
                $"{Capitalise(where)} doesn't offer an encrypted connection (STARTTLS), so SidePhase Mail won't connect. Try SSL/TLS on another port.",
                false),
            SocketException socket => new(DescribeSocket(socket, where), false),
            TimeoutException => new($"{Capitalise(where)} took too long to respond. Check your connection and try again.", false),
            OperationCanceledException => new("The operation was cancelled or timed out.", false),
            ImapCommandException imap => new($"{Capitalise(where)} refused the request: {imap.ResponseText}", false),
            ImapProtocolException or SmtpProtocolException => new($"The connection to {where} was interrupted. SidePhase Mail will try again.", false),
            FolderRenumberedException renumbered => new(renumbered.Message, false),
            ServiceNotConnectedException or ServiceNotAuthenticatedException or IOException => new(
                $"The connection to {where} was lost. Check your internet connection; SidePhase Mail will try again.",
                false),
            FolderNotFoundException => new("That folder no longer exists on the server. Refresh to update the folder list.", false),
            UnauthorizedAccessException => new("SidePhase Mail doesn't have permission to read or write that file.", false),
            _ => new($"Something went wrong talking to {where}: {exception.Message}", false),
        };
    }

    private static string DescribeSmtp(SmtpCommandException e) => e.ErrorCode switch
    {
        SmtpErrorCode.RecipientNotAccepted => $"The server rejected the recipient {e.Mailbox?.Address}. Check the address and try again.",
        SmtpErrorCode.SenderNotAccepted => $"The server won't send mail from {e.Mailbox?.Address}. Check the account's email address.",
        SmtpErrorCode.MessageNotAccepted => $"The server didn't accept the message ({(int)e.StatusCode}): {e.Message}",
        _ => $"The outgoing mail server refused the message ({(int)e.StatusCode}): {e.Message}",
    };

    private static string DescribeSocket(SocketException e, string where) => e.SocketErrorCode switch
    {
        SocketError.HostNotFound or SocketError.NoData => $"Couldn't find {where}. Check the server name and your internet connection.",
        SocketError.ConnectionRefused => $"{Capitalise(where)} refused the connection. Check the port number.",
        SocketError.TimedOut => $"{Capitalise(where)} didn't respond. Check the server name, port and your connection.",
        SocketError.NetworkUnreachable or SocketError.NetworkDown => "You appear to be offline. SidePhase Mail will try again when you're connected.",
        _ => $"Couldn't connect to {where} ({e.SocketErrorCode}).",
    };

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
