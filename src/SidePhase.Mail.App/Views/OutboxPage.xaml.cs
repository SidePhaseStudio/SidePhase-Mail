using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SidePhase.Mail.App.ViewModels;

namespace SidePhase.Mail.App.Views;

public sealed partial class OutboxPage : Page
{
    public OutboxPage()
    {
        ViewModel = App.GetService<OutboxViewModel>();
        InitializeComponent();
    }

    public OutboxViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.RefreshAsync();
    }
}
