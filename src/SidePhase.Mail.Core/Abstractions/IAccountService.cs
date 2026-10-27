using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Abstractions;

public enum DiscoverySource
{
    KnownProvider,
    ProviderAutoconfig,
    WellKnownAutoconfig,
    ThunderbirdDatabase,
    Guess,
    None,
}

/// <summary>What account setup found out about an email address.</summary>
public sealed record AccountDiscoveryResult
{
    public required string EmailAddress { get; init; }
    public string? ProviderName { get; init; }
    public MailProviderKind Provider { get; init; } = MailProviderKind.Imap;
    public AccountConnectionSettings? Settings { get; init; }
    public DiscoverySource Source { get; init; } = DiscoverySource.None;

    /// <summary>Help shown next to the password box, e.g. "Use an app password".</summary>
    public string? PasswordHint { get; init; }

    /// <summary>True when the settings are only a guess and must be confirmed by a successful connection test.</summary>
    public bool IsGuess => Source == DiscoverySource.Guess;

    public bool RequiresOAuth => Settings?.AuthMethod is AuthMethod.OAuthGoogle or AuthMethod.OAuthMicrosoft;
}

public sealed record AccountSetupRequest
{
    public required string EmailAddress { get; init; }
    public required string DisplayName { get; init; }
    public required MailProviderKind Provider { get; init; }
    public required AccountConnectionSettings Settings { get; init; }

    /// <summary>The password (password accounts only). Never persisted outside the secret store.</summary>
    public string? Password { get; init; }

    /// <summary>Tokens obtained from <see cref="IAccountService.SignInAsync"/> (OAuth accounts only).</summary>
    public OAuthTokens? Tokens { get; init; }
}

public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? Email);

public sealed record ConnectionTestResult(bool ImapOk, bool SmtpOk, string? ImapError, string? SmtpError)
{
    public bool Succeeded => ImapOk && SmtpOk;
}

/// <summary>Adds, tests, re-authenticates and removes real mail accounts.</summary>
public interface IAccountService
{
    /// <summary>True when the OAuth client for this provider has been configured (see docs/ACCOUNT_PROVIDERS.md).</summary>
    bool IsOAuthConfigured(AuthMethod method);

    Task<AccountDiscoveryResult> DiscoverAsync(string emailAddress, CancellationToken cancellationToken = default);

    /// <summary>Signs in through the provider's page in the system browser (OAuth accounts).</summary>
    Task<OAuthTokens> SignInAsync(AuthMethod method, string? emailHint, CancellationToken cancellationToken = default);

    /// <summary>Logs in to both IMAP and SMTP without saving anything.</summary>
    Task<ConnectionTestResult> TestAsync(AccountSetupRequest request, CancellationToken cancellationToken = default);

    /// <summary>Tests, then saves the account and its credentials. Throws if the test fails.</summary>
    Task<MailAccount> AddAsync(AccountSetupRequest request, CancellationToken cancellationToken = default);

    Task<AccountConnectionSettings?> GetConnectionSettingsAsync(Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the stored password or tokens after a successful test.</summary>
    Task ReauthenticateAsync(Guid accountId, string? password, OAuthTokens? tokens, CancellationToken cancellationToken = default);

    /// <summary>Removes the account, its local cache and its stored credentials. Never touches the server.</summary>
    Task RemoveAsync(Guid accountId, CancellationToken cancellationToken = default);
}
