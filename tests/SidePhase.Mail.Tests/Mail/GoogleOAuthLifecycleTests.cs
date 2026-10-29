using System.Net;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Settings;
using SidePhase.Mail.Infrastructure.Accounts;
using SidePhase.Mail.Infrastructure.OAuth;
using SidePhase.Mail.Infrastructure.Security;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Tests.Mail;

public sealed class GoogleOAuthLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sp-oauth-" + Guid.NewGuid().ToString("N"));
    private readonly FileSecretStore _store;
    private readonly SettingsService _settings = new(new InMemorySettingsStore());

    public GoogleOAuthLifecycleTests() => _store = new FileSecretStore(new AppDataPaths(_root), new XorProtector());
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private CredentialProvider Provider(HttpMessageHandler handler) => new(_store,
        new OAuthClient(new HttpClient(handler), new NoBrowser()), new OAuthConfiguration(_settings));

    private static AccountConnectionSettings Connection => new()
    {
        AuthMethod = AuthMethod.OAuthMicrosoft, // Refresh endpoint is simulated; no real registration needed.
        Username = "me@example.com",
        Imap = new("imap.example.com", 993, ConnectionSecurity.SslOnConnect),
        Smtp = new("smtp.example.com", 465, ConnectionSecurity.SslOnConnect),
    };

    [Theory]
    [InlineData(null, "studio-id", "studio-id")]
    [InlineData("", "studio-id", "studio-id")]
    [InlineData("  ", "studio-id", "studio-id")]
    [InlineData(" dev-id ", "studio-id", "dev-id")]
    public void Registration_uses_bundled_id_unless_developer_override_is_nonempty(string? development, string bundled, string expected) =>
        Assert.Equal(expected, OAuthConfiguration.ResolveGoogleClientId(development, bundled));

    [Fact]
    public void Build_has_registration_metadata_and_google_is_a_public_client()
    {
        Assert.EndsWith(".apps.googleusercontent.com", OAuthConfiguration.BundledGoogleClientId);
        var config = OAuthProviders.Google("studio-id");
        Assert.Equal("127.0.0.1", config.RedirectHost);
        Assert.Contains("https://mail.google.com/", config.Scopes);
    }

    [Fact]
    public async Task Google_exchange_and_refresh_use_the_desktop_value_only_in_the_token_request()
    {
        var handler = new FakeTokenHandler(HttpStatusCode.OK, """{"access_token":"access","refresh_token":"refresh","expires_in":3600}""");
        var client = new OAuthClient(new HttpClient(handler), new NoBrowser());
        await client.ExchangeCodeAsync(OAuthProviders.Google("studio-id", "desktop-value"), "code", "verifier", "http://127.0.0.1:1234/");
        await client.RefreshAsync(OAuthProviders.Google("studio-id", "desktop-value"), "refresh");
        Assert.All(handler.Requests, r => Assert.Contains("client_secret=desktop-value", r));
        Assert.DoesNotContain("desktop-value", OAuthClient.BuildAuthorizationUri(OAuthProviders.Google("studio-id", "desktop-value"), "http://127.0.0.1:1234/", "state", "challenge", null).AbsoluteUri);
        Assert.Contains("code_verifier=verifier", handler.Requests[0]);
    }

    [Fact]
    public async Task Partial_google_mail_consent_is_rejected()
    {
        var handler = new FakeTokenHandler(HttpStatusCode.OK, """{"access_token":"access","scope":"openid email"}""");
        var client = new OAuthClient(new HttpClient(handler), new NoBrowser());
        var error = await Assert.ThrowsAsync<OAuthException>(() => client.ExchangeCodeAsync(OAuthProviders.Google("studio-id"), "code", "verifier", "http://127.0.0.1:1234/"));
        Assert.Contains("fully granted", error.Message);
    }

    [Fact]
    public async Task Google_canonical_identity_scopes_are_accepted()
    {
        var handler = new FakeTokenHandler(HttpStatusCode.OK, """{"access_token":"access","scope":"https://mail.google.com/ openid https://www.googleapis.com/auth/userinfo.email"}""");
        var client = new OAuthClient(new HttpClient(handler), new NoBrowser());
        var tokens = await client.ExchangeCodeAsync(OAuthProviders.Google("studio-id"), "code", "verifier", "http://127.0.0.1:1234/");
        Assert.Equal("access", tokens.AccessToken);
    }

    [Fact]
    public async Task Listener_ignores_wrong_state_and_favicon_then_receives_valid_callback()
    {
        using var listener = new LoopbackRedirectListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var callback = listener.WaitForCallbackAsync(timeout.Token, "expected");
        using var http = new HttpClient();
        var redirect = listener.RedirectUri("127.0.0.1");
        using var favicon = await http.GetAsync(redirect + "favicon.ico", timeout.Token);
        Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);
        using var forged = await http.GetAsync(redirect + "?code=forged&state=wrong", timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        await http.GetStringAsync(redirect + "?code=real&state=expected", timeout.Token);
        Assert.Equal("real", (await callback)["code"]);
    }

    [Fact]
    public async Task Refresh_is_serialised_persisted_and_reused_after_restart()
    {
        _settings.Update(s => s.MicrosoftClientId = "test-client");
        var handler = new FakeTokenHandler(HttpStatusCode.OK, """{"access_token":"new-access","expires_in":3600}""");
        var provider = Provider(handler);
        var id = Guid.NewGuid();
        await provider.SaveTokensAsync(id, new("old-access", "refresh", DateTimeOffset.UtcNow.AddMinutes(-1), "me@example.com"));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.GetAsync(id, Connection)));
        Assert.All(results, r => Assert.Equal("new-access", r.AccessToken));
        Assert.Single(handler.Requests);
        Assert.Equal("new-access", (await Provider(handler).GetAsync(id, Connection)).AccessToken);
        Assert.Single(handler.Requests);
        Assert.Contains("refresh", (await _store.GetAsync(SecretKeys.OAuthTokens(id)))!);
    }

    [Fact]
    public async Task Revocation_stops_repeat_refresh_and_reauthentication_recovers()
    {
        _settings.Update(s => s.MicrosoftClientId = "test-client");
        var handler = new FakeTokenHandler(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var provider = Provider(handler);
        var id = Guid.NewGuid();
        await provider.SaveTokensAsync(id, new("expired", "revoked", DateTimeOffset.UtcNow.AddHours(-1), null));
        await Assert.ThrowsAsync<OAuthException>(() => provider.GetAsync(id, Connection));
        await Assert.ThrowsAsync<OAuthException>(() => provider.GetAsync(id, Connection));
        Assert.Single(handler.Requests);
        await provider.SaveTokensAsync(id, new("signed-in-again", "new-refresh", DateTimeOffset.UtcNow.AddHours(1), null));
        Assert.Equal("signed-in-again", (await provider.GetAsync(id, Connection)).AccessToken);
    }

    [Fact]
    public async Task Removing_account_clears_memory_and_disk_and_refuses_late_token_save()
    {
        var provider = Provider(new FakeTokenHandler(HttpStatusCode.OK, "{}"));
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var tokens = new OAuthTokens("access", "refresh", DateTimeOffset.UtcNow.AddHours(1), null);
        await provider.SaveTokensAsync(id, tokens);
        await provider.SaveTokensAsync(other, tokens);
        await provider.DeleteAsync(id);
        Assert.Null(await _store.GetAsync(SecretKeys.OAuthTokens(id)));
        await Assert.ThrowsAsync<OAuthException>(() => provider.GetAsync(id, Connection));
        await Assert.ThrowsAsync<OAuthException>(() => provider.SaveTokensAsync(id, tokens));
        Assert.Equal("access", (await provider.GetAsync(other, Connection)).AccessToken);
    }
}
