using System.Net;
using System.Text;
using SidePhase.Mail.Advertising;

namespace SidePhase.Mail.Tests;

public sealed class AdvertisingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-ads-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private AdService Service(AdvertisingMode mode, IAdProvider? provider = null, bool adFree = false, Func<DateTimeOffset>? now = null)
    {
        var store = new AdvertisingSettingsStore(_dir);
        store.Save(new AdvertisingSettings { Mode = mode, SimulateAdFree = adFree });
        AdService? service = null;
        var entitlement = new LocalAdEntitlement(() => service?.Settings ?? store.Load());
        service = new AdService(store, entitlement, "0.6.0-beta.1", "en-GB", provider is null ? null : _ => provider, now);
        return service;
    }

    [Fact]
    public void Off_is_the_default()
    {
        Assert.Equal(AdvertisingMode.Off, new AdvertisingSettingsStore(_dir).Load().Mode);
    }

    [Fact]
    public async Task Nothing_is_shown_when_off_ad_free_or_hidden()
    {
        Assert.Null(await Service(AdvertisingMode.Off).GetCardAsync(AdPlacement.SidebarFooter));
        Assert.Null(await Service(AdvertisingMode.Demo, adFree: true).GetCardAsync(AdPlacement.SidebarFooter));

        var hidden = Service(AdvertisingMode.Demo);
        hidden.HideForSession();
        Assert.Null(await hidden.GetCardAsync(AdPlacement.SidebarFooter));
    }

    [Fact]
    public async Task Demo_cards_are_marked_as_test_content_and_valid()
    {
        var card = await Service(AdvertisingMode.Demo).GetCardAsync(AdPlacement.SidebarFooter);
        Assert.NotNull(card);
        Assert.True(card.IsTest);
        Assert.All(DemoAdProvider.Cards, c =>
        {
            Assert.True(c.IsValid(out var problem), problem);
            Assert.True(c.IsTest);
            Assert.Matches(@"^example\.(com|org|net)$", c.Destination!.Host); // reserved example domains only
        });
    }

    [Fact]
    public async Task Production_mode_stays_off_until_enabled_in_a_reviewed_release()
    {
        Assert.False(AdvertisingPolicy.ProductionAdvertisingEnabled);
        var provider = new CountingProvider();
        var service = Service(AdvertisingMode.Production);
        Assert.False(service.IsActive);
        Assert.Equal(AdvertisingMode.Off, AdService.EffectiveMode(service.Settings));
        Assert.Null(await service.GetCardAsync(AdPlacement.SidebarFooter));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task A_card_stays_for_the_minimum_time_instead_of_rotating()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new CountingProvider();
        var service = Service(AdvertisingMode.Demo, provider, now: () => now);

        var first = await service.GetCardAsync(AdPlacement.SidebarFooter);
        var again = await service.GetCardAsync(AdPlacement.SidebarFooter);
        Assert.Same(first, again);
        Assert.Equal(1, provider.Calls);

        now += AdvertisingPolicy.MinimumDisplayTime;
        await service.GetCardAsync(AdPlacement.SidebarFooter);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task Invalid_cards_and_provider_failures_show_nothing()
    {
        Assert.Null(await Service(AdvertisingMode.Demo, new CountingProvider { Card = Card() with { Destination = new Uri("http://insecure.example/") } }).GetCardAsync(AdPlacement.SidebarFooter));
        Assert.Null(await Service(AdvertisingMode.Demo, new CountingProvider { Card = Card() with { Headline = "<b>Click</b>" } }).GetCardAsync(AdPlacement.SidebarFooter));
        Assert.Null(await Service(AdvertisingMode.Demo, new ThrowingProvider()).GetCardAsync(AdPlacement.SidebarFooter));
    }

    [Fact]
    public void The_request_carries_no_user_or_mail_data()
    {
        // If anyone adds a property to AdRequest, this fails and the privacy policy must be reviewed first.
        var properties = typeof(AdRequest).GetProperties().Select(p => p.Name).Order();
        Assert.Equal(["AppVersion", "Language", "Placement"], properties);

        // And the advertising project can't see mail data at all: it references neither the mail engine nor the core.
        var references = typeof(AdRequest).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(references, r => r.StartsWith("SidePhase.Mail.Core", StringComparison.Ordinal) || r.StartsWith("SidePhase.Mail.Infrastructure", StringComparison.Ordinal) || r.StartsWith("MailKit", StringComparison.Ordinal) || r.StartsWith("MimeKit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remote_provider_sends_only_placement_version_and_language_and_validates_the_answer()
    {
        var handler = new RecordingHandler("""{"id":"c1","advertiser":"Acme","headline":"Hello","body":"Plain text","callToAction":"Visit","destination":"https://acme.example/","placements":["SidebarFooter"]}""");
        var provider = RemoteAdProvider.Create(new HttpClient(handler), "https://ads.sidephase.example/v1/card")!;

        var card = await provider.GetAsync(new AdRequest(AdPlacement.SidebarFooter, "0.6.0-beta.1", "en-GB"));

        Assert.NotNull(card);
        Assert.False(card.IsTest);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://ads.sidephase.example/v1/card?placement=SidebarFooter&version=0.6.0-beta.1&lang=en-GB", request.Uri);
        Assert.Null(request.Cookie);
        Assert.Equal(HttpMethod.Get, request.Method);
    }

    [Theory]
    [InlineData("""{"id":"c1","advertiser":"Acme","headline":"Hi","destination":"http://acme.example/"}""")]
    [InlineData("""{"id":"c1","advertiser":"Acme","headline":"<script>x</script>"}""")]
    [InlineData("""{"id":"c1","advertiser":"Acme","headline":"Hi","destination":"javascript:alert(1)"}""")]
    [InlineData("""{"id":"c1","advertiser":"Acme","headline":"Hi","placements":["MessageBody"]}""")]
    [InlineData("""{"id":"c1","advertiser":"Acme","headline":"Hi","placements":["EmptyReadingPane"]}""")]
    [InlineData("<html>not json</html>")]
    public async Task Remote_cards_that_break_the_rules_are_dropped(string json)
    {
        var provider = RemoteAdProvider.Create(new HttpClient(new RecordingHandler(json)), "https://ads.sidephase.example/v1/card")!;
        Assert.Null(await provider.GetAsync(new AdRequest(AdPlacement.SidebarFooter, "1", "en")));
    }

    [Theory]
    [InlineData("http://ads.example/card")]
    [InlineData("https://user:pw@ads.example/card")]
    [InlineData("https://ads.example/card?track=1")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void Remote_provider_needs_a_plain_https_endpoint(string? endpoint) =>
        Assert.Null(RemoteAdProvider.Create(new HttpClient(), endpoint));

    [Fact]
    public async Task Ad_free_takes_effect_immediately()
    {
        var service = Service(AdvertisingMode.Demo);
        Assert.NotNull(await service.GetCardAsync(AdPlacement.EmptyReadingPane));
        var changed = false;
        service.Changed += (_, _) => changed = true;

        service.Update(s => s.SimulateAdFree = true);

        Assert.True(changed);
        Assert.False(service.IsActive);
        Assert.Null(await service.GetCardAsync(AdPlacement.EmptyReadingPane));
        Assert.True(new AdvertisingSettingsStore(_dir).Load().SimulateAdFree);
    }

    [Fact]
    public void Damaged_settings_fall_back_to_off()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "advertising.json"), "{ nope");
        Assert.Equal(AdvertisingMode.Off, new AdvertisingSettingsStore(_dir).Load().Mode);
    }

    private static SponsoredContent Card() => new() { Id = "x", Advertiser = "Acme", Headline = "Hello", Destination = new Uri("https://acme.example/") };

    private sealed class CountingProvider : IAdProvider
    {
        public int Calls { get; private set; }
        public SponsoredContent? Card { get; init; }
        public string Name => "counting";

        public Task<SponsoredContent?> GetAsync(AdRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<SponsoredContent?>(Card ?? AdvertisingTests.Card() with { Id = "c" + Calls });
        }
    }

    private sealed class ThrowingProvider : IAdProvider
    {
        public string Name => "throwing";
        public Task<SponsoredContent?> GetAsync(AdRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("boom");
    }

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public List<(string Uri, HttpMethod Method, string? Cookie)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri, request.Method, request.Headers.TryGetValues("Cookie", out var c) ? string.Join(";", c) : null));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
