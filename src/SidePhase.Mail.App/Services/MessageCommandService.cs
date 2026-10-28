using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Composition;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Scheduling;
using SidePhase.Mail.Core.Services;
using SidePhase.Mail.Core.Settings;

namespace SidePhase.Mail.App.Services;

/// <summary>
/// The message actions shared by the message list, context menus and the reading pane, including undo.
/// </summary>
public sealed class MessageCommandService(
    IMailDataSource mail,
    ISenderRulesService rules,
    ISettingsService settings,
    ToastService toasts,
    DialogService dialogs,
    WindowService windows,
    IClock clock)
{
    public Task ToggleStarAsync(MessageSummary message) => mail.SetStarredAsync([message.Id], !message.IsStarred);

    public Task TogglePinAsync(MessageSummary message) => mail.SetPinnedAsync([message.Id], !message.IsPinned);

    public Task SetReadAsync(MessageSummary message, bool isRead) => mail.SetReadAsync([message.Id], isRead);

    public async Task ArchiveAsync(MessageSummary message)
    {
        if (message.FolderKind == FolderKind.Archive)
        {
            toasts.Show("This message is already in Archive.");
            return;
        }

        var moves = await mail.MoveToAsync([message.Id], FolderKind.Archive);
        toasts.ShowWithUndo("Archived", () => mail.RestoreAsync(moves));
    }

    public async Task DeleteAsync(MessageSummary message)
    {
        if (message.FolderKind == FolderKind.Trash)
        {
            var confirmed = await dialogs.ConfirmAsync(
                "Delete permanently?",
                $"\"{message.DisplaySubject}\" will be permanently deleted. This can't be undone.",
                "Delete permanently");
            if (confirmed)
            {
                await mail.DeletePermanentlyAsync([message.Id]);
                toasts.Show("Message permanently deleted.");
            }

            return;
        }

        var moves = await mail.MoveToAsync([message.Id], FolderKind.Trash);
        toasts.ShowWithUndo("Moved to Trash", () => mail.RestoreAsync(moves));
    }

    public async Task MoveToJunkAsync(MessageSummary message)
    {
        var moves = await mail.MoveToAsync([message.Id], FolderKind.Junk);
        toasts.ShowWithUndo("Moved to Junk", () => mail.RestoreAsync(moves));
    }

    /// <summary>Trusts the sender and returns the message to the Inbox.</summary>
    public async Task NotJunkAsync(MessageSummary message)
    {
        var addedTrust = rules.AddRule(SenderRuleKind.TrustedSender, message.From.Address);
        IReadOnlyList<MoveRecord> moves = message.FolderKind == FolderKind.Junk
            ? await mail.MoveToAsync([message.Id], FolderKind.Inbox)
            : [];

        toasts.ShowWithUndo($"{message.From.DisplayName} is now a trusted sender.", async () =>
        {
            if (addedTrust) rules.RemoveRule(SenderRuleKind.TrustedSender, message.From.Address);
            if (moves.Count > 0) await mail.RestoreAsync(moves);
        });
    }

    /// <summary>
    /// Snoozes a message until a preset time ("LaterToday", "TomorrowMorning", "ThisWeekend", "NextWeek"), a chosen
    /// time ("Custom"), or brings it back now ("Clear"). Snoozing is stored on this PC only.
    /// </summary>
    public async Task SnoozeAsync(MessageSummary message, string choice)
    {
        if (choice == "Clear")
        {
            var previous = message.SnoozedUntil;
            await mail.SetSnoozeAsync([message.Id], null);
            toasts.ShowWithUndo("Unsnoozed", () => mail.SetSnoozeAsync([message.Id], previous));
            return;
        }

        var until = await ResolveTimeAsync(choice, "Snooze until", "Snooze", message.SnoozedUntil);
        if (until is null) return;

        var before = message.SnoozedUntil;
        await mail.SetSnoozeAsync([message.Id], until);
        toasts.ShowWithUndo($"Snoozed until {Describe(until.Value)}", () => mail.SetSnoozeAsync([message.Id], before));
    }

    /// <summary>
    /// Sets a follow-up reminder ("Tomorrow", "InThreeDays", "NextWeek", "Custom") or marks it done ("Clear").
    /// Reminders only notify you; they never change or send the message.
    /// </summary>
    public async Task FollowUpAsync(MessageSummary message, string choice)
    {
        var before = message.FollowUpAt;
        if (choice == "Clear")
        {
            await mail.SetFollowUpAsync([message.Id], null);
            toasts.ShowWithUndo("Follow-up done", () => mail.SetFollowUpAsync([message.Id], before));
            return;
        }

        var due = await ResolveTimeAsync(choice, "Remind me to follow up", "Set reminder", message.FollowUpAt);
        if (due is null) return;

        await mail.SetFollowUpAsync([message.Id], due);
        toasts.ShowWithUndo($"Reminder set for {Describe(due.Value)}", () => mail.SetFollowUpAsync([message.Id], before));
    }

    /// <summary>Turns a menu choice into an exact time, asking for a date and time when "Custom" is chosen.</summary>
    public async Task<DateTimeOffset?> ResolveTimeAsync(string choice, string title, string confirmText, DateTimeOffset? current = null, XamlRoot? root = null)
    {
        var now = clock.Now;
        if (choice == "Custom")
        {
            var suggested = current is { } c && c > now ? c : QuickTimes.Resolve(QuickTime.TomorrowMorning, now, TimeZoneInfo.Local);
            return await dialogs.PickDateTimeAsync(title, confirmText, suggested, root);
        }

        return Enum.TryParse<QuickTime>(choice, out var preset) ? QuickTimes.Resolve(preset, now, TimeZoneInfo.Local) : null;
    }

    public string Describe(DateTimeOffset when) => QuickTimes.Describe(when, clock.Now, TimeZoneInfo.Local);

    public async Task ReplyAsync(MessageSummary message, ComposeMode mode)
    {
        var accounts = await mail.GetAccountsAsync();
        var account = accounts.FirstOrDefault(a => a.Id == message.AccountId) ?? accounts.FirstOrDefault();
        if (account is null) return;

        var body = await mail.GetMessageBodyAsync(message.Id);
        windows.OpenCompose(ReplyBuilder.Create(mode, message, body, account));
    }

    public async Task EditDraftAsync(MessageSummary message)
    {
        var draft = await mail.GetDraftAsync(message.Id) ?? new DraftMessage
        {
            DraftId = message.Id,
            AccountId = message.AccountId,
            To = message.To,
            Cc = message.Cc,
            Subject = message.Subject,
            TextBody = message.Preview,
        };
        windows.OpenCompose(draft with { Mode = ComposeMode.EditDraft });
    }

    public async Task ComposeNewAsync(Guid? accountId = null, EmailAddress? to = null)
    {
        var accounts = await mail.GetAccountsAsync();
        var account = accounts.FirstOrDefault(a => a.Id == accountId) ?? accounts.FirstOrDefault();
        if (account is null)
        {
            toasts.Show("Add an account before writing a message.", ToastKind.Warning);
            return;
        }

        windows.OpenCompose(new DraftMessage
        {
            AccountId = account.Id,
            To = to is null ? [] : [to],
            IsPlainText = settings.Current.DefaultComposeFormat == ComposeFormat.PlainText,
        });
    }

    /// <summary>
    /// Saves an attachment where the user chooses. Attachments are never opened or run by SidePhase Mail.
    /// </summary>
    public async Task SaveAttachmentAsync(MessageSummary message, int index, AttachmentInfo attachment)
    {
        if (mail.IsDemo)
        {
            toasts.Show($"\"{attachment.FileName}\" is demonstration content, so there's no file to save.");
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
                SuggestedFileName = SafeFileName(attachment.FileName),
            };
            var extension = Path.GetExtension(picker.SuggestedFileName);
            picker.FileTypeChoices.Add(string.IsNullOrEmpty(extension) ? "File" : extension.TrimStart('.').ToUpperInvariant() + " file", [string.IsNullOrEmpty(extension) ? "." : extension]);
            if (App.Main is { } window) WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            toasts.Show($"Downloading \"{attachment.FileName}\"…");
            var content = await mail.GetAttachmentContentAsync(message.Id, index);
            await File.WriteAllBytesAsync(file.Path, content);
            toasts.Show($"Saved \"{file.Name}\".", ToastKind.Success);
        }
        catch (Exception ex)
        {
            toasts.Show($"Couldn't save \"{attachment.FileName}\": {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>Asks before opening a link from a message, showing exactly where it goes.</summary>
    public async Task OpenLinkAsync(Uri link)
    {
        var panel = new StackPanel { Spacing = 10, MaxWidth = 460 };
        panel.Children.Add(new TextBlock { Text = "This link opens in your web browser:", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = link.Host,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = link.AbsoluteUri,
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            MaxLines = 4,
        });
        if (link.Scheme == Uri.UriSchemeHttp)
        {
            panel.Children.Add(new TextBlock { Text = "This link isn't encrypted (http).", FontSize = 12, Opacity = 0.7 });
        }

        var dialog = new ContentDialog
        {
            Title = "Open link?",
            Content = panel,
            PrimaryButtonText = "Open link",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            await Windows.System.Launcher.LaunchUriAsync(link);
        }
    }

    private static string SafeFileName(string name)
    {
        var cleaned = string.Concat(Path.GetFileName(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "attachment" : cleaned;
    }
}
