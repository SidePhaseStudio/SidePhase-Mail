namespace SidePhase.Mail.Advertising;

/// <summary>
/// A built-in demonstration banner, clearly labelled as test content, for previewing the footer placement. Nothing is
/// downloaded or reported. The link uses the reserved example.com domain.
/// </summary>
public sealed class DemoAdProvider : IAdProvider
{
    internal static readonly SponsoredContent Card = new()
    {
        Id = "demo-footer",
        Advertiser = "SidePhase Studio",
        Headline = "Test promotion: this is where a SidePhase Studio banner appears",
        Body = "Promotions are small, labelled and never use your email. This one is only a preview.",
        CallToAction = "Example link",
        Destination = new Uri("https://example.com/"),
        Source = PromotionSource.Test,
    };

    public string Name => "Test content (built in)";

    public Task<SponsoredContent?> GetAsync(AdRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<SponsoredContent?>(Card);
}
