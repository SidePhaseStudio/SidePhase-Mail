namespace SidePhase.Mail.App.Helpers;

public sealed record ShortcutEntry(string Keys, string Action);

/// <summary>Every keyboard shortcut, as listed in Settings › Shortcuts (keep in step with README.md).</summary>
public static class ShortcutReference
{
    public static IReadOnlyList<ShortcutEntry> MessageList { get; } =
    [
        new("Ctrl+N", "New message"),
        new("Ctrl+R", "Reply"),
        new("Ctrl+Shift+R", "Reply all"),
        new("Ctrl+Shift+F", "Forward"),
        new("Ctrl+F or Ctrl+E", "Search"),
        new("F5", "Check for new mail"),
        new("Delete", "Move to Trash (a whole conversation when one is selected)"),
        new("Backspace", "Archive"),
        new("Ctrl+Q / Ctrl+U", "Mark as read / unread"),
        new("Ctrl+Shift+P", "Pin or unpin"),
        new("Insert", "Add to or remove from favourites"),
        new("Ctrl+click / Shift+click", "Select several messages for bulk actions (archive, delete, read, move, favourite, junk)"),
        new("Esc", "Clear a multiple selection"),
        new("Shift+F10", "All actions for the selected message, including Snooze, Remind me and categories"),
    ];

    public static IReadOnlyList<ShortcutEntry> Compose { get; } =
    [
        new("Ctrl+Enter", "Send (asks first if you turned that on)"),
        new("Ctrl+S", "Save draft"),
        new("Ctrl+B / Ctrl+I / Ctrl+U", "Bold / italic / underline"),
        new("Ctrl+K", "Insert link"),
        new("Esc", "Close (offers to save the draft)"),
    ];
}
