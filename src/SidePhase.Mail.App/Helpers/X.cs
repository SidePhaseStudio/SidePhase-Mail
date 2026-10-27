using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace SidePhase.Mail.App.Helpers;

/// <summary>
/// Small pure functions used from x:Bind expressions. Keeping them here avoids a zoo of IValueConverters.
/// </summary>
public static class X
{
    private static readonly Color[] AvatarPalette =
    [
        ColorFromHex("#6E62E0"), // violet
        ColorFromHex("#3B78D0"), // blue
        ColorFromHex("#1F8F86"), // teal
        ColorFromHex("#B8507A"), // rose
        ColorFromHex("#A86A1F"), // amber
        ColorFromHex("#4A8F55"), // green
        ColorFromHex("#5E6B84"), // slate
        ColorFromHex("#8E55C2"), // purple
    ];

    private static readonly Dictionary<int, SolidColorBrush> AvatarBrushes = [];
    private static readonly Dictionary<string, SolidColorBrush> HexBrushes = [];

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static FontWeight Weight(bool strong) => strong ? FontWeights.SemiBold : FontWeights.Normal;

    public static double Opacity(bool full, double dimmed) => full ? 1.0 : dimmed;

    public static double StarOpacity(bool starred, bool hovered) => starred || hovered ? 1.0 : 0.0;

    public static double AvatarSize(bool compact) => compact ? 28 : 36;

    public static double AvatarFontSize(bool compact) => compact ? 11 : 13;

    public static Thickness RowPadding(bool compact) => compact ? new Thickness(10, 6, 10, 6) : new Thickness(10, 10, 10, 10);

    public static SolidColorBrush AvatarBrush(int index)
    {
        index = Math.Abs(index) % AvatarPalette.Length;
        if (!AvatarBrushes.TryGetValue(index, out var brush))
        {
            brush = new SolidColorBrush(AvatarPalette[index]);
            AvatarBrushes[index] = brush;
        }

        return brush;
    }

    public static SolidColorBrush HexBrush(string? hex)
    {
        hex ??= "#8B7CFF";
        if (!HexBrushes.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush(ColorFromHex(hex));
            HexBrushes[hex] = brush;
        }

        return brush;
    }

    public static Color ColorFromHex(string hex)
    {
        if (Core.Formatting.ColorMath.TryParseHex(hex, out var rgb))
        {
            return Color.FromArgb(255, rgb.R, rgb.G, rgb.B);
        }

        return Colors.Gray;
    }
}
