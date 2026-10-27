using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SidePhase.Mail.App.Services;

/// <summary>Shows ContentDialogs on the main window (or a supplied XamlRoot) using the current theme.</summary>
public sealed class DialogService
{
    public XamlRoot? DefaultXamlRoot { get; set; }

    public ElementTheme Theme { get; set; } = ElementTheme.Default;

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, XamlRoot? root = null)
    {
        dialog.XamlRoot = root ?? DefaultXamlRoot ?? throw new InvalidOperationException("No XamlRoot available for dialogs.");
        dialog.RequestedTheme = Theme;
        dialog.Style ??= Application.Current.Resources["DefaultContentDialogStyle"] as Style;
        dialog.CornerRadius = new CornerRadius(12);
        return await dialog.ShowAsync();
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText = "Cancel", XamlRoot? root = null)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = confirmText,
            CloseButtonText = cancelText,
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog, root) == ContentDialogResult.Primary;
    }
}
