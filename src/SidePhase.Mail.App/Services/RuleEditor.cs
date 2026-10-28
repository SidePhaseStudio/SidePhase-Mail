using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Organisation;

namespace SidePhase.Mail.App.Services;

/// <summary>The add/edit dialog for mail rules: a few "when" rows and the actions to take.</summary>
public sealed class RuleEditor(DialogService dialogs, MailRuleService rules, IMailDataSource mail, ToastService toasts)
{
    private static readonly (RuleField Field, string Label, string Hint)[] Fields =
    [
        (RuleField.Sender, "Sender contains", "name or address"),
        (RuleField.SenderDomain, "Sender's domain is", "example.com"),
        (RuleField.Recipient, "Recipient contains", "name or address"),
        (RuleField.Subject, "Subject contains", "words"),
        (RuleField.Keywords, "Subject or preview has any of", "word, another word"),
        (RuleField.SpamScore, "Spam score is at least", "e.g. 5"),
        (RuleField.HasAttachment, "Has attachments", "yes or no"),
    ];

    /// <summary>Plain-language summary of a rule for the Settings list.</summary>
    public static string Describe(MailRule rule, IReadOnlyDictionary<Guid, string> folderNames)
    {
        var when = string.Join(rule.MatchAll ? " and " : " or ", rule.Conditions.Select(c =>
            c.Field == RuleField.HasAttachment
                ? (c.Value.Equals("no", StringComparison.OrdinalIgnoreCase) ? "no attachments" : "has attachments")
                : $"{Fields.First(f => f.Field == c.Field).Label.ToLowerInvariant()} “{c.Value}”"));
        var then = string.Join(", ", rule.Actions.Select(a => a.Kind switch
        {
            RuleActionKind.MoveToFolder => "move to " + (Guid.TryParse(a.Value, out var id) && folderNames.TryGetValue(id, out var name) ? name : "a folder"),
            RuleActionKind.MarkRead => "mark as read",
            RuleActionKind.Star => "add to favourites",
            RuleActionKind.AddCategory => $"add category “{a.Value}”",
            RuleActionKind.SuppressNotification => "don't notify",
            _ => string.Empty,
        }));
        return $"When {when}: {then}.";
    }

