using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SidePhase.Mail.App.Helpers;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Formatting;
using SidePhase.Mail.Core.Rendering;
using SidePhase.Mail.Core.Settings;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace SidePhase.Mail.App.Services;

/// <summary>
/// Applies the theme (Dark Glass, Light Glass, Solid Dark, Solid Light, Follow Windows), the accent colour and the
/// window backdrop to every open window.
/// </summary>
/// <remarks>
/// Glass themes use the native Mica backdrop (falling back to Desktop Acrylic on Windows 10). They automatically
/// become solid when Windows "Transparency effects" are off or a contrast theme is active, so the app always
/// respects the user's accessibility settings.
/// </remarks>
public sealed class ThemeService
{
    private readonly ISettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly DispatcherQueue _dispatcher;
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly List<WindowEntry> _windows = [];
    private string _appliedAccent = string.Empty;
    private AppTheme _appliedTheme;

    private sealed record WindowEntry(Window Window, FrameworkElement Root, UIElement SolidLayer);

    public ThemeService(ISettingsService settings, DialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _appliedTheme = settings.Current.Theme;

        _settings.SettingsChanged += (_, e) => _dispatcher.TryEnqueue(() => OnSettingsChanged(e.Settings));
        _uiSettings.AdvancedEffectsEnabledChanged += (_, _) => _dispatcher.TryEnqueue(ApplyAll);
        _uiSettings.ColorValuesChanged += (_, _) => _dispatcher.TryEnqueue(ApplyAll);
        _accessibility.HighContrastChanged += (_, _) => _dispatcher.TryEnqueue(ApplyAll);
    }

    public event EventHandler? ThemeApplied;

    /// <summary>True when glass effects are wanted but Windows has transparency turned off (shown in Settings).</summary>
    public bool TransparencyDisabledBySystem => !_uiSettings.AdvancedEffectsEnabled;

    public bool IsHighContrast => _accessibility.HighContrast;

    /// <summary>Call once at start-up, before any window is created.</summary>
    public void InitializeResources()
    {
        ApplyAccent(_settings.Current.AccentColor, refreshWindows: false);
    }

    public void Register(Window window, FrameworkElement root, UIElement solidLayer)
    {
        var entry = new WindowEntry(window, root, solidLayer);
        _windows.Add(entry);
        window.Closed += (_, _) => _windows.Remove(entry);
        root.ActualThemeChanged += (_, _) =>
        {
            UpdateTitleBarColors(entry);
            ThemeApplied?.Invoke(this, EventArgs.Empty);
        };
        Apply(entry);
    }

    public static ElementTheme ToElementTheme(AppTheme theme) => theme switch
    {
        AppTheme.DarkGlass or AppTheme.SolidDark => ElementTheme.Dark,
        AppTheme.LightGlass or AppTheme.SolidLight => ElementTheme.Light,
        _ => ElementTheme.Default,
    };

    public static bool IsGlass(AppTheme theme) => theme is AppTheme.DarkGlass or AppTheme.LightGlass or AppTheme.FollowWindows;

    /// <summary>Colours used to render message bodies so they match the reading pane.</summary>
    public EmailRenderTheme GetEmailTheme(ElementTheme actualTheme)
    {
        var dark = actualTheme != ElementTheme.Light;
        var accent = ColorMath.TryParseHex(_settings.Current.AccentColor, out var rgb) ? rgb : ColorMath.ParseHex(AppSettings.DefaultAccent);
        var palette = ColorMath.DeriveAccent(accent, dark);
        var baseTheme = dark ? EmailRenderTheme.Dark : EmailRenderTheme.Light;
        return baseTheme with { Accent = palette.Text.ToHex() };
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (!string.Equals(settings.AccentColor, _appliedAccent, StringComparison.OrdinalIgnoreCase))
        {
            ApplyAccent(settings.AccentColor, refreshWindows: true);
        }

        if (settings.Theme != _appliedTheme)
        {
            _appliedTheme = settings.Theme;
            ApplyAll();
        }
    }

