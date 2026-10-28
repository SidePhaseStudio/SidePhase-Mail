using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SidePhase.Mail.App.Helpers;
using SidePhase.Mail.App.Models;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Formatting;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Rendering;
using SidePhase.Mail.Core.Services;
using SidePhase.Mail.Core.Settings;
using SidePhase.Mail.Core.Spam;

namespace SidePhase.Mail.App.ViewModels;

/// <summary>The reading pane: header, banners, attachments and the (lazily loaded) message body.</summary>
public sealed partial class ReadingPaneViewModel : ObservableObject
{
    private readonly IMailDataSource _mail;
    private readonly MessageCommandService _commands;
    private readonly ISettingsService _settings;
    private readonly ISenderRulesService _rules;
    private readonly JunkPolicy _junk;
    private readonly IClock _clock;
    private readonly HashSet<Guid> _remoteAllowed = [];
    private readonly Dictionary<Guid, MailAccount> _accounts = [];
    private readonly Dictionary<Guid, MailFolder> _folders = [];
    private CancellationTokenSource? _loadCts;
    private MessageBody? _body;

    public ReadingPaneViewModel(
        IMailDataSource mail,
        MessageCommandService commands,
        ISettingsService settings,
        ISenderRulesService rules,
        JunkPolicy junk,
        IClock clock)
    {
        _mail = mail;
        _commands = commands;
        _settings = settings;
        _rules = rules;
        _junk = junk;
        _clock = clock;

        Subject = SenderName = SenderAddress = Initials = DateText = ToLine = CcLine = string.Empty;
        AccountName = FolderName = SpamTitle = SpamDetail = SpamActionText = SpamCheckSummary = SpamCheckDetail = string.Empty;
        AccountColor = "#8B7CFF";
        _mail.DataChanged += (_, e) =>
        {
            if (e.Kind == MailDataChangeKind.Reset)
            {
                _accounts.Clear();
                _folders.Clear();
            }
        };
    }

