using System.Net;
using System.Text;
using SidePhase.Mail.Infrastructure.OAuth;

namespace SidePhase.Mail.Tests.Mail;

internal sealed class FakeTokenHandler(HttpStatusCode status, string json) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

internal sealed class NoBrowser : IBrowserLauncher
{
    public Uri? Opened { get; private set; }

    public void Open(Uri uri) => Opened = uri;
}

public class OAuthTests
{
    [Fact]
    public void Pkce_challenge_matches_rfc_7636_example()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void Verifiers_and_states_are_random_and_url_safe()
    {
        var verifier = Pkce.CreateVerifier();
        Assert.InRange(verifier.Length, 43, 128);
        Assert.DoesNotContain('+', verifier);
        Assert.DoesNotContain('/', verifier);
        Assert.DoesNotContain('=', verifier);
        Assert.NotEqual(Pkce.CreateState(), Pkce.CreateState());
    }

    [Fact]
    public void Authorization_uri_requests_pkce_and_the_documented_scopes_only()
    {
        var config = OAuthProviders.Google("client-123.apps.googleusercontent.com", null);
        var uri = OAuthClient.BuildAuthorizationUri(config, "http://127.0.0.1:5555/", "state-xyz", "challenge-abc", "me@gmail.com");
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("accounts.google.com", uri.Host);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("challenge-abc", query["code_challenge"]);
        Assert.Equal("state-xyz", query["state"]);
        Assert.Equal("http://127.0.0.1:5555/", query["redirect_uri"]);
        Assert.Equal("https://mail.google.com/ openid email", query["scope"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Null(query["client_secret"]);

        var ms = OAuthProviders.Microsoft("00000000-0000-0000-0000-000000000000");
        var msUri = OAuthClient.BuildAuthorizationUri(ms, "http://localhost:5555/", "s", "c", null);
        var msScopes = System.Web.HttpUtility.ParseQueryString(msUri.Query)["scope"]!;
        Assert.Contains("IMAP.AccessAsUser.All", msScopes);
        Assert.Contains("SMTP.Send", msScopes);
        Assert.Contains("offline_access", msScopes);
        Assert.DoesNotContain("Mail.ReadWrite", msScopes);
    }

    [Fact]
    public void Callback_with_wrong_or_missing_state_is_rejected()
    {
        Assert.Throws<OAuthException>(() => OAuthClient.ValidateCallback(new Dictionary<string, string> { ["code"] = "c", ["state"] = "forged" }, "expected", "Google"));
        Assert.Throws<OAuthException>(() => OAuthClient.ValidateCallback(new Dictionary<string, string> { ["code"] = "c" }, "expected", "Google"));
    }

    [Fact]
    public void Callback_errors_become_friendly_messages()
    {
        var ex = Assert.Throws<OAuthException>(() => OAuthClient.ValidateCallback(new Dictionary<string, string> { ["error"] = "access_denied", ["state"] = "s" }, "s", "Microsoft"));
        Assert.Contains("cancelled", ex.Message);
        Assert.Equal("the-code", OAuthClient.ValidateCallback(new Dictionary<string, string> { ["code"] = "the-code", ["state"] = "s" }, "s", "Google"));
    }

    [Fact]
    public async Task Code_exchange_parses_tokens_and_email()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"email\":\"me@gmail.com\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var handler = new FakeTokenHandler(HttpStatusCode.OK, $$"""{"access_token":"at-1","refresh_token":"rt-1","expires_in":1800,"id_token":"x.{{payload}}.y"}""");
        var client = new OAuthClient(new HttpClient(handler), new NoBrowser());

        var tokens = await client.ExchangeCodeAsync(OAuthProviders.Google("cid", "csecret"), "code-1", "verifier-1", "http://127.0.0.1:1/");
        Assert.Equal("at-1", tokens.AccessToken);
        Assert.Equal("rt-1", tokens.RefreshToken);
        Assert.Equal("me@gmail.com", tokens.Email);
        Assert.InRange(tokens.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(29), DateTimeOffset.UtcNow.AddMinutes(31));
        Assert.Contains("code_verifier=verifier-1", handler.Requests.Single());
        Assert.Contains("grant_type=authorization_code", handler.Requests.Single());
    }

    [Fact]
    public async Task Refresh_keeps_the_old_refresh_token_when_none_is_returned()
    {
        var handler = new FakeTokenHandler(HttpStatusCode.OK, """{"access_token":"at-2","expires_in":3600}""");
        var client = new OAuthClient(new HttpClient(handler), new NoBrowser());
        var tokens = await client.RefreshAsync(OAuthProviders.Microsoft("cid"), "rt-old");
        Assert.Equal("at-2", tokens.AccessToken);
        Assert.Equal("rt-old", tokens.RefreshToken);
        Assert.DoesNotContain("client_secret", handler.Requests.Single());
    }

    [Fact]
    public async Task Revoked_refresh_token_asks_the_user_to_sign_in_again()
    {
        var handler = new FakeTokenHandler(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var client = new OAuthClient(new HttpClient(handler), new NoBrowser());
        var ex = await Assert.ThrowsAsync<OAuthException>(() => client.RefreshAsync(OAuthProviders.Google("cid", null), "rt"));
        Assert.Contains("Sign in again", ex.Message);
        Assert.DoesNotContain("rt", ex.Message.Split(' '));
    }

    [Fact]
    public async Task Loopback_listener_receives_the_redirect_query()
    {
        using var listener = new LoopbackRedirectListener();
        var wait = listener.WaitForCallbackAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        using var http = new HttpClient();
        var page = await http.GetStringAsync($"http://127.0.0.1:{listener.Port}/?code=abc%20d&state=s1");
        var query = await wait;
        Assert.Equal("abc d", query["code"]);
        Assert.Equal("s1", query["state"]);
        Assert.Contains("SidePhase", page);
    }
}
