using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SidePhase.Mail.App.Helpers;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.Core.Formatting;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Scheduling;
using SidePhase.Mail.Core.Spam;

namespace SidePhase.Mail.App.ViewModels;

/// <summary>How a message should be presented in the current list.</summary>
/// <param name="Account">Set in views that mix accounts, so each row can show which account it belongs to.</param>
public sealed record MessageDisplayContext(DateTimeOffset Now, SpamVerdict Verdict, bool IsLocallyJunk, bool IsSenderBlocked, MailAccount? Account = null);

/// <summary>One row in the message list. Instances are reused across refreshes to keep the list stable.</summary>
public sealed partial class MessageItemViewModel : ObservableObject
{
    private readonly MessageCommandService _commands;
    private readonly Action<MessageItemViewModel>? _toggleConversation;
    private IReadOnlyList<MessageSummary> _inView = [];

    public MessageItemViewModel(
        MessageSummary summary,
        MessageDisplayContext context,
        MessageCommandService commands,
        Action<MessageItemViewModel>? toggleConversation = null,
        bool isThreadChild = false)
    {
        _commands = commands;
        _toggleConversation = toggleConversation;
        IsThreadChild = isThreadChild;
        Summary = summary;
        Apply(summary, context);
    }

    /// <summary>A message shown inside an expanded conversation (indented, compact).</summary>
    public bool IsThreadChild { get; }

    /// <summary>The conversation this row heads (conversation view), or null for a single message.</summary>
    public Guid? ConversationKey { get; private set; }

    /// <summary>Messages in the conversation, oldest first (including your own replies from Sent).</summary>
    public IReadOnlyList<MessageSummary> ConversationMessages { get; private set; } = [];

    /// <summary>The conversation this row belongs to (head or expanded child), for the reading pane.</summary>
    public IReadOnlyList<MessageSummary> ThreadMessages => IsThreadChild ? _thread : ConversationMessages;

    private IReadOnlyList<MessageSummary> _thread = [];

    public void SetThread(IReadOnlyList<MessageSummary> thread) => _thread = thread;

    public int ConversationCount => ConversationMessages.Count;
    public bool IsConversation => ConversationCount > 1;
    public string ConversationCountText => ConversationCount.ToString(CultureInfo.CurrentCulture);
    public bool IsExpanded { get; private set; }
    public string ExpandGlyph => IsExpanded ? "\uE70E" : "\uE70D";
    public string ExpandTooltip => IsExpanded ? "Collapse conversation" : $"Show all {ConversationCount} messages";

    /// <summary>What list actions (archive, delete, read) apply to: the whole conversation in view, or this message.</summary>
    private IReadOnlyList<MessageSummary> Targets => IsConversation && _inView.Count > 0 ? _inView : [Summary];

    /// <summary>Makes this row the head of a conversation (or a single message again when <paramref name="conversation"/> is null).</summary>
    public void SetConversation(Guid? key, IReadOnlyList<MessageSummary>? conversation, IReadOnlyList<MessageSummary>? inView, bool expanded)
    {
        ConversationKey = key;
        ConversationMessages = conversation ?? [];
        _inView = inView ?? [];
        IsExpanded = expanded && IsConversation;
        if (IsConversation)
        {
            // Like Gmail: the people in the conversation, and unread if anything in it is unread.
            var names = ConversationMessages.Select(m => m.FolderKind is FolderKind.Sent or FolderKind.Drafts ? "me" : FirstName(m.From))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            SenderName = names.Count <= 3 ? string.Join(", ", names) : $"{names[0]} … {string.Join(", ", names.TakeLast(2))}";
            IsUnread = _inView.Any(m => !m.IsRead);
            HasAttachments = ConversationMessages.Any(m => m.HasAttachments);
        }

        OnPropertyChanged(string.Empty);
    }

    private static string FirstName(EmailAddress address)
    {
        var name = address.DisplayName;
        var space = name.IndexOf(' ');
        return space > 0 && !name.Contains('@') ? name[..space] : name;
    }

    public MessageSummary Summary { get; private set; }

    public Guid Id => Summary.Id;

