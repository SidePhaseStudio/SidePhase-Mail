using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using SidePhase.Mail.Infrastructure.Mail;
using SidePhase.Mail.Infrastructure.OAuth;

namespace SidePhase.Mail.Tests.Mail;

public class ErrorTranslatorTests
{
    [Fact]
    public void Bad_credentials_require_sign_in()
    {
        var error = MailErrorTranslator.Describe(new AuthenticationException("NO [AUTHENTICATIONFAILED] Invalid credentials secret-password"));
        Assert.True(error.RequiresSignIn);
        Assert.DoesNotContain("secret-password", error.Message);
    }

    [Fact]
    public void Expired_oauth_requires_sign_in()
    {
        var error = MailErrorTranslator.Describe(new OAuthException("Your Google sign-in has expired."));
        Assert.True(error.RequiresSignIn);
        Assert.Contains("expired", error.Message);
    }

    [Fact]
    public void Network_errors_are_friendly()
    {
        var error = MailErrorTranslator.Describe(new SocketException((int)SocketError.HostNotFound), "imap.example.com");
        Assert.False(error.RequiresSignIn);
        Assert.Contains("imap.example.com", error.Message);
    }

    [Fact]
    public void Smtp_recipient_rejection_names_the_problem()
    {
        var ex = new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, new MimeKit.MailboxAddress("", "nobody@example.com"), "550 no such user");
        var error = MailErrorTranslator.Describe(ex);
        Assert.Contains("nobody@example.com", error.Message);
    }

    [Fact]
    public void Unknown_errors_still_produce_a_message()
    {
        var error = MailErrorTranslator.Describe(new InvalidOperationException("boom"));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }
}
