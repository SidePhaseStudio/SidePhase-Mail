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

    /// <summary>Changes the name shown for the account and its colour. Connection settings and sign-in are unchanged.</summary>
    Task UpdateAppearanceAsync(Guid accountId, string displayName, string? colorHex, CancellationToken cancellationToken = default);

    /// <summary>Removes the account, its local cache and its stored credentials. Never touches the server.</summary>
    Task RemoveAsync(Guid accountId, CancellationToken cancellationToken = default);
}

/// <summary>How an account's connection is doing, from its most recent sync.</summary>
public sealed record AccountStatus(DateTimeOffset? LastSyncedAt, string? LastError, bool RequiresSignIn, DateTimeOffset? LastErrorAt)
{
    public static readonly AccountStatus Unknown = new(null, null, false, null);

    public bool HasProblem => LastError is not null && (LastSyncedAt is null || LastErrorAt > LastSyncedAt);
}

/// <summary>Server-side mailbox storage (from IMAP QUOTA), in bytes.</summary>
public sealed record AccountStorage(long UsedBytes, long LimitBytes)
{
    public double Fraction => LimitBytes <= 0 ? 0 : Math.Clamp((double)UsedBytes / LimitBytes, 0, 1);

    public string Describe() => $"{Formatting.FileSizeFormatter.Format(UsedBytes)} of {Formatting.FileSizeFormatter.Format(LimitBytes)} used ({Fraction:P0})";
}
