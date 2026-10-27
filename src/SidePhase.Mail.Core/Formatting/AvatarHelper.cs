using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Formatting;

public static class AvatarHelper
{
    /// <summary>Number of colours in the avatar palette defined by the UI.</summary>
    public const int PaletteSize = 8;

    public static string Initials(EmailAddress address) => Initials(address.Name, address.Address);

    public static string Initials(string? name, string? address)
    {
        var source = string.IsNullOrWhiteSpace(name) ? address : name;
        if (string.IsNullOrWhiteSpace(source)) return "?";

        source = source.Trim();
        var at = source.IndexOf('@');
        if (at > 0) source = source[..at];

        // Ignore parenthesised suffixes and quotes: "Jane Doe (Finance)".
        var paren = source.IndexOf('(');
        if (paren > 0) source = source[..paren];

        var words = source
            .Split([' ', '.', '_', '-', '\t', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0]))
            .ToArray();

        if (words.Length == 0) return "?";
        if (words.Length == 1) return char.ToUpperInvariant(words[0][0]).ToString();

        return string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[^1][0]));
    }

    /// <summary>A stable palette index for an address (FNV-1a, so it is identical across runs and platforms).</summary>
    public static int ColorIndex(string? address)
    {
        var key = (address ?? string.Empty).Trim().ToLowerInvariant();
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in key)
            {
                hash ^= c;
                hash *= 16777619u;
            }

            return (int)(hash % PaletteSize);
        }
    }
}
