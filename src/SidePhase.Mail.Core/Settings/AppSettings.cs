using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Settings;

public enum AppTheme
{
    DarkGlass,
    LightGlass,
    SolidDark,
    SolidLight,
    FollowWindows,
}

public enum ListDensity
{
    Comfortable,
    Compact,
}

public enum ReadingPanePosition
{
    Right,
    Bottom,
}

public enum ComposeFormat
{
    RichText,
    PlainText,
}

/// <summary>Which mailbox the app shows. Demo data and real accounts are never mixed.</summary>
public enum DataMode
{
    Demo,
    Live,
}

public enum SenderRuleKind
{
    TrustedSender,
    BlockedSender,
    BlockedDomain,
}

public sealed record SenderRule(SenderRuleKind Kind, string Value, DateTimeOffset CreatedAt)
{
    public string NormalizedValue => Value.Trim().ToLowerInvariant();
}

/// <summary>
/// User preferences. Plain mutable POCO so it serialises cleanly; mutate through
/// <see cref="Abstractions.ISettingsService.Update"/> so changes are saved and broadcast.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;
    public const string DefaultAccent = "#8B7CFF";

    public int Version { get; set; } = CurrentVersion;
    public bool HasCompletedWelcome { get; set; }

    /// <summary>Demo shows the sample mailbox; Live shows the user's real accounts.</summary>
    public DataMode DataMode { get; set; } = DataMode.Demo;

    /// <summary>Folders shown in the sidebar's Favourites section.</summary>
    public List<Guid> FavouriteFolderIds { get; set; } = [];

    // OAuth client registrations (public client IDs, not secrets). See docs/ACCOUNT_PROVIDERS.md.
    // Environment variables SIDEPHASE_GOOGLE_CLIENT_ID / SIDEPHASE_MICROSOFT_CLIENT_ID take precedence.
    public string? GoogleClientId { get; set; }
    public string? MicrosoftClientId { get; set; }

    // Appearance
    public AppTheme Theme { get; set; } = AppTheme.DarkGlass;
    public string AccentColor { get; set; } = DefaultAccent;
    public ListDensity Density { get; set; } = ListDensity.Comfortable;
    public bool SidebarStartsCollapsed { get; set; }
    public bool ShowUnreadBadges { get; set; } = true;
    public ReadingPanePosition ReadingPane { get; set; } = ReadingPanePosition.Right;

    // Mail
    public bool NotificationsEnabled { get; set; } = true;
    public bool ShowPinnedSection { get; set; } = true;
    public bool PinnedSectionCollapsed { get; set; }
    public bool MarkAsReadOnOpen { get; set; } = true;
    public ComposeFormat DefaultComposeFormat { get; set; } = ComposeFormat.RichText;
    public int SyncIntervalMinutes { get; set; } = 5;

    /// <summary>Seconds a sent message is held so it can be undone (0 = off). Allowed: 0, 5, 10, 20, 30.</summary>
    public int UndoSendSeconds { get; set; } = 10;

    /// <summary>Ask before sending when Ctrl+Enter is pressed in the compose window.</summary>
    public bool ConfirmCtrlEnterSend { get; set; }

    /// <summary>Group replies into conversations in the message list.</summary>
    public bool ConversationView { get; set; } = true;

    // Spam
    public double SpamThreshold { get; set; } = 5.0;
    public bool TreatHighScoresAsJunk { get; set; } = true;
    public List<SenderRule> SenderRules { get; set; } = [];

    /// <summary>
    /// Also move new Inbox mail from blocked senders/domains into the server's Junk folder (off by default: normally
    /// blocked mail is only shown under Junk in SidePhase Mail, without changing anything on the server).
    /// </summary>
    public bool MoveBlockedMailToServerJunk { get; set; }

    /// <summary>Show a notice when messages are filtered into Junk.</summary>
    public bool NotifyWhenFiltered { get; set; }

    /// <summary>Accounts whose new mail never raises desktop notifications.</summary>
    public List<Guid> MutedNotificationAccounts { get; set; } = [];

    // Privacy
    public bool BlockRemoteImages { get; set; } = true;

    /// <summary>Signature per account id.</summary>
    public Dictionary<Guid, string> Signatures { get; set; } = [];

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.SenderRules = [.. SenderRules];
        copy.FavouriteFolderIds = [.. FavouriteFolderIds];
        copy.MutedNotificationAccounts = [.. MutedNotificationAccounts];
        copy.Signatures = new Dictionary<Guid, string>(Signatures);
        return copy;
    }

    /// <summary>Repairs out-of-range values from older or hand-edited settings files.</summary>
    public void Normalize()
    {
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.DarkGlass;
        if (!Enum.IsDefined(Density)) Density = ListDensity.Comfortable;
        if (!Enum.IsDefined(ReadingPane)) ReadingPane = ReadingPanePosition.Right;
        if (!Enum.IsDefined(DefaultComposeFormat)) DefaultComposeFormat = ComposeFormat.RichText;
        if (!Enum.IsDefined(DataMode)) DataMode = DataMode.Demo;
        FavouriteFolderIds = (FavouriteFolderIds ?? []).Distinct().ToList();
        MutedNotificationAccounts = (MutedNotificationAccounts ?? []).Distinct().ToList();
        GoogleClientId = string.IsNullOrWhiteSpace(GoogleClientId) ? null : GoogleClientId.Trim();
        MicrosoftClientId = string.IsNullOrWhiteSpace(MicrosoftClientId) ? null : MicrosoftClientId.Trim();
        if (!Formatting.ColorMath.TryParseHex(AccentColor, out _)) AccentColor = DefaultAccent;
        if (double.IsNaN(SpamThreshold) || SpamThreshold < 1 || SpamThreshold > 20) SpamThreshold = 5.0;
        if (SyncIntervalMinutes is < 1 or > 240) SyncIntervalMinutes = 5;
        if (UndoSendSeconds is not (0 or 5 or 10 or 20 or 30)) UndoSendSeconds = 10;
        SenderRules ??= [];
        Signatures ??= [];
        SenderRules = SenderRules
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Value))
            .DistinctBy(r => (r.Kind, r.NormalizedValue))
            .ToList();
        Version = CurrentVersion;
    }
}
