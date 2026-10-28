namespace SidePhase.Mail.Advertising;

/// <summary>Where a sponsored card may appear. Never inside a message, and (for now) never in the message list.</summary>
public enum AdPlacement
{
    /// <summary>A small card at the bottom of the left sidebar (only while the sidebar is open).</summary>
    SidebarFooter,

    /// <summary>A card in the reading pane when no message is open.</summary>
    EmptyReadingPane,
}

/// <summary>
/// One sponsored card. Plain text only (no HTML, images or scripts), so it renders with the app's own controls and
/// can't run anything, track the reader or look like part of an email. Always labelled "Sponsored" in the UI.
/// </summary>
public sealed record SponsoredContent
{
    public const int MaxAdvertiserLength = 40;
    public const int MaxHeadlineLength = 60;
    public const int MaxBodyLength = 140;
    public const int MaxCallToActionLength = 24;

    public required string Id { get; init; }

    /// <summary>Who the advertisement is from, shown on the card.</summary>
    public required string Advertiser { get; init; }

    public required string Headline { get; init; }

    public string Body { get; init; } = string.Empty;

    public string CallToAction { get; init; } = "Learn more";

    /// <summary>Opened in the browser only when the user clicks, after the usual link confirmation. HTTPS only.</summary>
    public Uri? Destination { get; init; }

    /// <summary>Demonstration content: the card says it's a test.</summary>
    public bool IsTest { get; init; }

    /// <summary>Placements this card may appear in (empty: any).</summary>
    public IReadOnlyList<AdPlacement> Placements { get; init; } = [];

    /// <summary>True if the card meets the content rules (lengths, plain text, HTTPS destination).</summary>
    public bool IsValid(out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(Id)) problem = "missing id";
        else if (string.IsNullOrWhiteSpace(Advertiser) || Advertiser.Length > MaxAdvertiserLength) problem = "advertiser missing or too long";
        else if (string.IsNullOrWhiteSpace(Headline) || Headline.Length > MaxHeadlineLength) problem = "headline missing or too long";
        else if (Body.Length > MaxBodyLength) problem = "body too long";
        else if (string.IsNullOrWhiteSpace(CallToAction) || CallToAction.Length > MaxCallToActionLength) problem = "call to action missing or too long";
        else if (Destination is { } uri && (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))) problem = "destination must be an https:// address";
        else if (new[] { Advertiser, Headline, Body, CallToAction }.Any(LooksLikeMarkup)) problem = "markup isn't allowed";
        else if (new[] { Advertiser, Headline, Body, CallToAction }.Any(t => t.Any(char.IsControl))) problem = "control characters aren't allowed";
        return problem is null;
    }

    private static bool LooksLikeMarkup(string text) => text.Contains('<') || text.Contains('>');
}
