using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Settings;
using SidePhase.Mail.Infrastructure.Live;
using SidePhase.Mail.Infrastructure.Mail;
using SidePhase.Mail.Infrastructure.OAuth;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Infrastructure.Accounts;

/// <summary>
/// Adds and removes real accounts. Settings are always tested before anything is saved; passwords and tokens go only
/// to the encrypted secret store; all traffic is between this computer and the user's own mail provider.
/// </summary>
public sealed class AccountService(
    MailCache cache,
    MailConnector connector,
    CredentialProvider credentials,
    AccountDiscovery discovery,
    OAuthClient oauth,
    OAuthConfiguration oauthConfiguration,
    LiveMailDataSource live,
    ISettingsService settings) : IAccountService
{
    private static readonly string[] Palette = ["#4F8EF7", "#2EC4B6", "#F7A23B", "#E5566A", "#9B7BFF", "#5BC46A", "#F25FA8", "#47B5E6"];

    public bool IsOAuthConfigured(AuthMethod method) => oauthConfiguration.IsConfigured(method);

    public Task<AccountDiscoveryResult> DiscoverAsync(string emailAddress, CancellationToken cancellationToken = default) =>
        discovery.DiscoverAsync(emailAddress, cancellationToken);

    public async Task<OAuthTokens> SignInAsync(AuthMethod method, string? emailHint, CancellationToken cancellationToken = default)
    {
        var config = await oauthConfiguration.GetAsync(method, cancellationToken).ConfigureAwait(false);
        return await oauth.SignInAsync(config, emailHint, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConnectionTestResult> TestAsync(AccountSetupRequest request, CancellationToken cancellationToken = default)
    {
        var credential = CredentialFor(request);
        var imapTask = TestPartAsync(async () =>
        {
            using var client = await connector.ConnectImapAsync(request.Settings.Imap, credential, cancellationToken).ConfigureAwait(false);
            await client.Inbox.StatusAsync(MailKit.StatusItems.Count, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        }, request.Settings.Imap.Host);
        var smtpTask = TestPartAsync(async () =>
        {
            using var client = await connector.ConnectSmtpAsync(request.Settings.Smtp, credential, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        }, request.Settings.Smtp.Host);

        var imap = await imapTask.ConfigureAwait(false);
        var smtp = await smtpTask.ConfigureAwait(false);
        return new ConnectionTestResult(imap is null, smtp is null, imap, smtp);
    }

    public async Task<MailAccount> AddAsync(AccountSetupRequest request, CancellationToken cancellationToken = default)
    {
        var email = request.EmailAddress.Trim();
        if (!AccountDiscovery.IsValidAddress(email)) throw new InvalidOperationException("That doesn't look like an email address.");

        await cache.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var existing = await cache.GetAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Any(a => a.Account.EmailAddress.Equals(email, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"{email} has already been added.");
        }

        var test = await TestAsync(request, cancellationToken).ConfigureAwait(false);
        if (!test.Succeeded) throw new InvalidOperationException(test.ImapError ?? test.SmtpError ?? "The connection test failed.");

        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? email : request.DisplayName.Trim(),
            EmailAddress = email,
            Provider = request.Provider,
            ColorHex = Palette[existing.Count % Palette.Length],
        };

        await SaveCredentialsAsync(account.Id, request, cancellationToken).ConfigureAwait(false);
        try
        {
            await cache.AddAccountAsync(new StoredAccount(account, request.Settings with { Username = UsernameFor(request) }), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await credentials.DeleteAsync(account.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        settings.Update(s =>
        {
            s.DataMode = DataMode.Live;
            s.HasCompletedWelcome = true;
        });
        await live.OnAccountsChangedAsync().ConfigureAwait(false);
        _ = live.SyncAsync(CancellationToken.None);
        return account;
    }

    public async Task<AccountConnectionSettings?> GetConnectionSettingsAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        (await cache.GetAccountsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(a => a.Account.Id == accountId)?.Settings;

    public async Task ReauthenticateAsync(Guid accountId, string? password, OAuthTokens? tokens, CancellationToken cancellationToken = default)
    {
        var stored = (await cache.GetAccountsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(a => a.Account.Id == accountId)
            ?? throw new InvalidOperationException("That account no longer exists.");
        var request = new AccountSetupRequest
        {
            EmailAddress = stored.Account.EmailAddress,
            DisplayName = stored.Account.DisplayName,
            Provider = stored.Account.Provider,
            Settings = stored.Settings,
            Password = password,
            Tokens = tokens,
        };

        var test = await TestAsync(request, cancellationToken).ConfigureAwait(false);
        if (!test.Succeeded) throw new InvalidOperationException(test.ImapError ?? test.SmtpError ?? "The connection test failed.");

        await SaveCredentialsAsync(accountId, request, cancellationToken).ConfigureAwait(false);
        await live.ResetConnectionAsync(accountId).ConfigureAwait(false);
        _ = live.SyncAsync(CancellationToken.None);
    }

    public async Task RemoveAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await credentials.DeleteAsync(accountId, cancellationToken).ConfigureAwait(false);
        var folderIds = (await cache.GetFoldersAsync(accountId, cancellationToken).ConfigureAwait(false)).Select(f => f.Id).ToHashSet();
        await cache.DeleteAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        settings.Update(s =>
        {
            s.FavouriteFolderIds.RemoveAll(folderIds.Contains);
            s.Signatures.Remove(accountId);
        });
        await live.OnAccountsChangedAsync(accountId).ConfigureAwait(false);
    }

    private async Task SaveCredentialsAsync(Guid accountId, AccountSetupRequest request, CancellationToken ct)
    {
        if (request.Settings.AuthMethod == AuthMethod.Password)
        {
            await credentials.SavePasswordAsync(accountId, request.Password ?? string.Empty, ct).ConfigureAwait(false);
        }
        else
        {
            await credentials.SaveTokensAsync(accountId, request.Tokens ?? throw new InvalidOperationException("Sign in first."), ct).ConfigureAwait(false);
        }
    }

    private static string UsernameFor(AccountSetupRequest request) =>
        request.Settings.AuthMethod != AuthMethod.Password && !string.IsNullOrEmpty(request.Tokens?.Email)
            ? request.Tokens.Email
            : request.Settings.Username;

    private static Accounts.MailCredential CredentialFor(AccountSetupRequest request)
    {
        var username = UsernameFor(request);
        if (request.Settings.AuthMethod == AuthMethod.Password)
        {
            if (string.IsNullOrEmpty(request.Password)) throw new InvalidOperationException("Enter the password for this account.");
            return new MailCredential(username, request.Password, null);
        }

        var tokens = request.Tokens ?? throw new InvalidOperationException("Sign in with your provider first.");
        return new MailCredential(username, null, tokens.AccessToken);
    }

    private static async Task<string?> TestPartAsync(Func<Task> test, string host)
    {
        try
        {
            await test().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return MailErrorTranslator.Describe(ex, host).Message;
        }
    }
}
