using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SidePhase.Mail.App.ViewModels;

namespace SidePhase.Mail.App.Views;

public sealed partial class AttachmentsPage : Page
{
    public AttachmentsPage()
    {
        ViewModel = App.GetService<AttachmentsViewModel>();
        InitializeComponent();
        ViewModel.OpenMessage = id => App.Main?.OpenMessageAsync(id) ?? Task.CompletedTask;
    }

    public AttachmentsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.RefreshAsync();
    }
}
