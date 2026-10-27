using System.Net;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Infrastructure.Accounts;

namespace SidePhase.Mail.Tests.Mail;

public class AccountDiscoveryTests
{
    private const string Sample = """
        <?xml version="1.0"?>
        <clientConfig version="1.1">
          <emailProvider id="example.org">
            <displayName>Example Mail</displayName>
            <incomingServer type="pop3"><hostname>pop.example.org</hostname><port>995</port><socketType>SSL</socketType></incomingServer>
            <incomingServer type="imap"><hostname>plain.example.org</hostname><port>143</port><socketType>plain</socketType><username>%EMAILADDRESS%</username><authentication>password-cleartext</authentication></incomingServer>
            <incomingServer type="imap"><hostname>imap.example.org</hostname><port>143</port><socketType>STARTTLS</socketType><username>%EMAILLOCALPART%</username><authentication>password-cleartext</authentication></incomingServer>
            <incomingServer type="imap"><hostname>imaps.example.org</hostname><port>993</port><socketType>SSL</socketType><username>%EMAILLOCALPART%</username><authentication>password-cleartext</authentication></incomingServer>
            <outgoingServer type="smtp"><hostname>smtp.example.org</hostname><port>587</port><socketType>STARTTLS</socketType><authentication>password-cleartext</authentication></outgoingServer>
          </emailProvider>
        </clientConfig>
        """;

    [Fact]
    public void Autoconfig_prefers_ssl_and_never_returns_plain_text_servers()
    {
        var parsed = AccountDiscovery.ParseAutoconfig(Sample, "jo@example.org");
        Assert.NotNull(parsed);
        var settings = parsed.Value.Settings;
        Assert.Equal("Example Mail", parsed.Value.Name);
        Assert.Equal(new ServerEndpoint("imaps.example.org", 993, ConnectionSecurity.SslOnConnect), settings.Imap);
        Assert.Equal(new ServerEndpoint("smtp.example.org", 587, ConnectionSecurity.StartTls), settings.Smtp);
        Assert.Equal("jo", settings.Username);
        Assert.Equal(AuthMethod.Password, settings.AuthMethod);
    }

    [Fact]
    public void Autoconfig_with_only_plain_servers_is_rejected()
    {
        var xml = Sample.Replace("SSL", "plain").Replace("STARTTLS", "plain");
        Assert.Null(AccountDiscovery.ParseAutoconfig(xml, "jo@example.org"));
        Assert.Null(AccountDiscovery.ParseAutoconfig("<not xml", "jo@example.org"));
    }

    [Theory]
    [InlineData("someone@gmail.com", MailProviderKind.Gmail, AuthMethod.OAuthGoogle, "imap.gmail.com")]
    [InlineData("someone@hotmail.co.uk", MailProviderKind.Microsoft, AuthMethod.OAuthMicrosoft, "outlook.office365.com")]
    [InlineData("someone@icloud.com", MailProviderKind.Imap, AuthMethod.Password, "imap.mail.me.com")]
    public async Task Known_providers_need_no_network(string email, MailProviderKind kind, AuthMethod auth, string imapHost)
    {
        var handler = new FakeTokenHandler(HttpStatusCode.InternalServerError, "{}");
        var result = await new AccountDiscovery(new HttpClient(handler)).DiscoverAsync(email);
        Assert.Equal(DiscoverySource.KnownProvider, result.Source);
        Assert.Equal(kind, result.Provider);
        Assert.Equal(auth, result.Settings!.AuthMethod);
        Assert.Equal(imapHost, result.Settings.Imap.Host);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Unknown_domains_fall_back_to_an_encrypted_guess_that_must_be_tested()
    {
        var handler = new FakeTokenHandler(HttpStatusCode.NotFound, "");
        var result = await new AccountDiscovery(new HttpClient(handler)).DiscoverAsync("me@unknown-domain.test");
        Assert.True(result.IsGuess);
        Assert.Equal("imap.unknown-domain.test", result.Settings!.Imap.Host);
        Assert.Equal(ConnectionSecurity.SslOnConnect, result.Settings.Imap.Security);
    }

    [Fact]
    public async Task Provider_autoconfig_is_fetched_over_https_only()
    {
        var handler = new FakeTokenHandler(HttpStatusCode.OK, Sample);
        var result = await new AccountDiscovery(new HttpClient(handler)).DiscoverAsync("jo@example.org");
        Assert.Equal(DiscoverySource.ProviderAutoconfig, result.Source);
        Assert.Equal("imaps.example.org", result.Settings!.Imap.Host);
        var url = Assert.Single(handler.Requests);
        Assert.StartsWith("https://autoconfig.example.org/", url);
    }

    [Theory]
    [InlineData("a@b.com", true)]
    [InlineData("not an address", false)]
    [InlineData("missing-at.example.com", false)]
    [InlineData("a@localhost", false)]
    public void Validates_addresses(string email, bool valid) => Assert.Equal(valid, AccountDiscovery.IsValidAddress(email));
}
