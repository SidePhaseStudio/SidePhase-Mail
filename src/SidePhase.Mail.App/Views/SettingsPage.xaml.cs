using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SidePhase.Mail.App.ViewModels;

namespace SidePhase.Mail.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();
        ViewModel.ShowWelcomeRequested += OnShowWelcomeRequested;
        Unloaded += (_, _) => ViewModel.ShowWelcomeRequested -= OnShowWelcomeRequested;
    }

    public SettingsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string section) ShowSection(section);
        await ViewModel.LoadAsync();
        Bindings.Update();
    }

    /// <summary>Selects a section by its tag ("accounts", "about", …).</summary>
    public void ShowSection(string section)
    {
        foreach (var item in SectionBar.Items)
        {
            if (item.Tag as string == section)
            {
                SectionBar.SelectedItem = item;
                return;
            }
        }
    }

    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // SelectionChanged can fire while the page is still being parsed.
        if (AppearanceSection is null || AboutSection is null || ContentScroller is null) return;

        var tag = sender.SelectedItem?.Tag as string;
        AppearanceSection.Visibility = tag is null or "appearance" ? Visibility.Visible : Visibility.Collapsed;
        AccountsSection.Visibility = tag == "accounts" ? Visibility.Visible : Visibility.Collapsed;
        MailSection.Visibility = tag == "mail" ? Visibility.Visible : Visibility.Collapsed;
        SpamSection.Visibility = tag == "spam" ? Visibility.Visible : Visibility.Collapsed;
        PrivacySection.Visibility = tag == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsSection.Visibility = tag == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        RulesSection.Visibility = tag == "rules" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        ContentScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void OnAddAccountClick(object sender, RoutedEventArgs e) => App.Main?.ShowWelcome();

    private void OnShowWelcomeRequested(object? sender, EventArgs e) => App.Main?.ShowWelcome();
}