    private void ApplyAll()
    {
        foreach (var entry in _windows.ToArray())
        {
            Apply(entry);
        }

        ThemeApplied?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(WindowEntry entry)
    {
        var theme = _settings.Current.Theme;
        var highContrast = _accessibility.HighContrast;
        var requested = highContrast ? ElementTheme.Default : ToElementTheme(theme);
        entry.Root.RequestedTheme = requested;
        _dialogs.Theme = requested;

        SystemBackdrop? backdrop = null;
        if (!highContrast && IsGlass(theme) && _uiSettings.AdvancedEffectsEnabled)
        {
            if (MicaController.IsSupported())
            {
                backdrop = entry.Window.SystemBackdrop as MicaBackdrop ?? new MicaBackdrop { Kind = MicaKind.Base };
            }
            else if (DesktopAcrylicController.IsSupported())
            {
                backdrop = entry.Window.SystemBackdrop as DesktopAcrylicBackdrop ?? new DesktopAcrylicBackdrop();
            }
        }

        if (!ReferenceEquals(entry.Window.SystemBackdrop, backdrop))
        {
            entry.Window.SystemBackdrop = backdrop;
        }

        // Solid themes (and every fallback) paint an opaque background under the translucent layers.
        entry.SolidLayer.Visibility = backdrop is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateTitleBarColors(entry);
    }

    private static void UpdateTitleBarColors(WindowEntry entry)
    {
        var titleBar = entry.Window.AppWindow?.TitleBar;
        if (titleBar is null) return;

        var dark = entry.Root.ActualTheme == ElementTheme.Dark;
        var foreground = dark ? Colors.White : Color.FromArgb(255, 20, 22, 28);
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(110, 255, 255, 255) : Color.FromArgb(110, 0, 0, 0);
        titleBar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0);
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(10, 0, 0, 0);
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    private void ApplyAccent(string hex, bool refreshWindows)
    {
        if (!ColorMath.TryParseHex(hex, out var accent))
        {
            accent = ColorMath.ParseHex(AppSettings.DefaultAccent);
        }

        _appliedAccent = hex;
        var palette = FindPaletteDictionary();
        if (palette is not null)
        {
            foreach (var key in new[] { "Default", "Dark", "Light" })
            {
                if (!palette.ThemeDictionaries.TryGetValue(key, out var value) || value is not ResourceDictionary dictionary)
                {
                    continue;
                }

                var dark = key != "Light";
                var p = ColorMath.DeriveAccent(accent, dark);
                SetBrush(dictionary, "SpAccentBrush", dark ? p.Base : p.Fill);
                SetBrush(dictionary, "SpAccentTextBrush", p.Text);
                SetBrush(dictionary, "SpAccentFillBrush", p.Fill);
                SetBrush(dictionary, "SpSelectionBrush", dark ? p.Base : p.Fill);
                SetBrush(dictionary, "SpSelectionHoverBrush", dark ? p.Base : p.Fill);
                SetBrush(dictionary, "SpBadgeBrush", dark ? p.Base : p.Fill);
                SetBrush(dictionary, "SpBadgeTextBrush", dark ? ColorMath.Lighten(p.Base, 0.55) : ColorMath.Darken(p.Text, 0.1));
                SetBrush(dictionary, "AccentFillColorDefaultBrush", p.Fill);
                SetBrush(dictionary, "AccentFillColorSecondaryBrush", p.FillHover);
                SetBrush(dictionary, "AccentFillColorTertiaryBrush", p.FillPressed);
                SetBrush(dictionary, "AccentFillColorSelectedTextBackgroundBrush", p.Fill);
                SetBrush(dictionary, "AccentTextFillColorPrimaryBrush", p.Text);
                SetBrush(dictionary, "AccentTextFillColorSecondaryBrush", p.Text);
                SetBrush(dictionary, "AccentTextFillColorTertiaryBrush", p.Base);
            }
        }

        var resources = Application.Current.Resources;
        resources["SystemAccentColor"] = ToColor(accent);
        resources["SystemAccentColorLight1"] = ToColor(ColorMath.Lighten(accent, 0.15));
        resources["SystemAccentColorLight2"] = ToColor(ColorMath.Lighten(accent, 0.3));
        resources["SystemAccentColorLight3"] = ToColor(ColorMath.Lighten(accent, 0.45));
        resources["SystemAccentColorDark1"] = ToColor(ColorMath.Darken(accent, 0.12));
        resources["SystemAccentColorDark2"] = ToColor(ColorMath.Darken(accent, 0.25));
        resources["SystemAccentColorDark3"] = ToColor(ColorMath.Darken(accent, 0.38));

        if (refreshWindows)
        {
            // Re-resolve {ThemeResource} references so every control picks up the new accent immediately.
            foreach (var entry in _windows.ToArray())
            {
                var requested = entry.Root.RequestedTheme;
                entry.Root.RequestedTheme = entry.Root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
                entry.Root.RequestedTheme = requested;
            }

            ThemeApplied?.Invoke(this, EventArgs.Empty);
        }
    }

    private static ResourceDictionary? FindPaletteDictionary()
    {
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
        {
            if (dictionary.Source?.OriginalString.EndsWith("Palette.xaml", StringComparison.OrdinalIgnoreCase) == true)
            {
                return dictionary;
            }
        }

        return null;
    }

    private static void SetBrush(ResourceDictionary dictionary, string key, Rgb color)
    {
        if (dictionary.TryGetValue(key, out var value) && value is SolidColorBrush brush)
        {
            brush.Color = ToColor(color);
        }
    }

    private static Color ToColor(Rgb rgb) => Color.FromArgb(255, rgb.R, rgb.G, rgb.B);
}
