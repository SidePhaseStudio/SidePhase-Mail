using System.Net.Mail;
using System.Xml.Linq;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Infrastructure.Accounts;

/// <summary>
/// Works out IMAP/SMTP settings for an address: built-in provider table, then Mozilla-style autoconfig over HTTPS
/// (the domain's own server, then the Thunderbird ISP database), then an unverified guess. Only encrypted settings are
/// ever returned, and nothing is trusted until a connection test succeeds.
/// </summary>
public sealed class AccountDiscovery(HttpClient http)
{
    private sealed record KnownProvider(
        string Name,
        string[] Domains,
        MailProviderKind Kind,
        ServerEndpoint Imap,
        ServerEndpoint Smtp,
        AuthMethod Auth,
        string? PasswordHint);

    private const string AppPasswordHint = "This provider needs an app password, not your normal password. Create one in your account's security settings.";

    private static readonly KnownProvider[] Known =
    [
        new("Gmail", ["gmail.com", "googlemail.com"], MailProviderKind.Gmail,
            new("imap.gmail.com", 993, ConnectionSecurity.SslOnConnect), new("smtp.gmail.com", 465, ConnectionSecurity.SslOnConnect),
            AuthMethod.OAuthGoogle, "Sign in with Google is recommended. If you use 2-Step Verification you can use an app password instead."),
        new("Outlook.com", ["outlook.com", "hotmail.com", "hotmail.co.uk", "hotmail.fr", "hotmail.de", "hotmail.it", "hotmail.es", "live.com", "live.co.uk", "live.fr", "live.de", "msn.com", "outlook.co.uk", "outlook.fr", "outlook.de"], MailProviderKind.Microsoft,
            new("outlook.office365.com", 993, ConnectionSecurity.SslOnConnect), new("smtp-mail.outlook.com", 587, ConnectionSecurity.StartTls),
            AuthMethod.OAuthMicrosoft, "Microsoft no longer accepts passwords for Outlook.com mail apps. Use Sign in with Microsoft."),
        new("Yahoo Mail", ["yahoo.com", "yahoo.co.uk", "yahoo.fr", "yahoo.de", "ymail.com", "rocketmail.com"], MailProviderKind.Imap,
            new("imap.mail.yahoo.com", 993, ConnectionSecurity.SslOnConnect), new("smtp.mail.yahoo.com", 465, ConnectionSecurity.SslOnConnect),
            AuthMethod.Password, AppPasswordHint),
        new("iCloud Mail", ["icloud.com", "me.com", "mac.com"], MailProviderKind.Imap,
            new("imap.mail.me.com", 993, ConnectionSecurity.SslOnConnect), new("smtp.mail.me.com", 587, ConnectionSecurity.StartTls),
            AuthMethod.Password, "iCloud needs an app-specific password (appleid.apple.com → Sign-In and Security)."),
        new("AOL Mail", ["aol.com", "aim.com"], MailProviderKind.Imap,
            new("imap.aol.com", 993, ConnectionSecurity.SslOnConnect), new("smtp.aol.com", 465, ConnectionSecurity.SslOnConnect),
            AuthMethod.Password, AppPasswordHint),
        new("Fastmail", ["fastmail.com", "fastmail.fm"], MailProviderKind.Imap,
            new("imap.fastmail.com", 993, ConnectionSecurity.SslOnConnect), new("smtp.fastmail.com", 465, ConnectionSecurity.SslOnConnect),
            AuthMethod.Password, "Fastmail needs an app password (Settings → Privacy & Security → Integrations)."),
        new("Zoho Mail", ["zoho.com", "zohomail.com"], MailProviderKind.Imap,
            new("imap.zoho.com", 993, ConnectionSecurity.SslOnConnect), new("smtp.zoho.com", 465, ConnectionSecurity.SslOnConnect),
            AuthMethod.Password, "If you use two-factor authentication, create an app-specific password in Zoho."),
        new("Zoho Mail (EU)", ["zoho.eu", "zohomail.eu"], MailProviderKind.Imap,
            new("imap.zoho.eu", 993, ConnectionSecurity.SslOnConnect), new("smtp.zoho.eu", 465, ConnectionSecurity.SslOnConnect),
            AuthMethod.Password, "If you use two-factor authentication, create an app-specific password in Zoho."),
        new("GMX", ["gmx.com", "gmx.co.uk", "gmx.us"], MailProviderKind.Imap,
            new("imap.gmx.com", 993, ConnectionSecurity.SslOnConnect), new("mail.gmx.com", 587, ConnectionSecurity.StartTls),
            AuthMethod.Password, "Enable IMAP access in GMX settings first."),
        new("GMX", ["gmx.net", "gmx.de", "gmx.at", "gmx.ch"], MailProviderKind.Imap,
            new("imap.gmx.net", 993, ConnectionSecurity.SslOnConnect), new("mail.gmx.net", 587, ConnectionSecurity.StartTls),
            AuthMethod.Password, "Enable IMAP access in GMX settings first."),
    ];

