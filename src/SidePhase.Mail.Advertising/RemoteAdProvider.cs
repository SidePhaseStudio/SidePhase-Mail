using System.Net.Http.Json;
using System.Text.Json;

namespace SidePhase.Mail.Advertising;

/// <summary>
/// The separate SidePhase Ads API (not part of this repository): <c>GET {endpoint}?placement=…&amp;version=…&amp;lang=…</c>
/// over HTTPS, answered with one banner as JSON. The request carries only <see cref="AdRequest"/> (no cookies, no
/// identifiers, no mail data); the answer is validated and anything that breaks the content rules is dropped. Remote
/// banners are text only (no images are downloaded). Used only when explicitly configured
/// (<see cref="AdvertisingMode.Production"/> plus an endpoint in advertising.json).
/// <code>
/// { "id": "c-123", "advertiser": "…", "headline": "…", "body": "…", "callToAction": "…", "destination": "https://…" }
/// </code>
/// </summary>
public sealed class RemoteAdProvider : IAdProvider
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;

    /// <summary>Creates the provider, or returns null if the endpoint isn't a plain https:// address.</summary>
    public static RemoteAdProvider? Create(HttpClient http, string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
            ? new RemoteAdProvider(http, uri)
            : null;

    private RemoteAdProvider(HttpClient http, Uri endpoint)
    {
        _http = http;
        _endpoint = endpoint;
    }

    public string Name => "SidePhase Studio campaigns";

    /// <summary>The exact URL requested for <paramref name="request"/> (public so tests can check what's sent).</summary>
    public Uri RequestUri(AdRequest request) => new(
        $"{_endpoint.GetLeftPart(UriPartial.Path)}?placement={request.Placement}&version={Uri.EscapeDataString(request.AppVersion)}&lang={Uri.EscapeDataString(request.Language)}");

    public async Task<SponsoredContent?> GetAsync(AdRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, RequestUri(request));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.SendAsync(message, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength > 16 * 1024) return null;
            var dto = await response.Content.ReadFromJsonAsync<CardDto>(AdvertisingSettings.Json, timeout.Token).ConfigureAwait(false);
            return Parse(dto, request.Placement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }

    internal static SponsoredContent? Parse(CardDto? dto, AdPlacement placement)
    {
        if (dto is null) return null;
        var card = new SponsoredContent
        {
            Id = dto.Id ?? string.Empty,
            Advertiser = dto.Advertiser?.Trim() ?? string.Empty,
            Headline = dto.Headline?.Trim() ?? string.Empty,
            Body = dto.Body?.Trim() ?? string.Empty,
            CallToAction = string.IsNullOrWhiteSpace(dto.CallToAction) ? "Learn more" : dto.CallToAction.Trim(),
            Destination = Uri.TryCreate(dto.Destination, UriKind.Absolute, out var destination) ? destination : null,
            Source = PromotionSource.Remote,
        };
        if (dto.Destination is not null && card.Destination is null) return null;
        return card.IsValid(out _) ? card : null;
    }

    internal sealed record CardDto(
        string? Id, string? Advertiser, string? Headline, string? Body, string? CallToAction, string? Destination);
}
