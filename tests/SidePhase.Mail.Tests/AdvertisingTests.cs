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

    private string PromotionsFile => Path.Combine(_dir, "promotions.json");

    private AdService Service(AdvertisingMode mode, IAdProvider? provider = null, bool adFree = false, Func<DateTimeOffset>? now = null, string? endpoint = null)
    {
        var store = new AdvertisingSettingsStore(_dir);
        store.Save(new AdvertisingSettings { Mode = mode, SimulateAdFree = adFree, Endpoint = endpoint });
        AdService? service = null;
        var entitlement = new LocalAdEntitlement(() => service?.Settings ?? store.Load());
        service = new AdService(store, entitlement, "0.7.0-beta.1", "en-GB", provider is null ? null : _ => provider, now, PromotionsFile);
        return service;
    }

    private void WritePromotions(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PromotionsFile, json);
    }

    [Fact]
    public void Bundled_promotions_are_the_default()
    {
        Assert.Equal(AdvertisingMode.House, new AdvertisingSettingsStore(_dir).Load().Mode);
    }

    [Fact]
    public async Task With_no_approved_promotion_nothing_is_shown_so_the_footer_collapses()
    {
        WritePromotions("""{ "promotions": [ { "id": "draft", "approved": false, "headline": "Not approved yet" } ] }""");
        Assert.Null(await Service(AdvertisingMode.House).GetCardAsync(AdPlacement.Footer));

        File.Delete(PromotionsFile);
        Assert.Null(await Service(AdvertisingMode.House).GetCardAsync(AdPlacement.Footer));

        WritePromotions("{ broken json");
        Assert.Null(await Service(AdvertisingMode.House).GetCardAsync(AdPlacement.Footer));
    }

    [Fact]
    public async Task Approved_house_promotions_show_within_their_dates_only()
    {
        WritePromotions("""
            { "promotions": [
              { "id": "spring", "approved": true, "headline": "Spring update", "destination": "https://sidephase.example/spring",
                "image": "spring.png", "starts": "2027-03-01T00:00:00Z", "ends": "2027-04-01T00:00:00Z" } ] }
            """);
        var inside = await Service(AdvertisingMode.House, now: () => DateTimeOffset.Parse("2027-03-15T00:00:00Z")).GetCardAsync(AdPlacement.Footer);
        Assert.NotNull(inside);
        Assert.Equal(PromotionSource.House, inside.Source);
        Assert.Equal("SidePhase Studio", inside.Advertiser);
        Assert.Equal("spring.png", inside.Image);

        Assert.Null(await Service(AdvertisingMode.House, now: () => DateTimeOffset.Parse("2027-04-02T00:00:00Z")).GetCardAsync(AdPlacement.Footer));
    }

    [Theory]
    [InlineData("https://cdn.example/banner.png")]
    [InlineData("../../secret.png")]
    [InlineData("banner.exe")]
    [InlineData("C:\\banner.png")]
    public async Task House_promotion_images_must_be_bundled_files(string image)
    {
        WritePromotions(System.Text.Json.JsonSerializer.Serialize(new { promotions = new[] { new { id = "x", approved = true, headline = "Hi", image } } }));
        Assert.Null(await Service(AdvertisingMode.House).GetCardAsync(AdPlacement.Footer));
    }

    [Fact]
    public async Task Nothing_is_shown_when_off_ad_free_or_hidden()
    {
        Assert.Null(await Service(AdvertisingMode.Off).GetCardAsync(AdPlacement.Footer));
        Assert.Null(await Service(AdvertisingMode.Demo, adFree: true).GetCardAsync(AdPlacement.Footer));

        var hidden = Service(AdvertisingMode.Demo);
        hidden.HideForSession();
        Assert.Null(await hidden.GetCardAsync(AdPlacement.Footer));
    }

    [Fact]
    public async Task Demo_cards_are_marked_as_test_content_and_valid()
    {
        var card = await Service(AdvertisingMode.Demo).GetCardAsync(AdPlacement.Footer);
        Assert.NotNull(card);
        Assert.True(card.IsTest);
        Assert.True(DemoAdProvider.Card.IsValid(out var problem), problem);
        Assert.Equal("example.com", DemoAdProvider.Card.Destination!.Host); // reserved example domain only
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://ads.example/v1/banner")]
    [InlineData("not a url")]
    public async Task Production_mode_without_an_explicit_https_endpoint_stays_off(string? endpoint)
    {
        var service = Service(AdvertisingMode.Production, endpoint: endpoint);
        Assert.False(service.IsActive);
        Assert.Equal(AdvertisingMode.Off, AdService.EffectiveMode(service.Settings));
        Assert.Null(await service.GetCardAsync(AdPlacement.Footer));
    }

    [Fact]
    public void Production_needs_explicit_configuration()
    {
        Assert.Equal(AdvertisingMode.Production, AdService.EffectiveMode(new AdvertisingSettings { Mode = AdvertisingMode.Production, Endpoint = "https://ads.sidephase.example/v1/banner" }));
    }

    [Fact]
    public async Task A_card_stays_for_the_minimum_time_instead_of_rotating()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new CountingProvider();
        var service = Service(AdvertisingMode.Demo, provider, now: () => now);

        var first = await service.GetCardAsync(AdPlacement.Footer);
        var again = await service.GetCardAsync(AdPlacement.Footer);
        Assert.Same(first, again);
        Assert.Equal(1, provider.Calls);

        now += AdvertisingPolicy.MinimumDisplayTime;
        await service.GetCardAsync(AdPlacement.Footer);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task Invalid_cards_and_provider_failures_show_nothing()
    {
        Assert.Null(await Service(AdvertisingMode.Demo, new CountingProvider { Card = Card() with { Destination = new Uri("http://insecure.example/") } }).GetCardAsync(AdPlacement.Footer));
        Assert.Null(await Service(AdvertisingMode.Demo, new CountingProvider { Card = Card() with { Headline = "<b>Click</b>" } }).GetCardAsync(AdPlacement.Footer));
        Assert.Null(await Service(AdvertisingMode.Demo, new ThrowingProvider()).GetCardAsync(AdPlacement.Footer));
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
        var handler = new RecordingHandler("""{"id":"c1","advertiser":"Acme","headline":"Hello","body":"Plain text","callToAction":"Visit","destination":"https://acme.example/"}""");
        var provider = RemoteAdProvider.Create(new HttpClient(handler), "https://ads.sidephase.example/v1/card")!;

        var card = await provider.GetAsync(new AdRequest(AdPlacement.Footer, "0.6.0-beta.1", "en-GB"));

        Assert.NotNull(card);
        Assert.False(card.IsTest);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://ads.sidephase.example/v1/card?placement=Footer&version=0.6.0-beta.1&lang=en-GB", request.Uri);
        Assert.Equal(PromotionSource.Remote, card.Source);
        Assert.Null(card.Image); // remote banners never download images
        Assert.Null(request.Cookie);
        Assert.Equal(HttpMethod.Get, request.Method);
    }

    [Theory]
    [InlineData("""{"id":"c1","advertiser":"Acme","headline":"Hi","destination":"http://acme.example/"}""")]
    [InlineData("""{"id":"c1","advertiser":"Acme","headline":"<script>x</script>"}""")]
    [InlineData("""{"id":"c1","advertiser":"Acme","headline":"Hi","destination":"javascript:alert(1)"}""")]
    [InlineData("<html>not json</html>")]
    public async Task Remote_cards_that_break_the_rules_are_dropped(string json)
    {
        var provider = RemoteAdProvider.Create(new HttpClient(new RecordingHandler(json)), "https://ads.sidephase.example/v1/card")!;
        Assert.Null(await provider.GetAsync(new AdRequest(AdPlacement.Footer, "1", "en")));
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
        Assert.NotNull(await service.GetCardAsync(AdPlacement.Footer));
        var changed = false;
        service.Changed += (_, _) => changed = true;

        service.Update(s => s.SimulateAdFree = true);

        Assert.True(changed);
        Assert.False(service.IsActive);
        Assert.Null(await service.GetCardAsync(AdPlacement.Footer));
        Assert.True(new AdvertisingSettingsStore(_dir).Load().SimulateAdFree);
    }

    [Fact]
    public void Damaged_settings_fall_back_to_the_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "advertising.json"), "{ nope");
        Assert.Equal(AdvertisingMode.House, new AdvertisingSettingsStore(_dir).Load().Mode);
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
