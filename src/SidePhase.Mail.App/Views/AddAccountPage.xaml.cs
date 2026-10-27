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

    private void OnEmailKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.ContinueCommand.CanExecute(null)) ViewModel.ContinueCommand.Execute(null);
    }

    private void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.AddCommand.CanExecute(null)) ViewModel.AddCommand.Execute(null);
    }
}
