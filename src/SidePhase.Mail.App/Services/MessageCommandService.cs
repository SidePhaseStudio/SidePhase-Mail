using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Composition;
using SidePhase.Mail.Core.Models;
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
    SendTheBuggerBackService buggerBack,
    ToastService toasts,
    DialogService dialogs,
    WindowService windows)
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

    public async Task SendTheBuggerBackAsync(MessageSummary message)
    {
        var sender = message.From;
        var canBlockDomain = SendTheBuggerBackService.CanBlockDomain(sender);

        var domainBox = new CheckBox
        {
            Content = $"Also block everything from @{sender.Domain}",
            IsChecked = canBlockDomain && settings.Current.BuggerBackBlocksDomainByDefault,
            IsEnabled = canBlockDomain,
        };

        var panel = new StackPanel { Spacing = 12, MaxWidth = 440 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"{sender.DisplayName} <{sender.Address}> will be blocked and this message moved to Junk. " +
                   "Anything else they send goes straight to Junk and won't notify you.",
        });
        panel.Children.Add(domainBox);
        if (!canBlockDomain)
        {
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                FontSize = 12,
                Text = $"@{sender.Domain} is a shared mail provider, so blocking the whole domain would block everyone who uses it.",
            });
        }

        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12,
            Text = "Nothing is sent to them — no fake bounces, no replies. Just a very satisfying click.",
        });

        var dialog = new ContentDialog
        {
            Title = "Send the Bugger Back!",
            Content = panel,
            PrimaryButtonText = "Send them packing",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        var result = await buggerBack.ExecuteAsync(message, new SendTheBuggerBackOptions(domainBox.IsChecked == true));
        toasts.ShowWithUndo(result.Summary, () => buggerBack.UndoAsync(result));
    }

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

    public void OpenAttachment(AttachmentInfo attachment)
    {
        if (mail.IsDemo)
        {
            toasts.Show($"\"{attachment.FileName}\" is demonstration content, so there's no file to open. " +
                        "Opening and saving attachments arrives with real accounts in Milestone 2.");
        }
        else
        {
            toasts.Show("Opening attachments is not implemented yet (Milestone 2).", ToastKind.Warning);
        }
    }
}
