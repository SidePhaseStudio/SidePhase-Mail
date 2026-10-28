using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.App.ViewModels;
using SidePhase.Mail.Core.Contacts;
using Windows.Storage.Pickers;

namespace SidePhase.Mail.App.Views;

public sealed partial class ContactsPage : Page
{
    public ContactsPage()
    {
        ViewModel = App.GetService<ContactsViewModel>();
        InitializeComponent();
        ViewModel.EditContact = ShowEditorAsync;
    }

    public ContactsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.RefreshAsync();
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add(".vcf");
        picker.FileTypeFilter.Add(".vcard");
        if (App.Main is { } window) WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var properties = await file.GetBasicPropertiesAsync();
        if (properties.Size > (ulong)VCardParser.MaxFileCharacters)
        {
            App.GetService<ToastService>().Show("This file is too large to be a contacts file.", ToastKind.Error);
            return;
        }

        // Read as text only; the file is never opened or run by anything else.
        var text = await File.ReadAllTextAsync(file.Path);
        await ViewModel.ImportAsync(text, file.Name);
    }

    /// <summary>The add/edit form. Addresses go one per line.</summary>
    private async Task<Contact?> ShowEditorAsync(Contact? existing)
    {
        var name = new TextBox { Header = "Name", Text = existing?.DisplayName ?? string.Empty };
        var emails = new TextBox
        {
            Header = "Email addresses (one per line)",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 72,
            IsSpellCheckEnabled = false,
            Text = string.Join(Environment.NewLine, existing?.Emails.Select(e => e.Address) ?? []),
        };
        var notes = new TextBox
        {
            Header = "Notes",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60,
            Text = existing?.Notes ?? string.Empty,
        };
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(name);
        panel.Children.Add(emails);
        panel.Children.Add(notes);

        var dialog = new ContentDialog
        {
            Title = existing is null ? "New contact" : "Edit contact",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await App.GetService<DialogService>().ShowAsync(dialog) != ContentDialogResult.Primary) return null;

        var labels = existing?.Emails.ToDictionary(e => e.Address, e => e.Label, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var addresses = emails.Text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => new ContactEmail(a, labels.GetValueOrDefault(a)))
            .ToList();

        return existing is null
            ? new Contact { Id = Guid.NewGuid(), DisplayName = name.Text, Emails = addresses, Notes = notes.Text }
            : existing with { DisplayName = name.Text, Emails = addresses, Notes = notes.Text };
    }
}
