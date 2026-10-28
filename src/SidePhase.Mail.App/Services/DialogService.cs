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

    /// <summary>
    /// Asks for a future date and time in the user's local time zone. Returns the exact instant, or null if cancelled.
    /// </summary>
    public async Task<DateTimeOffset?> PickDateTimeAsync(string title, string confirmText, DateTimeOffset suggested, XamlRoot? root = null)
    {
        var local = suggested.ToLocalTime();
        var date = new CalendarDatePicker
        {
            Header = "Date",
            Date = local.Date,
            MinDate = DateTimeOffset.Now.Date,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var time = new TimePicker { Header = "Time", Time = local.TimeOfDay, MinuteIncrement = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Opacity = 0.85 };
        var zone = new TextBlock { Text = $"Time zone: {TimeZoneInfo.Local.DisplayName}", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 12, MinWidth = 300 };
        panel.Children.Add(date);
        panel.Children.Add(time);
        panel.Children.Add(zone);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        DateTimeOffset? result = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (date.Date is not { } day)
            {
                error.Text = "Choose a date.";
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            var when = Core.Scheduling.QuickTimes.FromLocal(DateOnly.FromDateTime(day.Date), TimeOnly.FromTimeSpan(time.Time), TimeZoneInfo.Local);
            if (when <= DateTimeOffset.UtcNow)
            {
                error.Text = "Choose a time in the future.";
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            result = when;
        };

        return await ShowAsync(dialog, root) == ContentDialogResult.Primary ? result : null;
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
