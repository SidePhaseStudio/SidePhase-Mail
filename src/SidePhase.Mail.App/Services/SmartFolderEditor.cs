using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SidePhase.Mail.Core.Organisation;
using SidePhase.Mail.Core.Services;

namespace SidePhase.Mail.App.Services;

/// <summary>
/// The add/edit dialog for smart folders. Simple fields cover the common cases and write a search query, which can be
/// edited directly for anything else (it uses the same operators as the search box).
/// </summary>
public sealed class SmartFolderEditor(DialogService dialogs, SmartFolderService folders, ToastService toasts)
{
    private static readonly (string Label, string Value)[] Periods =
        [("Any time", ""), ("Last day", "1d"), ("Last 3 days", "3d"), ("Last week", "7d"), ("Last month", "30d"), ("Last 3 months", "90d"), ("Last year", "1y")];

    private static readonly (string Label, string Value)[] Sizes =
        [("Any size", ""), ("Over 1 MB", "1mb"), ("Over 5 MB", "5mb"), ("Over 10 MB", "10mb"), ("Over 25 MB", "25mb")];

    public async Task EditAsync(SmartFolder? existing)
    {
        var form = AdvancedSearch.FromQuery(existing?.Query);
        var name = new TextBox { Header = "Name", Text = existing?.Name ?? string.Empty, PlaceholderText = "e.g. From the bank" };
        var from = new TextBox { Header = "From (name, address or domain)", Text = form.From, IsSpellCheckEnabled = false };
        var words = new TextBox { Header = "Containing the words", Text = form.Words };
        var period = Combo("Received", Periods, form.NewerThan);
        var size = Combo("Size", Sizes, form.LargerThan);
        var attachments = new CheckBox { Content = "Has attachments", IsChecked = form.HasAttachment };
        var unread = new CheckBox { Content = "Unread only", IsChecked = form.Unread };
        var query = new TextBox
        {
            Header = "Search query",
            Text = existing?.Query ?? string.Empty,
            IsSpellCheckEnabled = false,
            PlaceholderText = "Built from the fields above, or type operators such as category:bills",
        };
        var hint = new TextBlock
        {
            Text = "A smart folder never moves mail; it just shows the messages stored on this PC that match. Junk and Trash are left out unless the query says in:junk or in:trash.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.75,
        };

        var updating = false;
        void Rebuild()
        {
            if (updating) return;
            updating = true;
            var current = AdvancedSearch.FromQuery(query.Text) with
            {
                From = from.Text,
                Words = words.Text,
                NewerThan = Value(period, Periods),
                LargerThan = Value(size, Sizes),
                HasAttachment = attachments.IsChecked == true,
                Unread = unread.IsChecked == true,
            };
            query.Text = current.ToQuery();
            updating = false;
        }

        from.TextChanged += (_, _) => Rebuild();
        words.TextChanged += (_, _) => Rebuild();
        period.SelectionChanged += (_, _) => Rebuild();
        size.SelectionChanged += (_, _) => Rebuild();
        attachments.Click += (_, _) => Rebuild();
        unread.Click += (_, _) => Rebuild();

        var checks = new Grid { ColumnSpacing = 10 };
        checks.ColumnDefinitions.Add(new ColumnDefinition());
        checks.ColumnDefinitions.Add(new ColumnDefinition());
        checks.Children.Add(attachments);
        Grid.SetColumn(unread, 1);
        checks.Children.Add(unread);

        var pickers = new Grid { ColumnSpacing = 10 };
        pickers.ColumnDefinitions.Add(new ColumnDefinition());
        pickers.ColumnDefinitions.Add(new ColumnDefinition());
        pickers.Children.Add(period);
        Grid.SetColumn(size, 1);
        pickers.Children.Add(size);

        var panel = new StackPanel { Spacing = 10, Width = 400 };
        foreach (var element in new UIElement[] { name, from, words, pickers, checks, query, hint }) panel.Children.Add(element);

        var dialog = new ContentDialog
        {
            Title = existing is null ? "New smart folder" : "Edit smart folder",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        try
        {
            await folders.SaveAsync(existing is null
                ? new SmartFolder { Id = Guid.NewGuid(), Name = name.Text, Query = query.Text }
                : existing with { Name = name.Text, Query = query.Text });
            toasts.Show($"Smart folder \"{name.Text.Trim()}\" saved.", ToastKind.Success);
        }
        catch (ArgumentException ex)
        {
            toasts.Show(ex.Message, ToastKind.Warning);
        }
    }

    public async Task DeleteAsync(SmartFolder folder)
    {
        var confirmed = await dialogs.ConfirmAsync(
            "Delete smart folder?",
            $"\"{folder.Name}\" will be removed from the sidebar. No messages are deleted or moved.",
            "Delete");
        if (!confirmed) return;
        await folders.DeleteAsync(folder.Id);
        toasts.ShowWithUndo($"Deleted \"{folder.Name}\"", async () => await folders.SaveAsync(folder));
    }

    private static ComboBox Combo(string header, (string Label, string Value)[] options, string current)
    {
        var combo = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (label, _) in options) combo.Items.Add(label);
        var index = Array.FindIndex(options, o => o.Value.Equals(current, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = index < 0 ? 0 : index;
        return combo;
    }

    private static string Value(ComboBox combo, (string Label, string Value)[] options) =>
        combo.SelectedIndex > 0 && combo.SelectedIndex < options.Length ? options[combo.SelectedIndex].Value : string.Empty;
}
