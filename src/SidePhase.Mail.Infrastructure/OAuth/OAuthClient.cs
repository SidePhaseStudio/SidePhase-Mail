using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SidePhase.Mail.Core.Abstractions;

namespace SidePhase.Mail.Infrastructure.OAuth;

/// <summary>Opens URLs in the user's default browser.</summary>
public interface IBrowserLauncher
{
    void Open(Uri uri);
}

public sealed class ShellBrowserLauncher : IBrowserLauncher
{
    public void Open(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Only https sign-in pages are opened.", nameof(uri));
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}

/// <summary>
/// OAuth 2.0 authorization code flow for native apps (RFC 8252): the provider's own sign-in page opens in the system
/// browser, the app never sees the user's password, and PKCE + a random state protect the redirect.
/// </summary>
public sealed class OAuthClient(HttpClient http, IBrowserLauncher browser)
{
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    public async Task<OAuthTokens> SignInAsync(OAuthProviderConfig config, string? loginHint, CancellationToken cancellationToken = default)
    {
        using var listener = new LoopbackRedirectListener();
        var redirectUri = listener.RedirectUri(config.RedirectHost);
        var verifier = Pkce.CreateVerifier();
        var state = Pkce.CreateState();

        browser.Open(BuildAuthorizationUri(config, redirectUri, state, Pkce.Challenge(verifier), loginHint));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);
        IReadOnlyDictionary<string, string> callback;
        try
        {
            callback = await listener.WaitForCallbackAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OAuthException($"{config.Name} sign-in timed out. Try again when you're ready.");
        }

        var code = ValidateCallback(callback, state, config.Name);
        return await ExchangeCodeAsync(config, code, verifier, redirectUri, cancellationToken).ConfigureAwait(false);
    }

    public static Uri BuildAuthorizationUri(OAuthProviderConfig config, string redirectUri, string state, string codeChallenge, string? loginHint)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = string.Join(' ', config.Scopes),
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };
        if (!string.IsNullOrWhiteSpace(loginHint)) query["login_hint"] = loginHint;
        foreach (var (key, value) in config.ExtraAuthorizationParameters) query[key] = value;

        var builder = new StringBuilder(config.AuthorizationEndpoint.AbsoluteUri).Append('?');
        builder.AppendJoin('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return new Uri(builder.ToString());
    }

    /// <summary>Checks the redirect: the state must match exactly, and errors become friendly messages.</summary>
    public static string ValidateCallback(IReadOnlyDictionary<string, string> callback, string expectedState, string providerName)
    {
        if (!callback.TryGetValue("state", out var state) || !CryptographicEquals(state, expectedState))
        {
            throw new OAuthException("The sign-in response didn't match this request, so it was ignored for your safety. Please try again.");
        }

        if (callback.TryGetValue("error", out var error))
        {
            throw error switch
            {
                "access_denied" => new OAuthException($"{providerName} sign-in was cancelled, or access wasn't granted."),
                _ => new OAuthException($"{providerName} sign-in failed ({error}). {callback.GetValueOrDefault("error_description")}".Trim()),
            };
        }

        return callback.TryGetValue("code", out var code) && !string.IsNullOrEmpty(code)
            ? code
            : throw new OAuthException($"{providerName} didn't return an authorisation code.");
    }

    public async Task<OAuthTokens> ExchangeCodeAsync(OAuthProviderConfig config, string code, string verifier, string redirectUri, CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = config.ClientId,
            ["code_verifier"] = verifier,
            ["scope"] = string.Join(' ', config.Scopes),
        };
        if (!string.IsNullOrEmpty(config.ClientSecret)) form["client_secret"] = config.ClientSecret;
        return await PostTokenRequestAsync(config, form, existingRefreshToken: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OAuthTokens> RefreshAsync(OAuthProviderConfig config, string refreshToken, CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = config.ClientId,
            ["scope"] = string.Join(' ', config.Scopes),
        };
        if (!string.IsNullOrEmpty(config.ClientSecret)) form["client_secret"] = config.ClientSecret;
        return await PostTokenRequestAsync(config, form, refreshToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OAuthTokens> PostTokenRequestAsync(OAuthProviderConfig config, Dictionary<string, string> form, string? existingRefreshToken, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(config.TokenEndpoint, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OAuthException($"Couldn't reach {config.Name} to refresh the sign-in. SidePhase Mail will try again.", ex, isTransient: true);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OAuthException($"{config.Name} took too long to respond. SidePhase Mail will try again.", ex, isTransient: true);
        }

        using (response)
        {
            JsonElement json;
            try
            {
                json = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
            {
                // Not a token response at all (an outage page, a captive portal or a proxy's error page).
                throw new OAuthException($"{config.Name} sent an unexpected response ({(int)response.StatusCode}). SidePhase Mail will try again.", ex, isTransient: true);
            }

            if (!response.IsSuccessStatusCode || json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("access_token", out var accessToken))
            {
                var error = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var e) ? e.GetString() : null;
                var status = (int)response.StatusCode;
                if (error == "invalid_grant")
                    throw new OAuthException($"Your {config.Name} sign-in has expired or was revoked. Sign in again to keep receiving mail.");
                if (status >= 500 || status == 429 || error is "temporarily_unavailable" or "server_error")
                    throw new OAuthException($"{config.Name} is having problems right now ({error ?? status.ToString(System.Globalization.CultureInfo.InvariantCulture)}). SidePhase Mail will try again.", isTransient: true);
                throw new OAuthException($"{config.Name} rejected the sign-in ({error ?? status.ToString(System.Globalization.CultureInfo.InvariantCulture)}).");
            }

            return ReadTokens(json, accessToken, existingRefreshToken);
        }
    }

    private static OAuthTokens ReadTokens(JsonElement json, JsonElement accessToken, string? existingRefreshToken)
    {
        var expiresIn = json.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds) ? seconds : 3600;
        var refresh = json.TryGetProperty("refresh_token", out var r) ? r.GetString() : existingRefreshToken;
        var email = json.TryGetProperty("id_token", out var idToken) ? ReadEmailClaim(idToken.GetString()) : null;
        return new OAuthTokens(accessToken.GetString()!, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn), email);
    }

    /// <summary>
    /// Reads the email from an ID token's payload. The token came directly from the provider's token endpoint over TLS,
    /// and is only used to fill in the address; it grants nothing by itself.
    /// </summary>
    internal static string? ReadEmailClaim(string? idToken)
    {
        if (string.IsNullOrEmpty(idToken)) return null;
        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            foreach (var claim in new[] { "email", "preferred_username", "upn" })
            {
                if (doc.RootElement.TryGetProperty(claim, out var value) && value.GetString() is { } s && s.Contains('@')) return s;
            }
        }
        catch (FormatException)
        {
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static bool CryptographicEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
