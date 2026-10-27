using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.App.ViewModels;

public enum NavItemKind
{
    Item,
    Account,
    Header,
    Separator,
}

public enum BadgeMode
{
    None,
    Unread,
    Total,
}

/// <summary>An entry in the sidebar: a smart mailbox, an account, one of its folders, or a header.</summary>
public sealed partial class NavItemViewModel : ObservableObject
{
    public NavItemViewModel(NavItemKind kind, string title, string glyph = "", MailboxScope? scope = null, BadgeMode badgeMode = BadgeMode.Unread)
    {
        Kind = kind;
        Title = title;
        Glyph = glyph;
        Scope = scope;
        BadgeMode = badgeMode;
        ShowBadges = true;
    }

    public NavItemKind Kind { get; }
    public string Title { get; }
    public string Glyph { get; }
    public MailboxScope? Scope { get; }
    public BadgeMode BadgeMode { get; }
    public string ColorHex { get; init; } = "#8B7CFF";
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>Child folders (accounts only). Null for leaves so no expand chevron is shown.</summary>
    public ObservableCollection<NavItemViewModel>? Children { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeVisibility))]
    public partial int Count { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeVisibility))]
    public partial bool ShowBadges { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public Visibility BadgeVisibility => ShowBadges && BadgeMode != BadgeMode.None && Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string AutomationName => Count > 0 && BadgeMode == BadgeMode.Unread ? $"{Title}, {Count} unread" : Title;

    public IEnumerable<NavItemViewModel> SelfAndDescendants()
    {
        yield return this;
        if (Children is null) yield break;
        foreach (var child in Children)
        {
            foreach (var item in child.SelfAndDescendants()) yield return item;
        }
    }
}
