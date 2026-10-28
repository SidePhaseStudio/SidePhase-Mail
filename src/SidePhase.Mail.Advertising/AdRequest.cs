namespace SidePhase.Mail.Advertising;

/// <summary>
/// Everything an ad provider is ever told. Deliberately nothing about the user or their mail: no email contents,
/// subjects, addresses, contacts, folders, accounts or identifiers. A test enforces that this type stays this small.
/// </summary>
/// <param name="Placement">Where the card will be shown.</param>
/// <param name="AppVersion">SidePhase Mail's version (e.g. "0.7.0-beta.1").</param>
/// <param name="Language">The display language (e.g. "en-GB"), for choosing a card in the right language.</param>
public sealed record AdRequest(AdPlacement Placement, string AppVersion, string Language);

/// <summary>Supplies sponsored cards. Implementations must never throw for ordinary failures: return null instead.</summary>
public interface IAdProvider
{
    /// <summary>A short name for diagnostics and the settings page ("Test content", "SidePhase Studio campaigns").</summary>
    string Name { get; }

    Task<SponsoredContent?> GetAsync(AdRequest request, CancellationToken cancellationToken = default);
}
