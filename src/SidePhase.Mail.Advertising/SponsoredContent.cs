namespace SidePhase.Mail.Advertising;

/// <summary>
/// Where a promotion may appear. There is one placement: a banner centred in the main window's footer. Never inside
/// the inbox, the message list, a message or a compose window.
/// </summary>
public enum AdPlacement
{
    Footer,
}

/// <summary>Where a promotion came from (decides its label).</summary>
public enum PromotionSource
{
    /// <summary>Built-in demonstration banner: labelled "Test content".</summary>
    Test,

    /// <summary>An approved SidePhase Studio promotion bundled with the app.</summary>
    House,

    /// <summary>From the separate SidePhase Ads API (only when explicitly configured).</summary>
    Remote,
}

/// <summary>
/// One promotion banner (about 728 × 90). Plain text, optionally with an image bundled in the app (never a remote
/// image), so it renders with the app's own controls and can't run anything, track the reader or look like part of an
/// email. Always labelled "Promotion" in the UI.
/// </summary>
public sealed record SponsoredContent
{
    public const int MaxAdvertiserLength = 40;
    public const int MaxHeadlineLength = 70;
    public const int MaxBodyLength = 140;
    public const int MaxCallToActionLength = 24;

    public required string Id { get; init; }

    /// <summary>Who the promotion is from, shown on the banner.</summary>
    public required string Advertiser { get; init; }

    public required string Headline { get; init; }

    public string Body { get; init; } = string.Empty;

    public string CallToAction { get; init; } = "Learn more";

    /// <summary>Opened in the browser only when the user clicks, after the usual link confirmation. HTTPS only.</summary>
    public Uri? Destination { get; init; }

    /// <summary>
    /// Optional banner image bundled with the app, relative to Assets/Promotions (e.g. "spring-2027.png"). PNG or JPEG,
    /// designed at 728 × 90. Never a URL: nothing is downloaded to show a promotion.
    /// </summary>
    public string? Image { get; init; }

    public PromotionSource Source { get; init; } = PromotionSource.House;

    public bool IsTest => Source == PromotionSource.Test;

    /// <summary>True if the banner meets the content rules (lengths, plain text, HTTPS destination, local image).</summary>
    public bool IsValid(out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(Id)) problem = "missing id";
        else if (string.IsNullOrWhiteSpace(Advertiser) || Advertiser.Length > MaxAdvertiserLength) problem = "advertiser missing or too long";
        else if (string.IsNullOrWhiteSpace(Headline) || Headline.Length > MaxHeadlineLength) problem = "headline missing or too long";
        else if (Body.Length > MaxBodyLength) problem = "body too long";
        else if (string.IsNullOrWhiteSpace(CallToAction) || CallToAction.Length > MaxCallToActionLength) problem = "call to action missing or too long";
        else if (Destination is { } uri && (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))) problem = "destination must be an https:// address";
        else if (Image is { } image && !IsLocalImageName(image)) problem = "image must be a .png or .jpg file name in Assets/Promotions";
        else if (new[] { Advertiser, Headline, Body, CallToAction }.Any(t => t.Contains('<') || t.Contains('>'))) problem = "markup isn't allowed";
        else if (new[] { Advertiser, Headline, Body, CallToAction }.Any(t => t.Any(char.IsControl))) problem = "control characters aren't allowed";
        return problem is null;
    }

    /// <summary>A plain file name (no folders, no URL) with a PNG/JPEG extension.</summary>
    public static bool IsLocalImageName(string name) =>
        name.Length is > 4 and <= 80
        && name.IndexOfAny(['/', '\\', ':', '?', '#', '%']) < 0
        && !name.StartsWith('.')
        && (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));
}