    /// <summary>Settings for Google Workspace (Gmail on your own domain), used when the user picked Google.</summary>
    public static AccountConnectionSettings GoogleWorkspace(string email) => new()
    {
        Imap = new("imap.gmail.com", 993, ConnectionSecurity.SslOnConnect),
        Smtp = new("smtp.gmail.com", 465, ConnectionSecurity.SslOnConnect),
        Username = email,
        AuthMethod = AuthMethod.OAuthGoogle,
    };

    /// <summary>
    /// Applies the provider the user chose on the welcome screen ("Google", "Microsoft") to what discovery found. An
    /// address on its own domain (Google Workspace, Microsoft 365) isn't recognisable from the address alone, so
    /// choosing Google or Microsoft means "sign in with that provider" whatever the domain. Choosing "Other" keeps
    /// discovery's result.
    /// </summary>
    public static AccountDiscoveryResult ForChoice(ProviderChoice choice, AccountDiscoveryResult discovered) => choice switch
    {
        ProviderChoice.Google when discovered.Provider != MailProviderKind.Gmail => discovered with
        {
            ProviderName = "Google Workspace",
            Provider = MailProviderKind.Gmail,
            Source = DiscoverySource.KnownProvider,
            Settings = GoogleWorkspace(discovered.EmailAddress),
            PasswordHint = "Sign in with Google is recommended. If your administrator allows it, an app password also works.",
        },
        ProviderChoice.Microsoft when discovered.Provider != MailProviderKind.Microsoft => discovered with
        {
            ProviderName = "Microsoft 365",
            Provider = MailProviderKind.Microsoft,
            Source = DiscoverySource.KnownProvider,
            Settings = Microsoft365(discovered.EmailAddress),
            PasswordHint = null,
        },
        _ => discovered,
    };

    /// <summary>Settings for Microsoft 365 work or school accounts (any domain), used when the user picks that option.</summary>
    public static AccountConnectionSettings Microsoft365(string email) => new()
    {
        Imap = new("outlook.office365.com", 993, ConnectionSecurity.SslOnConnect),
        Smtp = new("smtp.office365.com", 587, ConnectionSecurity.StartTls),
        Username = email,
        AuthMethod = AuthMethod.OAuthMicrosoft,
    };

    public static bool IsValidAddress(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Contains(' ')) return false;
        try
        {
            var parsed = new MailAddress(email.Trim());
            return parsed.Address.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase) && parsed.Host.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async Task<AccountDiscoveryResult> DiscoverAsync(string email, CancellationToken ct = default)
    {
        email = email.Trim();
        if (!IsValidAddress(email)) throw new ArgumentException("That doesn't look like an email address.", nameof(email));
        var domain = email[(email.LastIndexOf('@') + 1)..].ToLowerInvariant();

        if (FindKnown(domain) is { } known)
        {
            return new AccountDiscoveryResult
            {
                EmailAddress = email,
                ProviderName = known.Name,
                Provider = known.Kind,
                Source = DiscoverySource.KnownProvider,
                PasswordHint = known.PasswordHint,
                Settings = new AccountConnectionSettings { Imap = known.Imap, Smtp = known.Smtp, Username = email, AuthMethod = known.Auth },
            };
        }

        var sources = new (DiscoverySource Source, string Url)[]
        {
            (DiscoverySource.ProviderAutoconfig, $"https://autoconfig.{domain}/mail/config-v1.1.xml?emailaddress={Uri.EscapeDataString(email)}"),
            (DiscoverySource.WellKnownAutoconfig, $"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml?emailaddress={Uri.EscapeDataString(email)}"),
            // The public ISP database only receives the domain, never the full address.
            (DiscoverySource.ThunderbirdDatabase, $"https://autoconfig.thunderbird.net/v1.1/{Uri.EscapeDataString(domain)}"),
        };

        foreach (var (source, url) in sources)
        {
            var xml = await TryFetchAsync(url, ct).ConfigureAwait(false);
            if (xml is null) continue;
            if (ParseAutoconfig(xml, email) is { } parsed)
            {
                return new AccountDiscoveryResult
                {
                    EmailAddress = email,
                    ProviderName = parsed.Name,
                    Provider = parsed.Settings.AuthMethod switch
                    {
                        AuthMethod.OAuthGoogle => MailProviderKind.Gmail,
                        AuthMethod.OAuthMicrosoft => MailProviderKind.Microsoft,
                        _ => MailProviderKind.Imap,
                    },
                    Source = source,
                    Settings = parsed.Settings,
                };
            }
        }

        return new AccountDiscoveryResult
        {
            EmailAddress = email,
            Source = DiscoverySource.Guess,
            Settings = new AccountConnectionSettings
            {
                Imap = new($"imap.{domain}", 993, ConnectionSecurity.SslOnConnect),
                Smtp = new($"smtp.{domain}", 465, ConnectionSecurity.SslOnConnect),
                Username = email,
                AuthMethod = AuthMethod.Password,
            },
        };
    }

