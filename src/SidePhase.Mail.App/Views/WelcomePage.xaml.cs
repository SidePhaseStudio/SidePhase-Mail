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
        App.GetService<ISettingsService>().Update(s => s.HasCompletedWelcome = true);
        App.Main?.ShowInbox();
    }
}
