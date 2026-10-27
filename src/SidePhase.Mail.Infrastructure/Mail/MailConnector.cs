using System.Net.Security;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Infrastructure.Accounts;

namespace SidePhase.Mail.Infrastructure.Mail;

/// <summary>
/// Opens authenticated IMAP and SMTP connections. Every connection is encrypted: SSL/TLS from the start, or STARTTLS
/// that must succeed (MailKit fails rather than continuing in plain text). Certificates are validated by Windows.
/// </summary>
public sealed class MailConnector
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// For automated tests against local servers with self-signed certificates only. Never set by the app.
    /// </summary>
    public RemoteCertificateValidationCallback? CertificateValidationOverride { get; set; }

    public async Task<ImapClient> ConnectImapAsync(ServerEndpoint endpoint, MailCredential credential, CancellationToken ct = default)
    {
        var client = new ImapClient { Timeout = (int)Timeout.TotalMilliseconds };
        if (CertificateValidationOverride is not null) client.ServerCertificateValidationCallback = CertificateValidationOverride;
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, ToOptions(endpoint.Security), ct).ConfigureAwait(false);
            if (credential.AccessToken is { } token)
            {
                await client.AuthenticateAsync(new SaslMechanismOAuth2(credential.Username, token), ct).ConfigureAwait(false);
            }
            else
            {
                client.AuthenticationMechanisms.Remove("XOAUTH2");
                client.AuthenticationMechanisms.Remove("OAUTHBEARER");
                await client.AuthenticateAsync(credential.Username, credential.Password ?? string.Empty, ct).ConfigureAwait(false);
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<SmtpClient> ConnectSmtpAsync(ServerEndpoint endpoint, MailCredential credential, CancellationToken ct = default)
    {
        var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        if (CertificateValidationOverride is not null) client.ServerCertificateValidationCallback = CertificateValidationOverride;
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, ToOptions(endpoint.Security), ct).ConfigureAwait(false);
            if (credential.AccessToken is { } token)
            {
                await client.AuthenticateAsync(new SaslMechanismOAuth2(credential.Username, token), ct).ConfigureAwait(false);
            }
            else
            {
                client.AuthenticationMechanisms.Remove("XOAUTH2");
                client.AuthenticationMechanisms.Remove("OAUTHBEARER");
                await client.AuthenticateAsync(credential.Username, credential.Password ?? string.Empty, ct).ConfigureAwait(false);
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static SecureSocketOptions ToOptions(ConnectionSecurity security) => security switch
    {
        ConnectionSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        ConnectionSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => throw new ArgumentOutOfRangeException(nameof(security), "Unencrypted connections are not supported."),
    };
}
