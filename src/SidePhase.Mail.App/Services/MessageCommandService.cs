using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Composition;
using SidePhase.Mail.Core.Formatting;
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
    IClock clock,
    Core.Spam.LocalSpamFilter localFilter)
{
    /// <summary>Teaches the local filter from an explicit Junk / Not junk choice (real accounts only).</summary>
    private void Learn(IEnumerable<MessageSummary> messages, bool isSpam)
    {
        if (mail.IsDemo || !settings.Current.LocalSpamLearning) return;
        foreach (var message in messages.ToList()) _ = LearnAsync(message, isSpam);
    }

    private async Task LearnAsync(MessageSummary message, bool isSpam)
    {
        try
        {
            await localFilter.TrainAsync(message, isSpam);
        }
        catch (Exception)
        {
            // Learning is a convenience; it must never get in the way of moving the message.
        }
    }

    public Task ToggleStarAsync(MessageSummary message) => mail.SetStarredAsync([message.Id], !message.IsStarred);

    public Task TogglePinAsync(MessageSummary message) => mail.SetPinnedAsync([message.Id], !message.IsPinned);

    public Task SetReadAsync(MessageSummary message, bool isRead) => SetReadAsync([message], isRead);

    public Task SetReadAsync(IReadOnlyList<MessageSummary> messages, bool isRead) =>
        mail.SetReadAsync(messages.Where(m => m.IsRead != isRead).Select(m => m.Id).ToList(), isRead);

    /// <summary>Marks many messages read or unread, with undo.</summary>
    public async Task SetReadWithUndoAsync(IReadOnlyList<MessageSummary> messages, bool isRead)
    {
        var changed = messages.Where(m => m.IsRead != isRead).Select(m => m.Id).ToList();
        if (changed.Count == 0) return;
        await mail.SetReadAsync(changed, isRead);
        toasts.ShowWithUndo($"Marked {changed.Count} as {(isRead ? "read" : "unread")}", () => mail.SetReadAsync(changed, !isRead));
    }

    /// <summary>Adds many messages to (or removes them from) favourites, with undo.</summary>
    public async Task SetStarredWithUndoAsync(IReadOnlyList<MessageSummary> messages, bool isStarred)
    {
        var changed = messages.Where(m => m.IsStarred != isStarred).Select(m => m.Id).ToList();
        if (changed.Count == 0) return;
        await mail.SetStarredAsync(changed, isStarred);
        toasts.ShowWithUndo(isStarred ? $"Added {changed.Count} to favourites" : $"Removed {changed.Count} from favourites", () => mail.SetStarredAsync(changed, !isStarred));
    }

    /// <summary>Moves messages to a folder (of their own account), showing progress for big batches, with undo.</summary>
    public async Task MoveToFolderAsync(IReadOnlyList<MessageSummary> messages, MailFolder folder)
    {
        var targets = messages.Where(m => m.AccountId == folder.AccountId && m.FolderId != folder.Id).Select(m => m.Id).ToList();
        var otherAccounts = messages.Count(m => m.AccountId != folder.AccountId);
        if (targets.Count == 0)
        {
            toasts.Show(otherAccounts > 0 ? "Messages can only be moved to folders of their own account." : $"Already in {folder.Name}.");
            return;
        }

        var lastShown = -1;
        var progress = new Progress<double>(p =>
        {
            var percent = (int)(p * 100);
            if (targets.Count < 50 || percent - lastShown < 10) return;
            lastShown = percent;
            toasts.Show($"Moving {targets.Count} messages to {folder.Name}… {percent}%");
        });
        if (targets.Count >= 50) toasts.Show($"Moving {targets.Count} messages to {folder.Name}…");

        var moves = await mail.MoveToFolderAsync(targets, folder.Id, progress);
        var note = otherAccounts > 0 ? $" ({otherAccounts} from other accounts stayed where they were)" : string.Empty;
        toasts.ShowWithUndo(moves.Count == 1 ? $"Moved to {folder.Name}{note}" : $"Moved {moves.Count} messages to {folder.Name}{note}", () => mail.RestoreAsync(moves));
    }

    public Task ArchiveAsync(MessageSummary message) => ArchiveAsync([message]);

    /// <summary>Archives one message or a whole conversation.</summary>
    public async Task ArchiveAsync(IReadOnlyList<MessageSummary> messages)
    {
        var targets = messages.Where(m => m.FolderKind != FolderKind.Archive).Select(m => m.Id).ToList();
        if (targets.Count == 0)
        {
            toasts.Show(messages.Count == 1 ? "This message is already in Archive." : "These messages are already in Archive.");
            return;
        }

        var moves = await mail.MoveToAsync(targets, FolderKind.Archive);
        toasts.ShowWithUndo(targets.Count == 1 ? "Archived" : $"Archived {targets.Count} messages", () => mail.RestoreAsync(moves));
    }

    public Task DeleteAsync(MessageSummary message) => DeleteAsync([message]);

    /// <summary>Moves to Trash, or deletes permanently (after asking) what is already in Trash.</summary>
    public async Task DeleteAsync(IReadOnlyList<MessageSummary> messages)
    {
        var inTrash = messages.Where(m => m.FolderKind == FolderKind.Trash).ToList();
        if (inTrash.Count > 0 && inTrash.Count == messages.Count)
        {
            var confirmed = await dialogs.ConfirmAsync(
                "Delete permanently?",
                inTrash.Count == 1
                    ? $"\"{inTrash[0].DisplaySubject}\" will be permanently deleted. This can't be undone."
                    : $"{inTrash.Count} messages will be permanently deleted. This can't be undone.",
                "Delete permanently");
            if (confirmed)
            {
                await mail.DeletePermanentlyAsync(inTrash.Select(m => m.Id).ToList());
                toasts.Show(inTrash.Count == 1 ? "Message permanently deleted." : "Messages permanently deleted.");
            }

            return;
        }

        var targets = messages.Where(m => m.FolderKind != FolderKind.Trash).Select(m => m.Id).ToList();
        var moves = await mail.MoveToAsync(targets, FolderKind.Trash);
        toasts.ShowWithUndo(targets.Count == 1 ? "Moved to Trash" : $"Moved {targets.Count} messages to Trash", () => mail.RestoreAsync(moves));
    }

    public Task MoveToJunkAsync(MessageSummary message) => MoveToJunkAsync([message]);

    public async Task MoveToJunkAsync(IReadOnlyList<MessageSummary> messages)
    {
        Learn(messages, isSpam: true);
        var moves = await mail.MoveToAsync(messages.Select(m => m.Id).ToList(), FolderKind.Junk);
        toasts.ShowWithUndo("Moved to Junk", () => mail.RestoreAsync(moves));
    }

    /// <summary>Trusts the sender and returns the message to the Inbox.</summary>
    public async Task NotJunkAsync(MessageSummary message)
    {
        Learn([message], isSpam: false);
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

    /// <summary>Edits a message's local categories (comma-separated). Categories stay on this PC.</summary>
    public async Task EditCategoriesAsync(MessageSummary message)
    {
        var box = new TextBox
        {
            Header = "Categories",
            Text = string.Join(", ", message.Categories),
            PlaceholderText = "e.g. Bills, Family",
            MinWidth = 320,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = "Separate categories with commas. Find them later with category:name in search or a smart folder. They're kept on this PC.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
            Opacity = 0.75,
        });
        var dialog = new ContentDialog
        {
            Title = "Edit categories",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        var wanted = box.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.Length > 40 ? c[..40] : c)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var removed in message.Categories.Where(c => !wanted.Contains(c, StringComparer.CurrentCultureIgnoreCase)))
        {
            await mail.SetCategoryAsync([message.Id], removed, add: false);
        }

        foreach (var added in wanted.Where(c => !message.Categories.Contains(c, StringComparer.CurrentCultureIgnoreCase)))
        {
            await mail.SetCategoryAsync([message.Id], added, add: true);
        }
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

    /// <summary>
    /// Opens a new message from a mailto: link (a link in a message, or Windows when SidePhase Mail is the default
    /// email app). Only recipients, subject and body are used; nothing is sent until the user presses Send.
    /// </summary>
    public async Task ComposeFromLinkAsync(Core.Composition.MailtoLink link)
    {
        var accounts = await mail.GetAccountsAsync();
        var account = accounts.FirstOrDefault(a => a.Id == settings.Current.DefaultSendingAccountId) ?? accounts.FirstOrDefault();
        if (account is null)
        {
            toasts.Show("Add an account before writing a message.", ToastKind.Warning);
            return;
        }

        windows.OpenCompose(new DraftMessage
        {
            AccountId = account.Id,
            To = link.To,
            Cc = link.Cc,
            Bcc = link.Bcc,
            Subject = link.Subject,
            TextBody = link.Body,
            IsPlainText = settings.Current.DefaultComposeFormat == ComposeFormat.PlainText,
        });
    }

    public async Task ComposeNewAsync(Guid? accountId = null, EmailAddress? to = null)
    {
        var accounts = await mail.GetAccountsAsync();
        var account = accounts.FirstOrDefault(a => a.Id == accountId)
            ?? accounts.FirstOrDefault(a => a.Id == settings.Current.DefaultSendingAccountId)
            ?? accounts.FirstOrDefault();
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
    /// Saves an attachment where the user chooses, streaming it to disk with progress. Attachments are never opened or
    /// run by SidePhase Mail; files that could run code are only saved after a clear warning.
    /// </summary>
    public async Task SaveAttachmentAsync(MessageSummary message, int index, AttachmentInfo attachment)
    {
        if (mail.IsDemo)
        {
            toasts.Show($"\"{attachment.FileName}\" is demonstration content, so there's no file to save.");
            return;
        }

        if (!await ConfirmDangerousAsync([attachment])) return;

        string path;
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
                SuggestedFileName = AttachmentSafety.SafeFileName(attachment.FileName),
            };
            var extension = Path.GetExtension(picker.SuggestedFileName);
            picker.FileTypeChoices.Add(string.IsNullOrEmpty(extension) ? "File" : extension.TrimStart('.').ToUpperInvariant() + " file", [string.IsNullOrEmpty(extension) ? "." : extension]);
            InitializePicker(picker);

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            path = file.Path;
        }
        catch (Exception ex)
        {
            toasts.Show($"Couldn't choose where to save \"{attachment.FileName}\": {ex.Message}", ToastKind.Error);
            return;
        }

        if (await DownloadToAsync(message, index, attachment, path, overwrite: true))
        {
            toasts.Show($"Saved \"{Path.GetFileName(path)}\".", ToastKind.Success);
        }
    }

    /// <summary>Saves every attachment into a chosen folder. Existing files are never overwritten: names get " (2)" etc.</summary>
    public async Task SaveAllAttachmentsAsync(MessageSummary message, IReadOnlyList<AttachmentInfo> attachments)
    {
        if (attachments.Count == 0) return;
        if (mail.IsDemo)
        {
            toasts.Show("These are demonstration attachments, so there are no files to save.");
            return;
        }

        if (!await ConfirmDangerousAsync(attachments)) return;

        string folder;
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads };
            picker.FileTypeFilter.Add("*");
            InitializePicker(picker);
            var chosen = await picker.PickSingleFolderAsync();
            if (chosen is null) return;
            folder = chosen.Path;
        }
        catch (Exception ex)
        {
            toasts.Show("Couldn't choose a folder: " + ex.Message, ToastKind.Error);
            return;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var saved = 0;
        for (var i = 0; i < attachments.Count; i++)
        {
            var name = AttachmentSafety.UniqueName(
                AttachmentSafety.SafeFileName(attachments[i].FileName),
                n => used.Contains(n) || File.Exists(Path.Combine(folder, n)));
            used.Add(name);
            if (await DownloadToAsync(message, i, attachments[i], Path.Combine(folder, name), overwrite: false)) saved++;
        }

        toasts.Show(
            saved == attachments.Count ? $"Saved {saved} attachments to {Path.GetFileName(folder)}." : $"Saved {saved} of {attachments.Count} attachments.",
            saved == attachments.Count ? ToastKind.Success : ToastKind.Warning);
    }

    /// <summary>Previewable types: ordinary images and plain text. Never scripts, SVG (which can contain code) or HTML.</summary>
    public static bool CanPreview(AttachmentInfo attachment) =>
        (attachment.ContentType.ToLowerInvariant() is "image/png" or "image/jpeg" or "image/jpg" or "image/gif" or "image/bmp" or "image/webp"
            && attachment.SizeBytes <= MaxImagePreviewBytes)
        || (attachment.ContentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase) && attachment.SizeBytes <= MaxTextPreviewBytes);

    private const long MaxImagePreviewBytes = 20L * 1024 * 1024;
    private const long MaxTextPreviewBytes = 1024 * 1024;

    /// <summary>Shows an image or text attachment inside the app. Nothing is written to disk or run.</summary>
    public async Task PreviewAttachmentAsync(MessageSummary message, int index, AttachmentInfo attachment)
    {
        if (mail.IsDemo)
        {
            toasts.Show($"\"{attachment.FileName}\" is demonstration content, so there's nothing to preview.");
            return;
        }

        if (!CanPreview(attachment))
        {
            await SaveAttachmentAsync(message, index, attachment);
            return;
        }

        try
        {
            var isText = attachment.ContentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);
            using var buffer = new LimitedMemoryStream(isText ? MaxTextPreviewBytes : MaxImagePreviewBytes);
            await mail.SaveAttachmentAsync(message.Id, index, buffer);
            buffer.Position = 0;

            UIElement content;
            if (isText)
            {
                using var reader = new StreamReader(buffer, detectEncodingFromByteOrderMarks: true);
                content = new TextBlock
                {
                    Text = await reader.ReadToEndAsync(),
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                };
            }
            else
            {
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bitmap.SetSourceAsync(buffer.AsRandomAccessStream());
                content = new Image { Source = bitmap, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, MaxHeight = 640 };
            }

            var dialog = new ContentDialog
            {
                Title = attachment.FileName,
                Content = new ScrollViewer { Content = content, MaxHeight = 660, MinWidth = 420, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = "Save…",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialogs.ShowAsync(dialog) == ContentDialogResult.Primary) await SaveAttachmentAsync(message, index, attachment);
        }
        catch (Exception ex)
        {
            toasts.Show($"Couldn't preview \"{attachment.FileName}\": {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>Warns before saving files that could run code. Returns false if the user backs out.</summary>
    private async Task<bool> ConfirmDangerousAsync(IReadOnlyList<AttachmentInfo> attachments)
    {
        var risky = attachments.Where(a => AttachmentSafety.IsDangerous(a.FileName)).ToList();
        if (risky.Count == 0) return true;

        var disguised = risky.Any(a => AttachmentSafety.HasMisleadingName(a.FileName));
        var names = string.Join(", ", risky.Take(4).Select(a => $"\"{a.FileName}\""));
        var message = $"{names} can run programs, scripts or macros on your PC."
            + (disguised ? " The name is made to look like a harmless document, which is a common trick in harmful email." : string.Empty)
            + " Only open it if you were expecting it from someone you trust. SidePhase Mail won't open it for you.";
        return await dialogs.ConfirmAsync("This file could harm your PC", message, "Save anyway");
    }

    /// <summary>Streams one attachment to <paramref name="path"/>, showing progress for large files. Removes a partial file on failure.</summary>
    private async Task<bool> DownloadToAsync(MessageSummary message, int index, AttachmentInfo attachment, string path, bool overwrite)
    {
        var lastReport = DateTimeOffset.MinValue;
        var progress = new Progress<double>(p =>
        {
            if (attachment.SizeBytes < 2 * 1024 * 1024 || DateTimeOffset.UtcNow - lastReport < TimeSpan.FromSeconds(1)) return;
            lastReport = DateTimeOffset.UtcNow;
            toasts.Show($"Downloading \"{attachment.FileName}\"… {p:P0}");
        });

        var created = false;
        try
        {
            if (attachment.SizeBytes >= 2 * 1024 * 1024) toasts.Show($"Downloading \"{attachment.FileName}\"…");
            await using var file = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            created = true;
            await mail.SaveAttachmentAsync(message.Id, index, file, progress);
            return true;
        }
        catch (Exception ex)
        {
            if (created) TryDelete(path);
            toasts.Show($"Couldn't save \"{attachment.FileName}\": {ex.Message}", ToastKind.Error);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void InitializePicker(object picker)
    {
        if (App.Main is { } window) WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
    }

    /// <summary>A memory buffer that refuses to grow past a limit (for previews of untrusted content).</summary>
    private sealed class LimitedMemoryStream(long limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > limit) throw new InvalidOperationException("The attachment is too large to preview.");
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > limit) throw new InvalidOperationException("The attachment is too large to preview.");
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            if (Length + 1 > limit) throw new InvalidOperationException("The attachment is too large to preview.");
            base.WriteByte(value);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
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
}
