using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using SidePhase.Mail.App.ViewModels;
using Windows.System;

namespace SidePhase.Mail.App.Views;

/// <summary>Adds a real account (Google, Microsoft or any IMAP/SMTP provider).</summary>
public sealed partial class AddAccountPage : Page
{
    public AddAccountPage()
    {
        ViewModel = App.GetService<AddAccountViewModel>();
        InitializeComponent();
        ViewModel.Completed += (_, _) => App.Main?.ShowInbox();
    }

    public AddAccountViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Apply(e.Parameter is AddAccountPreset preset ? preset : AddAccountPreset.Any);
        EmailBox.Focus(FocusState.Programmatic);
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelCommand.Execute(null);
        App.Main?.ShowWelcome();
    }

    private void OnWorkAccountClick(object sender, RoutedEventArgs e) => ViewModel.Apply(AddAccountPreset.Microsoft365);

    /// <summary>
    /// Optional (beta): fills in server settings from a Thunderbird profile, after the user agrees. Only prefs.js is
    /// read; passwords, tokens, mail and address books are never touched, and nothing is saved until the usual test.
    /// </summary>
    private async void OnThunderbirdClick(object sender, RoutedEventArgs e)
    {
        var dialogs = App.GetService<Services.DialogService>();
        var agreed = await dialogs.ConfirmAsync(
            "Use settings from Thunderbird?",
            "SidePhase Mail will read only the account settings (email addresses, server names, ports and user names) from a Thunderbird profile on this PC. " +
            "It won't read saved passwords, mail or contacts, and won't change anything in Thunderbird. You'll still sign in here, and nothing is saved until the connection test passes.",
            "Continue");
        if (!agreed) return;

        try
        {
            var accounts = new List<Infrastructure.Accounts.ThunderbirdAccount>();
            foreach (var profile in Infrastructure.Accounts.ThunderbirdProfileReader.FindProfiles(Infrastructure.Accounts.ThunderbirdProfileReader.DefaultRoot).OrderByDescending(p => p.IsDefault))
            {
                try
                {
                    accounts.AddRange(Infrastructure.Accounts.ThunderbirdProfileReader.ReadAccounts(profile.Path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // Try the other profiles.
                }
            }

            if (accounts.Count == 0)
            {
                // No profile in the usual place: let the user choose the profile folder.
                var picker = new Windows.Storage.Pickers.FolderPicker();
                picker.FileTypeFilter.Add("*");
                if (App.Main is { } window) WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
                var folder = await picker.PickSingleFolderAsync();
                if (folder is null) return;
                accounts.AddRange(Infrastructure.Accounts.ThunderbirdProfileReader.ReadAccounts(folder.Path));
            }

            var distinct = accounts.DistinctBy(a => a.EmailAddress.ToLowerInvariant()).ToList();
            if (distinct.Count == 0)
            {
                ViewModel.ShowImportProblem("No IMAP accounts with encrypted connections were found in that Thunderbird profile.");
                return;
            }

            var choice = new ComboBox { ItemsSource = distinct.Select(a => a.EmailAddress).ToList(), SelectedIndex = 0, MinWidth = 320 };
            var dialog = new ContentDialog
            {
                Title = "Choose an account",
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = "Its server settings will be filled in below. Add one account at a time.", TextWrapping = TextWrapping.Wrap },
                        choice,
                    },
                },
                PrimaryButtonText = "Use these settings",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialogs.ShowAsync(dialog) == ContentDialogResult.Primary && choice.SelectedIndex >= 0)
                ViewModel.UseImportedSettings(distinct[choice.SelectedIndex]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ViewModel.ShowImportProblem("The Thunderbird settings couldn't be read: " + ex.Message);
        }
    }

    private void OnEmailKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.ContinueCommand.CanExecute(null)) ViewModel.ContinueCommand.Execute(null);
    }

    private void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.AddCommand.CanExecute(null)) ViewModel.AddCommand.Execute(null);
    }
}
