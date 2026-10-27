using System.Globalization;

namespace SidePhase.Mail.Core.Formatting;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>Colour utilities used to derive accent shades with accessible contrast.</summary>
public static class ColorMath
{
    public static bool TryParseHex(string? hex, out Rgb color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 8) s = s[2..]; // ignore alpha (#AARRGGBB)
        if (s.Length != 6) return false;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static Rgb ParseHex(string hex) =>
        TryParseHex(hex, out var c) ? c : throw new FormatException($"'{hex}' is not a #RRGGBB colour.");

    /// <summary>Mixes <paramref name="color"/> towards <paramref name="target"/> by <paramref name="amount"/> (0..1).</summary>
    public static Rgb Mix(Rgb color, Rgb target, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new Rgb(
            (byte)Math.Round(color.R + (target.R - color.R) * amount),
            (byte)Math.Round(color.G + (target.G - color.G) * amount),
            (byte)Math.Round(color.B + (target.B - color.B) * amount));
    }

    public static Rgb Lighten(Rgb color, double amount) => Mix(color, new Rgb(255, 255, 255), amount);

    public static Rgb Darken(Rgb color, double amount) => Mix(color, new Rgb(0, 0, 0), amount);

    public static double RelativeLuminance(Rgb c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>WCAG 2 contrast ratio between two colours (1..21).</summary>
    public static double ContrastRatio(Rgb a, Rgb b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>
    /// Moves <paramref name="color"/> towards black or white until it reaches <paramref name="minRatio"/> against
    /// <paramref name="background"/>.
    /// </summary>
    public static Rgb EnsureContrast(Rgb color, Rgb background, double minRatio)
    {
        if (ContrastRatio(color, background) >= minRatio) return color;
        var towardsWhite = RelativeLuminance(background) < 0.5;
        for (var step = 1; step <= 20; step++)
        {
            var candidate = towardsWhite ? Lighten(color, step * 0.05) : Darken(color, step * 0.05);
            if (ContrastRatio(candidate, background) >= minRatio) return candidate;
        }

        return towardsWhite ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0);
    }

    /// <summary>Accent shades for one theme.</summary>
    public sealed record AccentPalette(Rgb Base, Rgb Fill, Rgb FillHover, Rgb FillPressed, Rgb Text, Rgb OnFill);

    /// <summary>
    /// Derives the accent palette. Fills are darkened until white text on them meets 4.5:1, and accent text is
    /// adjusted to 4.5:1 against the theme surface.
    /// </summary>
    public static AccentPalette DeriveAccent(Rgb accent, bool darkTheme)
    {
        var white = new Rgb(255, 255, 255);
        var surface = darkTheme ? ParseHex("#151820") : ParseHex("#F7F8FB");
        var fill = EnsureContrast(accent, white, 4.5);
        var hover = darkTheme ? Lighten(fill, 0.08) : Darken(fill, 0.08);
        var pressed = darkTheme ? Darken(fill, 0.12) : Darken(fill, 0.18);
        var text = EnsureContrast(darkTheme ? Lighten(accent, 0.15) : Darken(accent, 0.1), surface, 4.5);
        return new AccentPalette(accent, fill, hover, pressed, text, white);
    }
}
