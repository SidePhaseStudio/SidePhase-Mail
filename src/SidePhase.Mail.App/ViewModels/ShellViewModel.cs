using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SidePhase.Mail.App.Helpers;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Services;

namespace SidePhase.Mail.App.ViewModels;

/// <summary>Sidebar navigation: smart mailboxes, accounts and folders with live unread counts.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IMailDataSource _mail;
    private readonly JunkPolicy _junk;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;
    private bool _refreshQueued;

    public ShellViewModel(IMailDataSource mail, JunkPolicy junk, ISenderRulesService rules, ISettingsService settings, IUiDispatcher ui)
    {
        _mail = mail;
        _junk = junk;
        _settings = settings;
        _ui = ui;
        IsPaneOpen = !settings.Current.SidebarStartsCollapsed;

        _mail.DataChanged += (_, e) =>
        {
            if (e.Kind == MailDataChangeKind.Reset) _ui.Post(() => _ = BuildAsync());
            else QueueCountRefresh();
        };
        rules.RulesChanged += (_, _) => QueueCountRefresh();
        _settings.SettingsChanged += (_, e) => _ui.Post(() =>
        {
            if (IsPaneOpen == e.Settings.SidebarStartsCollapsed) IsPaneOpen = !e.Settings.SidebarStartsCollapsed;
            QueueCountRefresh();
        });
    }

    public ObservableCollection<NavItemViewModel> NavItems { get; } = [];

    public bool IsDemo => _mail.IsDemo;

    [ObservableProperty]
    public partial bool IsPaneOpen { get; set; }

    public NavItemViewModel? DefaultItem => NavItems.FirstOrDefault(i => i.Kind == NavItemKind.Item);

    public event EventHandler? NavigationRebuilt;

    partial void OnIsPaneOpenChanged(bool value)
    {
        if (_settings.Current.SidebarStartsCollapsed == value)
        {
            _settings.Update(s => s.SidebarStartsCollapsed = !value);
        }
    }

    public async Task BuildAsync()
    {
        var accounts = await _mail.GetAccountsAsync();
        NavItems.Clear();

        NavItems.Add(new NavItemViewModel(NavItemKind.Item, "Unified Inbox", Glyphs.Mail, MailboxScope.UnifiedInbox));
        NavItems.Add(new NavItemViewModel(NavItemKind.Item, "All Accounts", Glyphs.People, MailboxScope.AllAccounts, BadgeMode.None));
        NavItems.Add(new NavItemViewModel(NavItemKind.Item, "Pinned", Glyphs.Pin, MailboxScope.Pinned, BadgeMode.Total));
        NavItems.Add(new NavItemViewModel(NavItemKind.Item, "Favourites", Glyphs.Star, MailboxScope.Favourites, BadgeMode.None));
        NavItems.Add(new NavItemViewModel(NavItemKind.Item, "Drafts", Glyphs.Edit, MailboxScope.Unified(FolderKind.Drafts), BadgeMode.Total));
        NavItems.Add(new NavItemViewModel(NavItemKind.Item, "Sent", Glyphs.Send, MailboxScope.Unified(FolderKind.Sent), BadgeMode.None));
        NavItems.Add(new NavItemViewModel(NavItemKind.Item, "Junk", Glyphs.Junk, MailboxScope.Unified(FolderKind.Junk)));
        NavItems.Add(new NavItemViewModel(NavItemKind.Item, "Archive", Glyphs.Archive, MailboxScope.Unified(FolderKind.Archive), BadgeMode.None));
        NavItems.Add(new NavItemViewModel(NavItemKind.Item, "Trash", Glyphs.Delete, MailboxScope.Unified(FolderKind.Trash), BadgeMode.None));

        if (accounts.Count > 0)
        {
            NavItems.Add(new NavItemViewModel(NavItemKind.Header, "Accounts"));
        }

        foreach (var account in accounts)
        {
            var folders = await _mail.GetFoldersAsync(account.Id);
            var children = new ObservableCollection<NavItemViewModel>(folders.Select(f =>
                new NavItemViewModel(NavItemKind.Item, f.Name, Glyphs.ForFolder(f.Kind), MailboxScope.ForFolder(f), BadgeFor(f.Kind))));
            var inbox = folders.FirstOrDefault(f => f.Kind == FolderKind.Inbox);

            NavItems.Add(new NavItemViewModel(
                NavItemKind.Account,
                account.DisplayName,
                Glyphs.Contact,
                inbox is null ? null : MailboxScope.ForFolder(inbox))
            {
                ColorHex = account.ColorHex ?? "#8B7CFF",
                Subtitle = account.EmailAddress,
                Children = children,
                IsExpanded = account == accounts[0],
            });
        }

        await RefreshCountsAsync();
        NavigationRebuilt?.Invoke(this, EventArgs.Empty);
    }

    public NavItemViewModel? FindByScope(MailboxScope scope) =>
        NavItems.SelectMany(i => i.SelfAndDescendants()).FirstOrDefault(i => i.Kind == NavItemKind.Item && i.Scope == scope);

    private static BadgeMode BadgeFor(FolderKind kind) => kind switch
    {
        FolderKind.Drafts => BadgeMode.Total,
        FolderKind.Sent or FolderKind.Archive or FolderKind.Trash => BadgeMode.None,
        _ => BadgeMode.Unread,
    };

    private void QueueCountRefresh()
    {
        _ui.Post(() =>
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
            _ = RefreshCountsAsync();
        });
    }

    private async Task RefreshCountsAsync()
    {
        try
        {
            var messages = await _mail.GetMessagesAsync();
            var showBadges = _settings.Current.ShowUnreadBadges;
            foreach (var item in NavItems.SelectMany(i => i.SelfAndDescendants()))
            {
                item.ShowBadges = showBadges;
                if (item.Scope is null || item.BadgeMode == BadgeMode.None) continue;
                item.Count = item.BadgeMode == BadgeMode.Total
                    ? messages.Count(m => MailboxScopeMatcher.Matches(m, item.Scope, _junk.IsLocallyJunk))
                    : messages.Count(m => !m.IsRead && MailboxScopeMatcher.Matches(m, item.Scope, _junk.IsLocallyJunk));
            }
        }
        finally
        {
            _refreshQueued = false;
        }
    }
}
