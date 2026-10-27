using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SidePhase.Mail.App.Helpers;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.Core.Formatting;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Spam;

namespace SidePhase.Mail.App.ViewModels;

/// <summary>How a message should be presented in the current list.</summary>
/// <param name="Account">Set in views that mix accounts, so each row can show which account it belongs to.</param>
public sealed record MessageDisplayContext(DateTimeOffset Now, SpamVerdict Verdict, bool IsLocallyJunk, bool IsSenderBlocked, MailAccount? Account = null);

/// <summary>One row in the message list. Instances are reused across refreshes to keep the list stable.</summary>
public sealed partial class MessageItemViewModel : ObservableObject
{
    private readonly MessageCommandService _commands;

    public MessageItemViewModel(MessageSummary summary, MessageDisplayContext context, MessageCommandService commands)
    {
        _commands = commands;
        Summary = summary;
        Apply(summary, context);
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
            DateText,
        }.Where(s => !string.IsNullOrEmpty(s)));
    }

    [RelayCommand]
    private Task ToggleStarAsync() => _commands.ToggleStarAsync(Summary);

    [RelayCommand]
    private Task TogglePinAsync() => _commands.TogglePinAsync(Summary);

    [RelayCommand]
    private Task ToggleReadAsync() => _commands.SetReadAsync(Summary, IsUnread);

    [RelayCommand]
    private Task ArchiveAsync() => _commands.ArchiveAsync(Summary);

    [RelayCommand]
    private Task DeleteAsync() => _commands.DeleteAsync(Summary);

    [RelayCommand]
    private Task ToggleJunkAsync() => IsInJunk ? _commands.NotJunkAsync(Summary) : _commands.MoveToJunkAsync(Summary);

    [RelayCommand]
    private Task SendTheBuggerBackAsync() => _commands.SendTheBuggerBackAsync(Summary);

    [RelayCommand]
    private Task ReplyAsync() => _commands.ReplyAsync(Summary, ComposeMode.Reply);

    [RelayCommand]
    private Task ReplyAllAsync() => _commands.ReplyAsync(Summary, ComposeMode.ReplyAll);

    [RelayCommand]
    private Task ForwardAsync() => _commands.ReplyAsync(Summary, ComposeMode.Forward);
}
