using System.Collections.Concurrent;
using System.Text.Json;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Infrastructure.OAuth;
using SidePhase.Mail.Infrastructure.Security;

namespace SidePhase.Mail.Infrastructure.Accounts;

/// <summary>A credential ready to authenticate with. Kept in memory only for the duration of a connection.</summary>
public sealed record MailCredential(string Username, string? Password, string? AccessToken);

/// <summary>Loads passwords and OAuth tokens from the secret store and refreshes access tokens before they expire.</summary>
public sealed class CredentialProvider(ISecretStore secrets, OAuthClient oauth, OAuthConfiguration oauthConfiguration)
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(3);
    private readonly ConcurrentDictionary<Guid, OAuthTokens> _tokens = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _refreshLocks = new();

    // Accounts whose refresh token the provider rejected (revoked or expired). Asking again on every sync would only
    // repeat the failure, so they stay "sign in again" until new tokens are saved.
    private readonly ConcurrentDictionary<Guid, string> _signInRequired = new();

    public async Task<MailCredential> GetAsync(Guid accountId, AccountConnectionSettings settings, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (settings.AuthMethod == AuthMethod.Password)
        {
            var password = await secrets.GetAsync(SecretKeys.Password(accountId), ct).ConfigureAwait(false)
                ?? throw new OAuthException("The saved password for this account is missing. Sign in again from Settings → Accounts.");
            return new MailCredential(settings.Username, password, null);
        }

        if (_signInRequired.TryGetValue(accountId, out var reason)) throw new OAuthException(reason);

        var gate = _refreshLocks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tokens = _tokens.TryGetValue(accountId, out var cached) ? cached : await LoadTokensAsync(accountId, ct).ConfigureAwait(false);
            if (tokens is null)
            {
                throw new OAuthException("This account needs you to sign in again.");
            }

            if (forceRefresh || tokens.ExpiresAt - DateTimeOffset.UtcNow < RefreshMargin)
            {
                if (string.IsNullOrEmpty(tokens.RefreshToken))
                {
                    throw new OAuthException("Your sign-in has expired. Sign in again to keep receiving mail.");
                }

                var config = await oauthConfiguration.GetAsync(settings.AuthMethod, ct).ConfigureAwait(false);
                try
                {
                    tokens = await oauth.RefreshAsync(config, tokens.RefreshToken, ct).ConfigureAwait(false);
                }
                catch (OAuthException ex) when (!ex.IsTransient)
                {
                    _tokens.TryRemove(accountId, out _);
                    _signInRequired[accountId] = ex.Message;
                    throw;
                }

                await SaveTokensAsync(accountId, tokens, ct).ConfigureAwait(false);
            }

            _tokens[accountId] = tokens;
            return new MailCredential(settings.Username, null, tokens.AccessToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task SavePasswordAsync(Guid accountId, string password, CancellationToken ct = default) =>
        secrets.SetAsync(SecretKeys.Password(accountId), password, ct);

    public async Task SaveTokensAsync(Guid accountId, OAuthTokens tokens, CancellationToken ct = default)
    {
        // Save first: if writing fails, the next attempt starts from what's on disk rather than from tokens that
        // only ever existed in memory.
        await secrets.SetAsync(SecretKeys.OAuthTokens(accountId), JsonSerializer.Serialize(tokens), ct).ConfigureAwait(false);
        _tokens[accountId] = tokens;
        _signInRequired.TryRemove(accountId, out _);
    }

    public async Task DeleteAsync(Guid accountId, CancellationToken ct = default)
    {
        _tokens.TryRemove(accountId, out _);
        _signInRequired.TryRemove(accountId, out _);
        await secrets.DeletePrefixAsync(SecretKeys.AccountPrefix(accountId), ct).ConfigureAwait(false);
    }

    private async Task<OAuthTokens?> LoadTokensAsync(Guid accountId, CancellationToken ct)
    {
        var json = await secrets.GetAsync(SecretKeys.OAuthTokens(accountId), ct).ConfigureAwait(false);
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<OAuthTokens>(json);
        }
        catch (JsonException)
        {
            return null; // damaged record: the user is asked to sign in again
        }
    }
}
