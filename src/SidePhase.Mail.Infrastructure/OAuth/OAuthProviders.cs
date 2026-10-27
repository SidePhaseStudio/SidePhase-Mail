using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Infrastructure.OAuth;

/// <summary>Endpoints, scopes and client registration for one OAuth provider.</summary>
public sealed record OAuthProviderConfig
{
    public required string Name { get; init; }
    public required AuthMethod Method { get; init; }
    public required Uri AuthorizationEndpoint { get; init; }
    public required Uri TokenEndpoint { get; init; }
    public required string ClientId { get; init; }

    /// <summary>
    /// Google "Desktop app" clients are issued a client secret that Google documents as not confidential; it is still
    /// configurable and stored encrypted rather than compiled in. Microsoft public clients have none.
    /// </summary>
    public string? ClientSecret { get; init; }

    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>Loopback host used in the redirect URI ("127.0.0.1" for Google, "localhost" for Microsoft).</summary>
    public required string RedirectHost { get; init; }

    public IReadOnlyDictionary<string, string> ExtraAuthorizationParameters { get; init; } = new Dictionary<string, string>();
}

public static class OAuthProviders
{
    /// <summary>
    /// Gmail over IMAP/SMTP needs the restricted <c>https://mail.google.com/</c> scope. Public distribution therefore
    /// requires Google's OAuth verification and a security assessment; until then only test users can sign in.
    /// </summary>
    public static OAuthProviderConfig Google(string clientId, string? clientSecret) => new()
    {
        Name = "Google",
        Method = AuthMethod.OAuthGoogle,
        AuthorizationEndpoint = new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        TokenEndpoint = new Uri("https://oauth2.googleapis.com/token"),
        ClientId = clientId,
        ClientSecret = clientSecret,
        Scopes = ["https://mail.google.com/", "openid", "email"],
        RedirectHost = "127.0.0.1",
        ExtraAuthorizationParameters = new Dictionary<string, string>
        {
            ["access_type"] = "offline", // ask for a refresh token
            ["prompt"] = "consent",
        },
    };

    /// <summary>
    /// Microsoft identity platform (personal accounts and work/school accounts on the "common" endpoint) with the
    /// Outlook IMAP and SMTP delegated permissions.
    /// </summary>
    public static OAuthProviderConfig Microsoft(string clientId, string tenant = "common") => new()
    {
        Name = "Microsoft",
        Method = AuthMethod.OAuthMicrosoft,
        AuthorizationEndpoint = new Uri($"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize"),
        TokenEndpoint = new Uri($"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token"),
        ClientId = clientId,
        Scopes =
        [
            "https://outlook.office.com/IMAP.AccessAsUser.All",
            "https://outlook.office.com/SMTP.Send",
            "offline_access",
            "openid",
            "email",
        ],
        RedirectHost = "localhost",
        ExtraAuthorizationParameters = new Dictionary<string, string>
        {
            ["prompt"] = "select_account",
        },
    };
}