    public string SenderName { get; private set; } = string.Empty;
    public string Initials { get; private set; } = string.Empty;
    public int AvatarIndex { get; private set; }
    public string Subject { get; private set; } = string.Empty;
    public string Preview { get; private set; } = string.Empty;
    public string DateText { get; private set; } = string.Empty;
    public bool IsUnread { get; private set; }
    public bool IsRead => !IsUnread;
    public bool IsStarred { get; private set; }
    public bool IsPinned { get; private set; }
    public bool HasAttachments { get; private set; }
    public bool IsDraft { get; private set; }
    public bool IsInJunk { get; private set; }
    public bool HasPreview => !IsCompact && Preview.Length > 0;

    public string CategoriesText { get; private set; } = string.Empty;
    public bool HasCategories => CategoriesText.Length > 0;

    public bool IsSnoozed { get; private set; }
    public bool IsNotSnoozed => !IsSnoozed;
    public bool HasFollowUp { get; private set; }
    public bool HasNoFollowUp => !HasFollowUp;
    public bool IsFollowUpOverdue { get; private set; }
    public bool IsFollowUpPending => HasFollowUp && !IsFollowUpOverdue;
    public string FollowUpTooltip { get; private set; } = string.Empty;
    public string SnoozeTooltip { get; private set; } = string.Empty;

    /// <summary>"Spam 14.2", "Suspicious" or empty.</summary>
    public string SpamTag { get; private set; } = string.Empty;
    public bool HasSpamTag => SpamTag.Length > 0;

    public bool IsCompact { get; private set; }

    /// <summary>Account colour marker, shown only when the list mixes several accounts.</summary>
    public bool ShowAccountMarker { get; private set; }
    public string AccountColorHex { get; private set; } = "#8B7CFF";
    public string AccountName { get; private set; } = string.Empty;

    public string StarGlyph => IsStarred ? Glyphs.StarFilled : Glyphs.Star;
    public string StarMenuText => IsStarred ? "Remove from favourites" : "Add to favourites";
    public string PinGlyph => IsPinned ? Glyphs.Unpin : Glyphs.Pin;
    public string PinMenuText => IsPinned ? "Unpin" : "Pin to top";
    public string ReadGlyph => IsUnread ? Glyphs.MailRead : Glyphs.Mail;
    public string ReadMenuText => IsUnread ? "Mark as read" : "Mark as unread";
    public string JunkMenuText => IsInJunk ? "Not junk" : "Move to Junk";
    public string ReadTooltip => IsUnread ? "Mark as read (Ctrl+Q)" : "Mark as unread (Ctrl+U)";
    public string PinTooltip => PinMenuText + " (Ctrl+Shift+P)";
    public string StarTooltip => StarMenuText + " (Insert)";
    public string JunkGlyph => IsInJunk ? Glyphs.Accept : Glyphs.Junk;

    public string AutomationName { get; private set; } = string.Empty;

    public void Update(MessageSummary summary, MessageDisplayContext context)
    {
        Summary = summary;
        Apply(summary, context);
        OnPropertyChanged(string.Empty);
    }

    public void SetCompact(bool compact)
    {
        if (IsCompact == compact) return;
        IsCompact = compact;
        OnPropertyChanged(nameof(IsCompact));
        OnPropertyChanged(nameof(HasPreview));
    }

