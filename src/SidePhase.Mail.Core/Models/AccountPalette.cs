namespace SidePhase.Mail.Core.Models;

/// <summary>
/// The colours used to tell accounts apart: new accounts get the next one, and the account settings offer the same
/// set (named for screen readers). From the same family as the accent colours.
/// </summary>
public static class AccountPalette
{
    public static readonly IReadOnlyList<(string Name, string Hex)> Colours =
    [
        ("Violet", "#8B7CFF"),
        ("Indigo", "#6C8CFF"),
        ("Blue", "#3FA7F5"),
        ("Teal", "#3FB6A8"),
        ("Green", "#4CC38A"),
        ("Amber", "#E9A93B"),
        ("Coral", "#FF7A6B"),
        ("Rose", "#F06BA8"),
        ("Graphite", "#9DA5B4"),
    ];

    /// <summary>The colour for the next account added (cycles through the palette, skipping the first, the app accent).</summary>
    public static string ForNewAccount(int existingAccounts) => Colours[1 + existingAccounts % (Colours.Count - 1)].Hex;
}
