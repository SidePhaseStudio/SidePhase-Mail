using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.Core.Contacts;
using SidePhase.Mail.Core.Formatting;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.App.ViewModels;

public sealed partial class ContactItemViewModel(Contact contact, ContactsViewModel owner) : ObservableObject
{
    public Contact Contact { get; } = contact;
    public string DisplayName => Contact.DisplayName;
    public string EmailsText => string.Join(", ", Contact.Emails.Select(e => e.Label is null ? e.Address : $"{e.Address} ({e.Label})"));
    public string Initials => AvatarHelper.Initials(new EmailAddress(Contact.DisplayName, Contact.PrimaryEmail ?? Contact.DisplayName));
    public int AvatarIndex => AvatarHelper.ColorIndex(Contact.PrimaryEmail);
    public bool CanWrite => Contact.PrimaryEmail is not null;
    public string SourceText => Contact.Source == ContactSources.VCard ? "Imported" : string.Empty;

    [RelayCommand] private Task WriteAsync() => owner.WriteToAsync(this);
    [RelayCommand] private Task EditAsync() => owner.EditAsync(this);
    [RelayCommand] private Task DeleteAsync() => owner.DeleteAsync(this);
}

/// <summary>The address book. Contacts are stored on this PC only and never uploaded.</summary>
public sealed partial class ContactsViewModel : ObservableObject
{
    private readonly ContactService _contacts;
    private readonly MessageCommandService _commands;
    private readonly ToastService _toasts;
    private readonly DialogService _dialogs;
    private readonly IUiDispatcher _ui;

    public ContactsViewModel(ContactService contacts, MessageCommandService commands, ToastService toasts, DialogService dialogs, IUiDispatcher ui)
    {
        _contacts = contacts;
        _commands = commands;
        _toasts = toasts;
        _dialogs = dialogs;
        _ui = ui;
        SearchText = string.Empty;
        _contacts.Changed += (_, _) => _ui.Post(() => _ = RefreshAsync());
    }

    public ObservableCollection<ContactItemViewModel> Items { get; } = [];

    /// <summary>Provided by the page: shows the add/edit form and returns the edited contact, or null if cancelled.</summary>
    public Func<Contact?, Task<Contact?>>? EditContact { get; set; }

    [ObservableProperty] public partial string SearchText { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial string CountText { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value) => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        var all = await _contacts.ListAsync();
        var found = await _contacts.SearchAsync(SearchText);
        Items.Clear();
        foreach (var contact in found) Items.Add(new ContactItemViewModel(contact, this));
        IsEmpty = Items.Count == 0;
        CountText = all.Count switch
        {
            0 => "No contacts yet",
            1 => "1 contact",
            var n => $"{n} contacts",
        };
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (EditContact is null) return;
        var contact = await EditContact(null);
        if (contact is null) return;
        await SaveAsync(contact);
    }

    internal async Task EditAsync(ContactItemViewModel item)
    {
        if (EditContact is null) return;
        var edited = await EditContact(item.Contact);
        if (edited is null) return;
        await SaveAsync(edited);
    }

    private async Task SaveAsync(Contact contact)
    {
        try
        {
            var saved = await _contacts.SaveAsync(contact);
            _toasts.Show($"Saved {saved.DisplayName}.", ToastKind.Success);
        }
        catch (ArgumentException ex)
        {
            _toasts.Show(ex.Message, ToastKind.Warning);
        }
    }

    internal async Task DeleteAsync(ContactItemViewModel item)
    {
        var confirmed = await _dialogs.ConfirmAsync("Delete contact?", $"{item.DisplayName} will be removed from your contacts on this PC.", "Delete");
        if (!confirmed) return;
        await _contacts.DeleteAsync(item.Contact.Id);
        _toasts.ShowWithUndo($"Deleted {item.DisplayName}", async () => await _contacts.SaveAsync(item.Contact));
    }

    internal Task WriteToAsync(ContactItemViewModel item) =>
        item.Contact.PrimaryEmail is { } email
            ? _commands.ComposeNewAsync(null, new EmailAddress(item.DisplayName, email))
            : Task.CompletedTask;

    /// <summary>Imports a vCard (.vcf) file's text.</summary>
    public async Task ImportAsync(string text, string fileName)
    {
        try
        {
            var result = await _contacts.ImportVCardAsync(text);
            var parts = new List<string>();
            if (result.Added > 0) parts.Add($"{result.Added} added");
            if (result.Updated > 0) parts.Add($"{result.Updated} updated");
            if (result.Skipped > 0) parts.Add($"{result.Skipped} skipped (already here or no email address)");
            _toasts.Show(parts.Count == 0 ? $"No contacts found in \"{fileName}\"." : $"Imported \"{fileName}\": {string.Join(", ", parts)}.",
                result.Added + result.Updated > 0 ? ToastKind.Success : ToastKind.Info);
        }
        catch (InvalidDataException ex)
        {
            _toasts.Show(ex.Message, ToastKind.Error);
        }
    }
}