    private void Apply(MessageSummary m, MessageDisplayContext context)
    {
        var showRecipients = m.FolderKind is FolderKind.Sent or FolderKind.Drafts;
        if (showRecipients)
        {
            var first = m.To.FirstOrDefault();
            SenderName = m.To.Count == 0 ? "(No recipients)" : "To: " + string.Join(", ", m.To.Select(a => a.DisplayName));
            Initials = first is null ? "?" : AvatarHelper.Initials(first);
            AvatarIndex = AvatarHelper.ColorIndex(first?.Address);
        }
        else
        {
            SenderName = m.From.DisplayName;
            Initials = AvatarHelper.Initials(m.From);
            AvatarIndex = AvatarHelper.ColorIndex(m.From.Address);
        }

        ShowAccountMarker = context.Account is not null;
        AccountColorHex = context.Account?.ColorHex ?? "#8B7CFF";
        AccountName = context.Account?.EmailAddress ?? string.Empty;

        Subject = m.DisplaySubject;
        Preview = m.Preview;
        DateText = DateDisplayFormatter.ForList(m.Date, context.Now);
        CategoriesText = string.Join(" · ", m.Categories);
        IsSnoozed = m.SnoozedUntil is not null;
        SnoozeTooltip = m.SnoozedUntil is { } until ? "Snoozed until " + QuickTimes.Describe(until, context.Now, TimeZoneInfo.Local) : string.Empty;
        if (m.SnoozedUntil is { } back) DateText = QuickTimes.Describe(back, context.Now, TimeZoneInfo.Local);
        HasFollowUp = m.FollowUpAt is not null;
        IsFollowUpOverdue = m.FollowUpAt is { } due && due <= context.Now;
        FollowUpTooltip = m.FollowUpAt is { } at
            ? (IsFollowUpOverdue ? "Follow-up overdue since " : "Follow up ") + QuickTimes.Describe(at, context.Now, TimeZoneInfo.Local)
            : string.Empty;
        IsUnread = !m.IsRead;
        IsStarred = m.IsStarred;
        IsPinned = m.IsPinned;
        HasAttachments = m.HasAttachments;
        IsDraft = m.FolderKind == FolderKind.Drafts;
        IsInJunk = m.FolderKind == FolderKind.Junk || context.IsLocallyJunk;

        SpamTag = context.IsSenderBlocked
            ? "Blocked"
            : context.Verdict switch
            {
                SpamVerdict.Spam when m.Spam.Score is { } s => "Spam " + s.ToString("0.#", CultureInfo.CurrentCulture),
                SpamVerdict.Spam => "Spam",
                SpamVerdict.Suspicious => "Suspicious",
                _ => string.Empty,
            };

        AutomationName = string.Join(", ", new[]
        {
            IsUnread ? "Unread" : null,
            IsPinned ? "Pinned" : null,
            IsStarred ? "Favourite" : null,
            SenderName,
            Subject,
            HasAttachments ? "has attachments" : null,
            SnoozeTooltip,
            FollowUpTooltip,
            DateText,
        }.Where(s => !string.IsNullOrEmpty(s)));
    }

    [RelayCommand]
    private Task ToggleStarAsync() => _commands.ToggleStarAsync(Summary);

    [RelayCommand]
    private Task TogglePinAsync() => _commands.TogglePinAsync(Summary);

    [RelayCommand]
    private Task ToggleReadAsync() => _commands.SetReadAsync(IsUnread ? Targets : [Summary], IsUnread);

    [RelayCommand]
    private Task ArchiveAsync() => _commands.ArchiveAsync(Targets);

    [RelayCommand]
    private Task DeleteAsync() => _commands.DeleteAsync(Targets);

    [RelayCommand]
    private Task ToggleJunkAsync() => IsInJunk ? _commands.NotJunkAsync(Summary) : _commands.MoveToJunkAsync(Targets);

    [RelayCommand]
    private void ToggleConversation() => _toggleConversation?.Invoke(this);

    [RelayCommand]
    private Task ReplyAsync() => _commands.ReplyAsync(Summary, ComposeMode.Reply);

    [RelayCommand]
    private Task ReplyAllAsync() => _commands.ReplyAsync(Summary, ComposeMode.ReplyAll);

    [RelayCommand]
    private Task ForwardAsync() => _commands.ReplyAsync(Summary, ComposeMode.Forward);

    /// <param name="choice">A <see cref="QuickTime"/> name, "Custom" or "Clear".</param>
    [RelayCommand]
    private Task SnoozeAsync(string choice) => _commands.SnoozeAsync(Summary, choice);

    /// <param name="choice">A <see cref="QuickTime"/> name, "Custom" or "Clear".</param>
    [RelayCommand]
    private Task FollowUpAsync(string choice) => _commands.FollowUpAsync(Summary, choice);

    [RelayCommand]
    private Task EditCategoriesAsync() => _commands.EditCategoriesAsync(Summary);
}