    public async Task EditAsync(MailRule? existing)
    {
        var accounts = (await mail.GetAccountsAsync()).Where(a => !a.IsDemo || mail.IsDemo).ToList();
        var folders = new List<(MailFolder Folder, MailAccount Account)>();
        foreach (var account in accounts)
        {
            foreach (var folder in await mail.GetFoldersAsync(account.Id)) folders.Add((folder, account));
        }

        var name = new TextBox { Header = "Name", Text = existing?.Name ?? string.Empty, PlaceholderText = "e.g. Newsletters" };
        var accountBox = new ComboBox { Header = "Applies to", HorizontalAlignment = HorizontalAlignment.Stretch };
        accountBox.Items.Add("Every account");
        foreach (var a in accounts) accountBox.Items.Add($"{a.DisplayName} ({a.EmailAddress})");
        accountBox.SelectedIndex = existing?.AccountId is { } aid ? accounts.FindIndex(a => a.Id == aid) + 1 : 0;

        var matchMode = new ComboBox { Header = "Match", HorizontalAlignment = HorizontalAlignment.Stretch };
        matchMode.Items.Add("All of these conditions");
        matchMode.Items.Add("Any of these conditions");
        matchMode.SelectedIndex = existing?.MatchAll == false ? 1 : 0;

        var conditionsPanel = new StackPanel { Spacing = 6 };
        void AddConditionRow(RuleCondition? condition)
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var field = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var f in Fields) field.Items.Add(f.Label);
            field.SelectedIndex = condition is null ? 0 : Array.FindIndex(Fields, f => f.Field == condition.Field);
            var value = new TextBox { Text = condition?.Value ?? string.Empty, IsSpellCheckEnabled = false };
            value.PlaceholderText = Fields[Math.Max(0, field.SelectedIndex)].Hint;
            field.SelectionChanged += (_, _) => value.PlaceholderText = Fields[Math.Max(0, field.SelectedIndex)].Hint;
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 11 }, Padding = new Thickness(8, 6, 8, 6) };
            ToolTipService.SetToolTip(remove, "Remove condition");
            remove.Click += (_, _) => conditionsPanel.Children.Remove(row);
            Grid.SetColumn(value, 1);
            Grid.SetColumn(remove, 2);
            row.Children.Add(field);
            row.Children.Add(value);
            row.Children.Add(remove);
            row.Tag = (field, value);
            conditionsPanel.Children.Add(row);
        }

        foreach (var c in existing?.Conditions ?? [new RuleCondition(RuleField.Sender, string.Empty)]) AddConditionRow(c);
        var addCondition = new HyperlinkButton { Content = "Add a condition" };
        addCondition.Click += (_, _) => { if (conditionsPanel.Children.Count < 8) AddConditionRow(null); };

        var actions = existing?.Actions ?? [];
        var moveFolder = actions.FirstOrDefault(a => a.Kind == RuleActionKind.MoveToFolder)?.Value;
        var move = new CheckBox { Content = "Move to folder", IsChecked = moveFolder is not null };
        var folderBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a folder" };
        foreach (var (f, a) in folders) folderBox.Items.Add(accounts.Count > 1 ? $"{a.DisplayName} · {f.Name}" : f.Name);
        folderBox.SelectedIndex = folders.FindIndex(f => f.Folder.Id.ToString() == moveFolder);
        var markRead = new CheckBox { Content = "Mark as read", IsChecked = actions.Any(a => a.Kind == RuleActionKind.MarkRead) };
        var star = new CheckBox { Content = "Add to favourites", IsChecked = actions.Any(a => a.Kind == RuleActionKind.Star) };
        var categoryValue = actions.FirstOrDefault(a => a.Kind == RuleActionKind.AddCategory)?.Value;
        var category = new CheckBox { Content = "Add a category", IsChecked = categoryValue is not null };
        var categoryName = new TextBox { Text = categoryValue ?? string.Empty, PlaceholderText = "e.g. Bills" };
        var silence = new CheckBox { Content = "Don't show a notification", IsChecked = actions.Any(a => a.Kind == RuleActionKind.SuppressNotification) };
        var stop = new CheckBox { Content = "Stop processing more rules", IsChecked = existing?.StopProcessing == true };
        var enabled = new ToggleSwitch { Header = "Rule is on", IsOn = existing?.Enabled ?? true };

        var panel = new StackPanel { Spacing = 10, Width = 460 };
        panel.Children.Add(name);
        panel.Children.Add(Pair(accountBox, matchMode));
        panel.Children.Add(new TextBlock { Text = "When a new message arrives and…", FontSize = 13 });
        panel.Children.Add(conditionsPanel);
        panel.Children.Add(addCondition);
        panel.Children.Add(new TextBlock { Text = "Do this", FontSize = 13, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(Pair(move, folderBox));
        panel.Children.Add(Pair(markRead, star));
        panel.Children.Add(Pair(category, categoryName));
        panel.Children.Add(Pair(silence, stop));
        panel.Children.Add(enabled);
        panel.Children.Add(new TextBlock
        {
            Text = "Rules run on new mail in the Inbox, once per message, and never delete anything. You can also run a rule on a folder from Settings › Rules.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.75,
        });

        var dialog = new ContentDialog
        {
            Title = existing is null ? "New rule" : "Edit rule",
            Content = new ScrollViewer { Content = panel, MaxHeight = 600 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        while (true)
        {
            if (await dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

            var chosenActions = new List<RuleAction>();
            if (move.IsChecked == true && folderBox.SelectedIndex >= 0) chosenActions.Add(new RuleAction(RuleActionKind.MoveToFolder, folders[folderBox.SelectedIndex].Folder.Id.ToString()));
            if (move.IsChecked == true && folderBox.SelectedIndex < 0) chosenActions.Add(new RuleAction(RuleActionKind.MoveToFolder, null));
            if (markRead.IsChecked == true) chosenActions.Add(new RuleAction(RuleActionKind.MarkRead));
            if (star.IsChecked == true) chosenActions.Add(new RuleAction(RuleActionKind.Star));
            if (category.IsChecked == true) chosenActions.Add(new RuleAction(RuleActionKind.AddCategory, categoryName.Text.Trim()));
            if (silence.IsChecked == true) chosenActions.Add(new RuleAction(RuleActionKind.SuppressNotification));

            var conditions = conditionsPanel.Children.OfType<Grid>()
                .Select(r => ((ComboBox Field, TextBox Value))r.Tag)
                .Where(t => t.Field.SelectedIndex >= 0)
                .Select(t => new RuleCondition(Fields[t.Field.SelectedIndex].Field, t.Value.Text.Trim()))
                .ToList();

            var rule = (existing ?? new MailRule { Id = Guid.NewGuid(), Name = string.Empty }) with
            {
                Name = name.Text,
                AccountId = accountBox.SelectedIndex > 0 ? accounts[accountBox.SelectedIndex - 1].Id : null,
                MatchAll = matchMode.SelectedIndex != 1,
                Conditions = conditions,
                Actions = chosenActions,
                StopProcessing = stop.IsChecked == true,
                Enabled = enabled.IsOn,
            };

            try
            {
                await rules.SaveAsync(rule);
                toasts.Show($"Rule \"{rule.Name.Trim()}\" saved.", ToastKind.Success);
                return;
            }
            catch (ArgumentException ex)
            {
                // Show the problem and keep the dialog's contents for another try.
                toasts.Show(ex.Message, ToastKind.Warning);
            }
        }
    }

    private static Grid Pair(UIElement left, UIElement right)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(left);
        Grid.SetColumn((FrameworkElement)right, 1);
        grid.Children.Add(right);
        return grid;
    }
}
