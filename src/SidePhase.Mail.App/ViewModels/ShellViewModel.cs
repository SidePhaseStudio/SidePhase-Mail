using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SidePhase.Mail.App.Helpers;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Scheduling;
using SidePhase.Mail.Core.Services;

namespace SidePhase.Mail.App.ViewModels;

/// <summary>Sidebar navigation: smart mailboxes, accounts and folders with live unread counts.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IMailDataSource _mail;
    private readonly JunkPolicy _junk;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;
    private readonly OutboxService _outbox;
    private readonly Core.Organisation.SmartFolderService _smartFolders;
    private IReadOnlyList<Core.Organisation.SmartFolder> _smartFolderList = [];
    private bool _refreshQueued;
    private int _buildVersion;
    private Task _latestBuild = Task.CompletedTask;

    public ShellViewModel(
        IMailDataSource mail,
        JunkPolicy junk,
        ISenderRulesService rules,
        ISettingsService settings,
        IUiDispatcher ui,
        OutboxService outbox,
        Core.Organisation.SmartFolderService smartFolders)
    {
        _mail = mail;
        _outbox = outbox;
        _smartFolders = smartFolders;
        _smartFolders.Changed += (_, _) => ui.Post(() => _ = BuildAsync());
        _outbox.Changed += (_, _) => QueueCountRefresh();
        _junk = junk;
        _settings = settings;
        _ui = ui;
        IsPaneOpen = !settings.Current.SidebarStartsCollapsed;

        _mail.DataChanged += (_, e) =>
        {
            if (e.Kind == MailDataChangeKind.Reset) _ui.Post(() =>
            {
                OnPropertyChanged(nameof(IsDemo));
                _ = BuildAsync();
            });
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

    /// <summary>
    /// Rebuilds the sidebar. Several rebuilds can be requested at once (e.g. while a new account syncs its folders),
    /// so each builds its own list and only the newest one is shown; they never interleave.
    /// </summary>
    public async Task BuildAsync()
    {
        var build = BuildCoreAsync(++_buildVersion);
        _latestBuild = build;
        await build;

        // If a newer rebuild started meanwhile, wait for it so callers always see the finished sidebar.
        while (!ReferenceEquals(_latestBuild, build))
        {
            build = _latestBuild;
            await build;
        }
    }

    private async Task BuildCoreAsync(int version)
    {
        var accounts = await _mail.GetAccountsAsync();
        var items = new List<NavItemViewModel>();

        items.Add(new NavItemViewModel(NavItemKind.Item, "Unified Inbox", Glyphs.Mail, MailboxScope.UnifiedInbox));
        items.Add(new NavItemViewModel(NavItemKind.Item, "All Accounts", Glyphs.People, MailboxScope.AllAccounts, BadgeMode.None));
        items.Add(new NavItemViewModel(NavItemKind.Item, "Pinned", Glyphs.Pin, MailboxScope.Pinned, BadgeMode.Total));
        items.Add(new NavItemViewModel(NavItemKind.Item, "Favourites", Glyphs.Star, MailboxScope.Favourites, BadgeMode.None));
        items.Add(new NavItemViewModel(NavItemKind.Item, "Snoozed", Glyphs.Snooze, MailboxScope.Snoozed, BadgeMode.Total));
        items.Add(new NavItemViewModel(NavItemKind.Item, "Follow-ups", Glyphs.Flag, MailboxScope.FollowUps, BadgeMode.Total));
        items.Add(new NavItemViewModel(NavItemKind.Item, "Drafts", Glyphs.Edit, MailboxScope.Unified(FolderKind.Drafts), BadgeMode.Total));
        items.Add(new NavItemViewModel(NavItemKind.Item, "Scheduled", Glyphs.Scheduled, badgeMode: BadgeMode.Total) { PageTag = "outbox" });
        items.Add(new NavItemViewModel(NavItemKind.Item, "Sent", Glyphs.Send, MailboxScope.Unified(FolderKind.Sent), BadgeMode.None));
        items.Add(new NavItemViewModel(NavItemKind.Item, "Junk", Glyphs.Junk, MailboxScope.Unified(FolderKind.Junk)));
        items.Add(new NavItemViewModel(NavItemKind.Item, "Archive", Glyphs.Archive, MailboxScope.Unified(FolderKind.Archive), BadgeMode.Total));
        items.Add(new NavItemViewModel(NavItemKind.Item, "Trash", Glyphs.Delete, MailboxScope.Unified(FolderKind.Trash), BadgeMode.None));

        // Smart folders: saved searches. They never move mail.
        _smartFolderList = await _smartFolders.ListAsync();
        items.Add(new NavItemViewModel(NavItemKind.Header, "Smart folders"));
        foreach (var smart in _smartFolderList)
        {
            var countable = Core.Organisation.SmartFolderFilter.IndexTerms(smart).Count == 0;
            items.Add(new NavItemViewModel(NavItemKind.Item, smart.Name, smart.Glyph, MailboxScope.ForSmartFolder(smart.Id), countable ? BadgeMode.Unread : BadgeMode.None)
            {
                SmartFolder = smart,
                Subtitle = smart.Query,
            });
        }

        items.Add(new NavItemViewModel(NavItemKind.Item, "New smart folder…", Glyphs.AddSmartFolder, badgeMode: BadgeMode.None) { PageTag = "new-smart-folder" });

        // Favourite folders (any account), in the order they were added.
        var favouriteIds = _settings.Current.FavouriteFolderIds;
        if (favouriteIds.Count > 0)
        {
            var allFolders = new Dictionary<Guid, (MailFolder Folder, MailAccount Account)>();
            foreach (var account in accounts)
            {
                foreach (var folder in await _mail.GetFoldersAsync(account.Id)) allFolders[folder.Id] = (folder, account);
            }

            var favourites = favouriteIds.Where(allFolders.ContainsKey).Select(id => allFolders[id]).ToList();
            if (favourites.Count > 0)
            {
                items.Add(new NavItemViewModel(NavItemKind.Header, "Favourite folders"));
                foreach (var (folder, account) in favourites)
                {
                    items.Add(new NavItemViewModel(NavItemKind.Item, folder.Name, Glyphs.ForFolder(folder.Kind), MailboxScope.ForFolder(folder), BadgeFor(folder.Kind))
                    {
                        Subtitle = account.EmailAddress,
                        ColorHex = account.ColorHex ?? "#8B7CFF",
                        IsFavouriteFolder = true,
                    });
                }
            }
        }

        if (accounts.Count > 0)
        {
            items.Add(new NavItemViewModel(NavItemKind.Header, "Accounts"));
        }

        foreach (var account in accounts)
        {
            var folders = await _mail.GetFoldersAsync(account.Id);
            var children = new ObservableCollection<NavItemViewModel>(folders.Select(f =>
                new NavItemViewModel(NavItemKind.Item, f.Name, Glyphs.ForFolder(f.Kind), MailboxScope.ForFolder(f), BadgeFor(f.Kind))));
            var inbox = folders.FirstOrDefault(f => f.Kind == FolderKind.Inbox);

            items.Add(new NavItemViewModel(
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

        if (version != _buildVersion) return; // a newer rebuild has started; it will publish its own list

        NavItems.Clear();
        foreach (var item in items) NavItems.Add(item);
        await RefreshCountsAsync();
        NavigationRebuilt?.Invoke(this, EventArgs.Empty);
    }

    public bool IsFavouriteFolder(Guid folderId) => _settings.Current.FavouriteFolderIds.Contains(folderId);

    /// <summary>Adds or removes a folder from the sidebar's favourite folders. Local to this PC.</summary>
    public async Task ToggleFavouriteFolderAsync(Guid folderId)
    {
        _settings.Update(s =>
        {
            if (!s.FavouriteFolderIds.Remove(folderId)) s.FavouriteFolderIds.Add(folderId);
        });
        await BuildAsync();
    }

    public NavItemViewModel? FindByScope(MailboxScope scope) =>
        NavItems.SelectMany(i => i.SelfAndDescendants()).FirstOrDefault(i => i.Kind == NavItemKind.Item && i.Scope == scope);

    private static BadgeMode BadgeFor(FolderKind kind) => kind switch
    {
        FolderKind.Drafts or FolderKind.Archive => BadgeMode.Total,
        FolderKind.Sent or FolderKind.Trash => BadgeMode.None,
        _ => BadgeMode.Unread,
    };

    private void QueueCountRefresh()
    {
        _ui.Post(() =>
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
            _ = RefreshCountsSoonAsync();
        });
    }

    private async Task RefreshCountsSoonAsync()
    {
        await Task.Delay(200);
        await RefreshCountsAsync();
    }

    private async Task RefreshCountsAsync()
    {
        try
        {
            var messages = await _mail.GetMessagesAsync();
            var showBadges = _settings.Current.ShowUnreadBadges;
            var accounts = await _mail.GetAccountsAsync();
            var context = new SearchContext { AccountNames = accounts.ToDictionary(a => a.Id, a => a.DisplayName + " " + a.EmailAddress) };
            var outbox = await _outbox.GetItemsAsync(_mail.IsDemo);
            var waiting = outbox.Count(i => i.State is OutboxState.Failed or OutboxState.Uncertain
                || (i.Kind == OutboxKind.Scheduled && i.State is OutboxState.Queued or OutboxState.Sending));
            foreach (var item in NavItems.SelectMany(i => i.SelfAndDescendants()))
            {
                item.ShowBadges = showBadges;
                if (item.PageTag == "outbox") item.Count = waiting;
                if (item.Scope is null || item.BadgeMode == BadgeMode.None) continue;
                if (item.SmartFolder is { } smart)
                {
                    var matches = Core.Organisation.SmartFolderFilter.Build(smart, context, _junk.IsLocallyJunk);
                    item.Count = messages.Count(m => !m.IsRead && matches(m));
                    continue;
                }

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
