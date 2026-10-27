using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SidePhase.Mail.Core.Abstractions;

namespace SidePhase.Mail.App.Views;

/// <summary>First-run welcome and the "Add account" destination.</summary>
public sealed partial class WelcomePage : Page
{
    public WelcomePage()
    {
        InitializeComponent();
    }

    private void OnExploreDemoClick(object sender, RoutedEventArgs e)
    {
        App.GetService<ISettingsService>().Update(s =>
        {
            s.HasCompletedWelcome = true;
            s.DataMode = Core.Settings.DataMode.Demo;
        });
        App.Main?.ShowInbox();
    }

    private void OnGoogleClick(object sender, RoutedEventArgs e) => App.Main?.ShowAddAccount(ViewModels.AddAccountPreset.Google);

    private void OnMicrosoftClick(object sender, RoutedEventArgs e) => App.Main?.ShowAddAccount(ViewModels.AddAccountPreset.Microsoft);

    private void OnOtherClick(object sender, RoutedEventArgs e) => App.Main?.ShowAddAccount();
}