    private static KnownProvider? FindKnown(string domain) =>
        Known.FirstOrDefault(p => p.Domains.Contains(domain, StringComparer.OrdinalIgnoreCase));

    private async Task<string?> TryFetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await http.GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) return null;
            var content = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return content.Length > 512 * 1024 ? null : content;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }

    /// <summary>Parses a Mozilla autoconfig document, keeping only encrypted IMAP and SMTP servers.</summary>
    internal static (string? Name, AccountConnectionSettings Settings)? ParseAutoconfig(string xml, string email)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var provider = doc.Root?.Element("emailProvider");
        if (provider is null) return null;

        var imap = provider.Elements("incomingServer").Where(e => (string?)e.Attribute("type") == "imap").Select(ParseServer).Where(s => s is not null).OrderBy(s => s!.Value.Rank).FirstOrDefault();
        var smtp = provider.Elements("outgoingServer").Where(e => (string?)e.Attribute("type") == "smtp").Select(ParseServer).Where(s => s is not null).OrderBy(s => s!.Value.Rank).FirstOrDefault();
        if (imap is null || smtp is null) return null;

        var local = email[..email.LastIndexOf('@')];
        var domain = email[(email.LastIndexOf('@') + 1)..];
        var username = (imap.Value.Username ?? "%EMAILADDRESS%")
            .Replace("%EMAILADDRESS%", email, StringComparison.Ordinal)
            .Replace("%EMAILLOCALPART%", local, StringComparison.Ordinal)
            .Replace("%EMAILDOMAIN%", domain, StringComparison.Ordinal);

        var auth = imap.Value.Auth;
        return ((string?)provider.Element("displayName"), new AccountConnectionSettings
        {
            Imap = imap.Value.Endpoint,
            Smtp = smtp.Value.Endpoint,
            Username = username,
            AuthMethod = auth,
        });

        static (ServerEndpoint Endpoint, string? Username, AuthMethod Auth, int Rank)? ParseServer(XElement e)
        {
            var host = ((string?)e.Element("hostname"))?.Trim();
            if (string.IsNullOrEmpty(host) || !int.TryParse((string?)e.Element("port"), out var port) || port is <= 0 or > 65535) return null;

            ConnectionSecurity security;
            int rank;
            switch (((string?)e.Element("socketType"))?.Trim().ToUpperInvariant())
            {
                case "SSL":
                    security = ConnectionSecurity.SslOnConnect;
                    rank = 0;
                    break;
                case "STARTTLS":
                    security = ConnectionSecurity.StartTls;
                    rank = 1;
                    break;
                default:
                    return null; // never offer unencrypted settings
            }

            var methods = e.Elements("authentication").Select(a => a.Value.Trim()).ToList();
            var auth = AuthMethod.Password;
            if (methods.Contains("OAuth2"))
            {
                if (host.EndsWith("gmail.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith("googlemail.com", StringComparison.OrdinalIgnoreCase)) auth = AuthMethod.OAuthGoogle;
                else if (host.EndsWith("office365.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith("outlook.com", StringComparison.OrdinalIgnoreCase)) auth = AuthMethod.OAuthMicrosoft;
            }

            if (auth == AuthMethod.Password && methods.Count > 0 && !methods.Any(m => m is "password-cleartext" or "password-encrypted" or "plain" or "secure"))
            {
                rank += 10; // only offers mechanisms we don't support (e.g. NTLM, GSSAPI)
            }

            return (new ServerEndpoint(host, port, security), (string?)e.Element("username"), auth, rank);
        }
    }
}

/// <summary>The provider the user picked when adding an account.</summary>
public enum ProviderChoice
{
    Other,
    Google,
    Microsoft,
}
