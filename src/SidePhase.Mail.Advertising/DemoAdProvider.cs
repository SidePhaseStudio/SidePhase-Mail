namespace SidePhase.Mail.Advertising;

/// <summary>
/// Built-in demonstration cards, clearly marked as test content, used to try the placements and design. Nothing
/// is downloaded and nothing is reported anywhere. Destinations use the reserved example.com domain.
/// </summary>
public sealed class DemoAdProvider : IAdProvider
{
    internal static readonly IReadOnlyList<SponsoredContent> Cards =
    [
        new()
        {
            Id = "demo-1",
            Advertiser = "Example Advertiser",
            Headline = "Your product could be here",
            Body = "This is a test advertisement. Real campaigns appear only after approval.",
            CallToAction = "Example link",
            Destination = new Uri("https://example.com/"),
            IsTest = true,
        },
        new()
        {
            Id = "demo-2",
            Advertiser = "Example Studio",
            Headline = "A quiet, relevant message",
            Body = "Test content showing how a sponsored card looks in SidePhase Mail.",
            CallToAction = "Example link",
            Destination = new Uri("https://example.org/"),
            IsTest = true,
        },
        new()
        {
            Id = "demo-3",
            Advertiser = "Example Partner",
            Headline = "Sponsored cards are small and labelled",
            Body = "They never appear inside your email and never use what's in it.",
            CallToAction = "Example link",
            Destination = new Uri("https://example.net/"),
            IsTest = true,
            Placements = [AdPlacement.EmptyReadingPane],
        },
    ];

    private int _next;

    public string Name => "Test content (built in)";

    public Task<SponsoredContent?> GetAsync(AdRequest request, CancellationToken cancellationToken = default)
    {
        var eligible = Cards.Where(c => c.Placements.Count == 0 || c.Placements.Contains(request.Placement)).ToList();
        if (eligible.Count == 0) return Task.FromResult<SponsoredContent?>(null);
        var index = (int)((uint)Interlocked.Increment(ref _next) % (uint)eligible.Count);
        return Task.FromResult<SponsoredContent?>(eligible[index]);
    }
}
