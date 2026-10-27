using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SidePhase.Mail.App.Controls;

/// <summary>A Windows 11 style settings row: icon, header, description and a control on the right.</summary>
/// <remarks>The template lives in Themes/Styles.xaml as an implicit style.</remarks>
public sealed partial class SettingsCard : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty, OnDescriptionChanged));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingsCard), new PropertyMetadata(""));

    private TextBlock? _description;

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _description = GetTemplateChild("DescriptionText") as TextBlock;
        UpdateDescriptionVisibility();
    }

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SettingsCard)d).UpdateDescriptionVisibility();

    private void UpdateDescriptionVisibility()
    {
        if (_description is not null)
        {
            _description.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