    public ObservableCollection<AttachmentItemViewModel> Attachments { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage), nameof(StarGlyph), nameof(StarLabel), nameof(PinGlyph), nameof(PinLabel),
        nameof(ReadLabel), nameof(IsDraft), nameof(IsInJunk), nameof(JunkLabel), nameof(JunkGlyph), nameof(CanReply))]
    public partial MessageSummary? Message { get; set; }

    public bool HasMessage => Message is not null;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial EmailContent? Content { get; set; }

    [ObservableProperty] public partial string Subject { get; set; }
    [ObservableProperty] public partial string SenderName { get; set; }
    [ObservableProperty] public partial string SenderAddress { get; set; }
    [ObservableProperty] public partial string Initials { get; set; }
    [ObservableProperty] public partial int AvatarIndex { get; set; }
    [ObservableProperty] public partial string DateText { get; set; }
    [ObservableProperty] public partial string ToLine { get; set; }
    [ObservableProperty] public partial string CcLine { get; set; }
    [ObservableProperty] public partial string AccountName { get; set; }
    [ObservableProperty] public partial string AccountColor { get; set; }
    [ObservableProperty] public partial string FolderName { get; set; }
    [ObservableProperty] public partial bool HasAttachments { get; set; }
    [ObservableProperty] public partial bool HasSeveralAttachments { get; set; }
    [ObservableProperty] public partial string AttachmentsSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ShowRemoteContentBanner { get; set; }
    [ObservableProperty] public partial bool ShowSpamBanner { get; set; }
    [ObservableProperty] public partial bool ShowFollowUpBanner { get; set; }
    [ObservableProperty] public partial bool FollowUpIsOverdue { get; set; }
    [ObservableProperty] public partial string FollowUpText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ShowSnoozeBanner { get; set; }
    [ObservableProperty] public partial string SnoozeText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsSnoozed { get; set; }
    [ObservableProperty] public partial bool SpamIsSevere { get; set; }
    [ObservableProperty] public partial string SpamTitle { get; set; }
    [ObservableProperty] public partial string SpamDetail { get; set; }
    [ObservableProperty] public partial string SpamActionText { get; set; }

    /// <summary>One-line summary of the SpamAssassin check shown in the details expander header.</summary>
    [ObservableProperty] public partial string SpamCheckSummary { get; set; }

    /// <summary>Where the result came from and how far it can be trusted.</summary>
    [ObservableProperty] public partial string SpamCheckDetail { get; set; }

    public System.Collections.ObjectModel.ObservableCollection<string> SpamRules { get; } = [];

    public bool IsDraft => Message?.FolderKind == FolderKind.Drafts;
    public bool CanReply => Message is not null && !IsDraft;
    public bool IsInJunk => Message is not null && (Message.FolderKind == FolderKind.Junk || _junk.IsLocallyJunk(Message));
    public string StarGlyph => Message?.IsStarred == true ? Glyphs.StarFilled : Glyphs.Star;
    public string StarLabel => Message?.IsStarred == true ? "Remove from favourites" : "Add to favourites";
    public string PinGlyph => Message?.IsPinned == true ? Glyphs.Unpin : Glyphs.Pin;
    public string PinLabel => Message?.IsPinned == true ? "Unpin" : "Pin to top";
    public string ReadLabel => Message?.IsRead == false ? "Mark as read" : "Mark as unread";
    public string JunkLabel => IsInJunk ? "Not junk" : "Move to Junk";
    public string JunkGlyph => IsInJunk ? Glyphs.Accept : Glyphs.Junk;

    /// <summary>The other messages in the open message's conversation (conversation view), oldest first.</summary>
    public ObservableCollection<ThreadEntryViewModel> ThreadItems { get; } = [];

    [ObservableProperty] public partial bool HasThread { get; set; }
    [ObservableProperty] public partial string ThreadSummary { get; set; } = string.Empty;

    /// <summary>Raised when a message in the conversation list is chosen, so the message list can select it.</summary>
    public event EventHandler<Guid>? ThreadMessageRequested;

    public Task ShowAsync(MessageSummary? message) => ShowAsync(message, null);

    public async Task ShowAsync(MessageSummary? message, IReadOnlyList<MessageSummary>? thread)
    {
        _loadCts?.Cancel();
        _loadCts = null;
        UpdateThread(message, thread);

        if (message is null)
        {
            Message = null;
            Content = null;
            Attachments.Clear();
            _body = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;

        Message = message;
        await ApplyHeaderAsync(message);
        Content = null;
        Attachments.Clear();
        HasAttachments = message.HasAttachments;
        HasSeveralAttachments = false;
        AttachmentsSummary = string.Empty;
        ShowRemoteContentBanner = false;
        IsLoading = true;

        try
        {
            var body = await _mail.GetMessageBodyAsync(message.Id, cts.Token);
            if (cts.IsCancellationRequested) return;
            _body = body;
            var index = 0;
            var all = body?.Attachments ?? [];
            foreach (var attachment in all)
            {
                var attachmentIndex = index++;
                Attachments.Add(new AttachmentItemViewModel(
                    attachment,
                    a => _commands.PreviewAttachmentAsync(message, attachmentIndex, a),
                    a => _commands.SaveAttachmentAsync(message, attachmentIndex, a),
                    () => _commands.SaveAllAttachmentsAsync(message, all)));
            }

            HasAttachments = Attachments.Count > 0;
            HasSeveralAttachments = Attachments.Count > 1;
            AttachmentsSummary = Attachments.Count switch
            {
                0 => string.Empty,
                1 => $"1 attachment · {FileSizeFormatter.Format(all.Sum(a => a.SizeBytes))}",
                var n => $"{n} attachments · {FileSizeFormatter.Format(all.Sum(a => a.SizeBytes))}",
            };
            RenderBody();
        }
        catch (OperationCanceledException)
        {
            // A newer selection replaced this one.
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) IsLoading = false;
        }
    }

    /// <summary>Refreshes the header after flags change, without reloading the body.</summary>
    public void UpdateSummary(MessageSummary message)
    {
        if (Message is null || Message.Id != message.Id || Message == message) return;
        Message = message;
        _ = ApplyHeaderAsync(message);
    }

    private void UpdateThread(MessageSummary? message, IReadOnlyList<MessageSummary>? thread)
    {
        ThreadItems.Clear();
        if (message is null || thread is not { Count: > 1 })
        {
            HasThread = false;
            return;
        }

        var now = _clock.Now;
        foreach (var m in thread)
        {
            ThreadItems.Add(new ThreadEntryViewModel(m, m.Id == message.Id, now, id => ThreadMessageRequested?.Invoke(this, id)));
        }

        var people = thread.Select(m => m.From.Address).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var attachments = thread.Count(m => m.HasAttachments);
        ThreadSummary = $"{thread.Count} messages in this conversation · {people} {(people == 1 ? "person" : "people")}"
            + (attachments > 0 ? $" · {attachments} with attachments" : string.Empty);
        HasThread = true;
    }

    private void RenderBody()
    {
        if (Message is null) return;
        var html = _body?.HtmlBody;
        var hasRemote = HtmlEmailDocumentBuilder.ContainsRemoteContent(html);
        var allowRemote = !_settings.Current.BlockRemoteImages || _remoteAllowed.Contains(Message.Id);
        ShowRemoteContentBanner = hasRemote && !allowRemote;
        Content = new EmailContent(Message.Id, html, _body?.TextBody ?? Message.Preview, allowRemote);
    }

    private async Task ApplyHeaderAsync(MessageSummary m)
    {
        Subject = m.DisplaySubject;
        SenderName = m.From.DisplayName;
        SenderAddress = m.From.Address;
        Initials = AvatarHelper.Initials(m.From);
        AvatarIndex = AvatarHelper.ColorIndex(m.From.Address);
        DateText = DateDisplayFormatter.ForReadingPane(m.Date, _clock.Now);
        ToLine = m.To.Count == 0 ? string.Empty : "To " + string.Join(", ", m.To.Select(a => a.DisplayName));
        CcLine = m.Cc.Count == 0 ? string.Empty : "Cc " + string.Join(", ", m.Cc.Select(a => a.DisplayName));

        if (!_accounts.ContainsKey(m.AccountId))
        {
            foreach (var account in await _mail.GetAccountsAsync())
            {
                _accounts[account.Id] = account;
                foreach (var folder in await _mail.GetFoldersAsync(account.Id)) _folders[folder.Id] = folder;
            }
        }

        if (_accounts.TryGetValue(m.AccountId, out var acct))
        {
            AccountName = acct.DisplayName;
            AccountColor = acct.ColorHex ?? "#8B7CFF";
        }

        FolderName = _junk.IsLocallyJunk(m) ? "Junk (filtered)" : _folders.TryGetValue(m.FolderId, out var f) ? f.Name : string.Empty;
        UpdateSpamBanner(m);
        UpdateReminderBanners(m);
    }

    private void UpdateReminderBanners(MessageSummary m)
    {
        var now = _clock.Now;
        IsSnoozed = m.SnoozedUntil is not null;
        ShowSnoozeBanner = m.SnoozedUntil is not null;
        SnoozeText = m.SnoozedUntil is { } until
            ? $"Snoozed until {_commands.Describe(until)}. It returns to its folder then."
            : string.Empty;

        ShowFollowUpBanner = m.FollowUpAt is not null;
        FollowUpIsOverdue = m.FollowUpAt is { } due && due <= now;
        FollowUpText = m.FollowUpAt is { } at
            ? FollowUpIsOverdue ? $"Follow-up overdue (was due {_commands.Describe(at)})." : $"Follow up {_commands.Describe(at)}."
            : string.Empty;
    }

    private void UpdateSpamBanner(MessageSummary m)
    {
        var threshold = _settings.Current.SpamThreshold;
        var verdict = SpamClassifier.Classify(m.Spam, threshold);
        var score = m.Spam.Score?.ToString("0.0", CultureInfo.CurrentCulture) ?? "n/a";
        var thresholdText = threshold.ToString("0.0", CultureInfo.CurrentCulture);
        var tests = m.Spam.Tests.Count == 0
            ? string.Empty
            : " Rules: " + string.Join(", ", m.Spam.Tests.Take(4)) + (m.Spam.Tests.Count > 4 ? $" +{m.Spam.Tests.Count - 4} more." : ".");

        UpdateSpamDetails(m.Spam, verdict, threshold);

        if (_rules.IsBlocked(m.From))
        {
            ShowSpamBanner = true;
            SpamIsSevere = true;
            SpamTitle = "You blocked this sender.";
            SpamDetail = "Messages from them go straight to Junk and never trigger notifications.";
            SpamActionText = "Unblock";
        }
        else if (verdict == SpamVerdict.Spam)
        {
            ShowSpamBanner = true;
            SpamIsSevere = true;
            SpamTitle = "This looks like spam.";
            SpamDetail = m.Spam.ServerFlaggedAsSpam == true
                ? $"Your mail server's SpamAssassin flagged it (score {score}).{tests}"
                : $"SpamAssassin score {score} is above your threshold of {thresholdText}.{tests}";
            SpamActionText = "Not junk";
        }
        else if (verdict == SpamVerdict.Suspicious)
        {
            ShowSpamBanner = true;
            SpamIsSevere = false;
            SpamTitle = "Be careful with this message.";
            SpamDetail = $"SpamAssassin score {score} is close to your threshold of {thresholdText}.{tests}";
            SpamActionText = "Trust sender";
        }
        else
        {
            ShowSpamBanner = false;
        }
    }

    private void UpdateSpamDetails(SpamReport spam, SpamVerdict verdict, double threshold)
    {
        var culture = CultureInfo.CurrentCulture;
        var thresholdText = threshold.ToString("0.0", culture);
        SpamCheckSummary = verdict switch
        {
            _ when !spam.HasHeaders => "Spam check: not checked by your mail server",
            _ when spam.Trust == SpamHeaderTrust.Untrusted => "Spam check: ignored (added by the sender)",
            SpamVerdict.Spam => $"Spam check: spam · score {Score(spam)} (your threshold {thresholdText})",
            SpamVerdict.Suspicious => $"Spam check: suspicious · score {Score(spam)} (your threshold {thresholdText})",
            SpamVerdict.Clean => $"Spam check: passed · score {Score(spam)} (your threshold {thresholdText})",
            _ => "Spam check: no score reported",
        };

        var lines = new List<string>();
        if (!spam.HasHeaders)
        {
            lines.Add("This message has no SpamAssassin headers. That doesn't mean it's safe — your provider may use a different spam filter or none.");
        }
        else
        {
            lines.Add(spam.Trust switch
            {
                SpamHeaderTrust.AddedInTransit => "Added by the mail servers that delivered this message.",
                SpamHeaderTrust.Untrusted => "These headers were already in the message when it was sent, so the sender could have written them. They are not used.",
                _ => "Where these headers were added couldn't be checked.",
            });
            if (spam.ServerFlaggedAsSpam is { } flagged) lines.Add(flagged ? "Your server marked it as spam." : "Your server didn't mark it as spam.");
            if (spam.RequiredScore is { } required) lines.Add($"Server threshold: {required.ToString("0.0", culture)}.");
            if (spam.HasConflictingHeaders) lines.Add("The message carries conflicting spam headers; the one added last is used.");
            if (spam.Version is { } version) lines.Add($"SpamAssassin {version}.");
        }

        SpamCheckDetail = string.Join(" ", lines);
        SpamRules.Clear();
        var rules = spam.Rules.Count > 0
            ? spam.Rules.OrderByDescending(r => Math.Abs(r.Score)).Select(r => $"{r.Score.ToString("+0.0;-0.0;0.0", culture),6}  {r.Name}  {r.Description}")
            : spam.Tests.Select(t => t);
        foreach (var rule in rules.Take(20)) SpamRules.Add(rule);

        static string Score(SpamReport r) => r.Score?.ToString("0.0", CultureInfo.CurrentCulture) ?? "n/a";
    }

    [RelayCommand]
    private Task ReplyAsync() => Message is null ? Task.CompletedTask : _commands.ReplyAsync(Message, ComposeMode.Reply);

    [RelayCommand]
    private Task ReplyAllAsync() => Message is null ? Task.CompletedTask : _commands.ReplyAsync(Message, ComposeMode.ReplyAll);

    [RelayCommand]
    private Task ForwardAsync() => Message is null ? Task.CompletedTask : _commands.ReplyAsync(Message, ComposeMode.Forward);

    [RelayCommand]
    private Task EditDraftAsync() => Message is null ? Task.CompletedTask : _commands.EditDraftAsync(Message);

    [RelayCommand]
    private Task ArchiveAsync() => Message is null ? Task.CompletedTask : _commands.ArchiveAsync(Message);

    [RelayCommand]
    private Task DeleteAsync() => Message is null ? Task.CompletedTask : _commands.DeleteAsync(Message);

    [RelayCommand]
    private Task TogglePinAsync() => Message is null ? Task.CompletedTask : _commands.TogglePinAsync(Message);

    [RelayCommand]
    private Task ToggleStarAsync() => Message is null ? Task.CompletedTask : _commands.ToggleStarAsync(Message);

    [RelayCommand]
    private Task ToggleReadAsync() => Message is null ? Task.CompletedTask : _commands.SetReadAsync(Message, !Message.IsRead);

    [RelayCommand]
    private Task ToggleJunkAsync() =>
        Message is null ? Task.CompletedTask
        : IsInJunk ? _commands.NotJunkAsync(Message)
        : _commands.MoveToJunkAsync(Message);

    /// <param name="choice">A QuickTime name, "Custom" or "Clear".</param>
    [RelayCommand]
    private Task SnoozeAsync(string choice) => Message is null ? Task.CompletedTask : _commands.SnoozeAsync(Message, choice);

    /// <param name="choice">A QuickTime name, "Custom" or "Clear".</param>
    [RelayCommand]
    private Task FollowUpAsync(string choice) => Message is null ? Task.CompletedTask : _commands.FollowUpAsync(Message, choice);

    [RelayCommand]
    private void ShowRemoteContent()
    {
        if (Message is null) return;
        _remoteAllowed.Add(Message.Id);
        RenderBody();
    }

    [RelayCommand]
    private async Task SpamActionAsync()
    {
        if (Message is null) return;
        var m = Message;
        if (_rules.IsBlocked(m.From))
        {
            _rules.RemoveRule(SenderRuleKind.BlockedSender, m.From.Address);
            _rules.RemoveRule(SenderRuleKind.BlockedDomain, m.From.Domain);
        }
        else
        {
            await _commands.NotJunkAsync(m);
        }

        UpdateSpamBanner(m);
        OnPropertyChanged(nameof(IsInJunk));
        OnPropertyChanged(nameof(JunkLabel));
        OnPropertyChanged(nameof(JunkGlyph));
    }

    public Task ComposeToAsync(EmailAddress to) => _commands.ComposeNewAsync(Message?.AccountId, to);
}

/// <summary>One message in the reading pane's conversation list.</summary>
public sealed partial class ThreadEntryViewModel(MessageSummary message, bool isCurrent, DateTimeOffset now, Action<Guid> open)
{
    public MessageSummary Message { get; } = message;
    public bool IsCurrent { get; } = isCurrent;
    public bool IsUnread => !Message.IsRead;
    public bool HasAttachments => Message.HasAttachments;
    public string Sender => Message.FolderKind is FolderKind.Sent or FolderKind.Drafts ? "You" : Message.From.DisplayName;
    public string DateText { get; } = DateDisplayFormatter.ForList(message.Date, now);
    public string Preview => Message.Preview;
    public double RowOpacity => IsCurrent ? 1.0 : 0.82;
    public string AutomationName => $"{Sender}, {DateText}{(IsCurrent ? ", open" : string.Empty)}{(IsUnread ? ", unread" : string.Empty)}";

    [RelayCommand]
    private void Open()
    {
        if (!IsCurrent) open(Message.Id);
    }
}
