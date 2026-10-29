using System.Text.Json;

namespace SidePhase.Mail.Advertising;

/// <summary>
/// SidePhase Studio's own promotions, bundled with the app in Assets/Promotions/promotions.json. Only entries marked
/// <c>"approved": true</c> and inside their date range are shown; the file ships with none approved, so the footer stays
/// collapsed until SidePhase Studio approves a campaign. Nothing is downloaded.
/// <code>
/// { "promotions": [ { "id": "…", "approved": true, "advertiser": "SidePhase Studio", "headline": "…", "body": "…",
///     "callToAction": "…", "destination": "https://…", "image": "banner.png",
///     "starts": "2027-01-01", "ends": "2027-02-01" } ] }
/// </code>
/// </summary>
public sealed class HousePromotionProvider(string promotionsFile, Func<DateTimeOffset>? now = null) : IAdProvider
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private IReadOnlyList<PromotionDto>? _loaded;
    private int _next;

    public string Name => "SidePhase Studio promotions (bundled)";

    public Task<SponsoredContent?> GetAsync(AdRequest request, CancellationToken cancellationToken = default)
    {
        var today = _now();
        var eligible = Load()
            .Where(p => p.Approved && (p.Starts is null || p.Starts <= today) && (p.Ends is null || today < p.Ends))
            .Select(ToContent)
            .Where(c => c is not null && c.IsValid(out _))
            .ToList();
        if (eligible.Count == 0) return Task.FromResult<SponsoredContent?>(null);
        var index = (int)((uint)Interlocked.Increment(ref _next) % (uint)eligible.Count);
        return Task.FromResult(eligible[index]);
    }

    private IReadOnlyList<PromotionDto> Load()
    {
        if (_loaded is not null) return _loaded;
        try
        {
            _loaded = File.Exists(promotionsFile)
                ? JsonSerializer.Deserialize<PromotionsFile>(File.ReadAllText(promotionsFile), AdvertisingSettings.Json)?.Promotions ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _loaded = []; // a broken file shows nothing, it never breaks the app
        }

        return _loaded;
    }

    private static SponsoredContent? ToContent(PromotionDto p)
    {
        if (p.Destination is not null && !Uri.TryCreate(p.Destination, UriKind.Absolute, out _)) return null;
        return new SponsoredContent
        {
            Id = p.Id ?? string.Empty,
            Advertiser = p.Advertiser?.Trim() ?? "SidePhase Studio",
            Headline = p.Headline?.Trim() ?? string.Empty,
            Body = p.Body?.Trim() ?? string.Empty,
            CallToAction = string.IsNullOrWhiteSpace(p.CallToAction) ? "Learn more" : p.CallToAction.Trim(),
            Destination = p.Destination is null ? null : new Uri(p.Destination),
            Image = string.IsNullOrWhiteSpace(p.Image) ? null : p.Image.Trim(),
            Source = PromotionSource.House,
        };
    }

    internal sealed record PromotionsFile(IReadOnlyList<PromotionDto>? Promotions);

    internal sealed record PromotionDto(
        string? Id, bool Approved, string? Advertiser, string? Headline, string? Body, string? CallToAction,
        string? Destination, string? Image, DateTimeOffset? Starts, DateTimeOffset? Ends);
}
