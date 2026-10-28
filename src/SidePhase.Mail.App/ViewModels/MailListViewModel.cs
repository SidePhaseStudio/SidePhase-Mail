using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Collections;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Services;
using SidePhase.Mail.Core.Settings;
using SidePhase.Mail.Core.Spam;

namespace SidePhase.Mail.App.ViewModels;

/// <summary>The centre column: the message list for the selected mailbox with search, filter, sort and sections.</summary>
public sealed partial class MailListViewModel : ObservableObject
{
    private readonly IMailDataSource _mail;
    private readonly ISettingsService _settings;
    private readonly ISenderRulesService _rules;
    private readonly JunkPolicy _junk;
    private readonly IClock _clock;
    private readonly IUiDispatcher _ui;
    private readonly MessageCommandService _commands;
    private readonly ToastService _toasts;
    private readonly Dictionary<Guid, MessageItemViewModel> _items = [];
    private readonly Dictionary<MessageSectionKind, ListHeaderViewModel> _headers = [];
    private CancellationTokenSource? _searchDelay;
    private bool _refreshQueued;
    private int _lastSelectedIndex = -1;
    private bool _suppressAutoAdvance;
    private int _refreshVersion;
    private int _searchVersion;
    private IReadOnlyDictionary<string, IReadOnlySet<Guid>> _bodyMatches = new Dictionary<string, IReadOnlySet<Guid>>();
    private IReadOnlySet<Guid> _serverMatches = new HashSet<Guid>();

    public MailListViewModel(
        IMailDataSource mail,
        ISettingsService settings,
        ISenderRulesService rules,
        JunkPolicy junk,
        IClock clock,
        IUiDispatcher ui,
        MessageCommandService commands,
        ToastService toasts,
        ReadingPaneViewModel reading)
    {
        _mail = mail;
        _settings = settings;
        _rules = rules;
        _junk = junk;
        _clock = clock;
        _ui = ui;
        _commands = commands;
        _toasts = toasts;
        Reading = reading;

        Title = "Unified Inbox";
        UnreadText = string.Empty;
        EmptyTitle = string.Empty;
        EmptyMessage = string.Empty;
        SearchText = string.Empty;
        SyncStatus = string.Empty;
        SearchStatus = string.Empty;

        _mail.DataChanged += (_, _) => QueueRefresh();
        _rules.RulesChanged += (_, _) => QueueRefresh();
        _settings.SettingsChanged += (_, _) => QueueRefresh();
    }

    public ReadingPaneViewModel Reading { get; }

    public ObservableCollection<object> Entries { get; } = [];

    public MailboxScope Scope { get; private set; } = MailboxScope.UnifiedInbox;

    public bool IsDemo => _mail.IsDemo;

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string UnreadText { get; set; }

    [ObservableProperty]
    public partial bool HasUnread { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyTitle { get; set; }

    [ObservableProperty]
    public partial string EmptyMessage { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterLabel), nameof(HasActiveFilter))]
    public partial MessageFilter Filter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortLabel))]
    public partial MessageSort Sort { get; set; }

    /// <summary>Says where the current search results come from (this PC, or also the server).</summary>
    [ObservableProperty]
    public partial string SearchStatus { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchServerCommand))]
    public partial bool IsSearchActive { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchServerCommand))]
    public partial bool IsSearchingServer { get; set; }

    /// <summary>True once the server has been searched for the current query.</summary>
    [ObservableProperty]
    public partial bool HasServerResults { get; set; }

    public bool CanSearchServer => IsSearchActive && !IsSearchingServer && !_mail.IsDemo
        && Scope.Kind is not (MailboxScopeKind.Pinned or MailboxScopeKind.Favourites);

    [ObservableProperty]
    public partial bool IsSyncing { get; set; }

    [ObservableProperty]
    public partial string SyncStatus { get; set; }

    [ObservableProperty]
    public partial MessageItemViewModel? SelectedItem { get; set; }

    public bool HasActiveFilter => Filter != MessageFilter.All;

    public string FilterLabel => Filter switch
    {
        MessageFilter.Unread => "Unread",
        MessageFilter.Starred => "Favourites",
        MessageFilter.WithAttachments => "Has attachments",
        _ => "All",
    };

    public string SortLabel => Sort switch
    {
        MessageSort.OldestFirst => "Oldest first",
        MessageSort.SenderAscending => "Sender",
        MessageSort.SubjectAscending => "Subject",
        _ => "Newest first",
    };

    /// <summary>Selects a message if it's in the current list (refreshing first). Returns false if it isn't.</summary>
    public async Task<bool> SelectMessageAsync(Guid messageId)
    {
        await RefreshAsync();
        var item = Entries.OfType<MessageItemViewModel>().FirstOrDefault(m => m.Id == messageId);
        if (item is null) return false;
        SelectedItem = item;
        return true;
    }

    public async Task NavigateAsync(MailboxScope scope, string title)
    {
        Scope = scope;
        Title = title;
        if (HasServerResults)
        {
            _searchVersion++;
            _serverMatches = new HashSet<Guid>();
            HasServerResults = false;
            SearchStatus = IsSearchActive ? "Searching messages stored on this PC" : string.Empty;
        }

        OnPropertyChanged(nameof(CanSearchServer));
        SearchServerCommand.NotifyCanExecuteChanged();
        _suppressAutoAdvance = true;
        SelectedItem = null;
        _lastSelectedIndex = -1;
        await RefreshAsync();
        _suppressAutoAdvance = false;
    }

    partial void OnSelectedItemChanged(MessageItemViewModel? value)
    {
        if (value is not null)
        {
            _lastSelectedIndex = Entries.IndexOf(value);
        }

        _ = Reading.ShowAsync(value?.Summary);

        if (value is { IsUnread: true } && _settings.Current.MarkAsReadOnOpen)
        {
            _ = _commands.SetReadAsync(value.Summary, true);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        // A new query invalidates any server results for the old one.
        _searchVersion++;
        _serverMatches = new HashSet<Guid>();
        HasServerResults = false;
        IsSearchActive = !string.IsNullOrWhiteSpace(value);
        SearchStatus = IsSearchActive ? "Searching messages stored on this PC" : string.Empty;

        _searchDelay?.Cancel();
        _searchDelay = new CancellationTokenSource();
        var token = _searchDelay.Token;
        _ = Task.Delay(220, token).ContinueWith(
            _ => _ui.Post(() => _ = UpdateSearchAsync()),
            token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    /// <summary>Looks the query's words up in downloaded message text (off the UI thread), then refreshes.</summary>
    private async Task UpdateSearchAsync()
    {
        var version = _searchVersion;
        var terms = MessageSearch.TextTerms(SearchText);
        IReadOnlyDictionary<string, IReadOnlySet<Guid>> matches = new Dictionary<string, IReadOnlySet<Guid>>();
        try
        {
            if (terms.Count > 0) matches = await _mail.SearchDownloadedTextAsync(terms);
        }
        catch (Exception)
        {
            // Fall back to searching headers and previews only.
        }

        if (version != _searchVersion) return;
        _bodyMatches = matches;
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanSearchServer))]
    private async Task SearchServerAsync()
    {
        var version = _searchVersion;
        IsSearchingServer = true;
        SearchStatus = "Searching your mail server…";
        try
        {
            var result = await _mail.SearchServerAsync(SearchText, Scope);
            if (version != _searchVersion) return;
            _serverMatches = result.MessageIds;
            HasServerResults = result.Supported;
            SearchStatus = result.Supported ? "Results from this PC and the server · " + result.Message : result.Message;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SearchStatus = "The server search failed: " + ex.Message;
        }
        finally
        {
            IsSearchingServer = false;
        }
    }

    partial void OnFilterChanged(MessageFilter value) => _ = RefreshAsync();

    partial void OnSortChanged(MessageSort value) => _ = RefreshAsync();

    [RelayCommand]
    private void SetFilter(string filter) => Filter = Enum.TryParse<MessageFilter>(filter, out var f) ? f : MessageFilter.All;

    [RelayCommand]
    private void SetSort(string sort) => Sort = Enum.TryParse<MessageSort>(sort, out var s) ? s : MessageSort.NewestFirst;

    [RelayCommand]
    private void ClearFilter()
    {
        Filter = MessageFilter.All;
        SearchText = string.Empty;
    }

    [RelayCommand]
    private Task ComposeAsync() => _commands.ComposeNewAsync(Scope.AccountId);

    [RelayCommand]
    private async Task SyncAsync()
    {
        if (IsSyncing) return;
        IsSyncing = true;
        SyncStatus = "Checking for new mail…";
        try
        {
            var result = await _mail.SyncAsync();
            SyncStatus = result.Succeeded
                ? $"Updated {DateTime.Now.ToString("t", CultureInfo.CurrentCulture)}"
                : "Sync failed";
            _toasts.Show(result.Message, result.Succeeded ? ToastKind.Info : ToastKind.Error);
        }
        catch (Exception ex)
        {
            SyncStatus = "Sync failed";
            _toasts.Show("Couldn't check for new mail: " + ex.Message, ToastKind.Error);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>
    /// Re-selects a sensible neighbour when a header is chosen with the keyboard (headers aren't selectable).
    /// </summary>
    public MessageItemViewModel? NearestMessage(int index, bool forward)
    {
        for (var i = index; i >= 0 && i < Entries.Count; i += forward ? 1 : -1)
        {
            if (Entries[i] is MessageItemViewModel m) return m;
        }

        for (var i = index; i >= 0 && i < Entries.Count; i += forward ? -1 : 1)
        {
            if (Entries[i] is MessageItemViewModel m) return m;
        }

        return null;
    }

    private void TogglePinnedSection(ListHeaderViewModel header)
    {
        _settings.Update(s => s.PinnedSectionCollapsed = !header.IsCollapsed);
    }

    /// <summary>Coalesces bursts of change events (e.g. a sync adding messages in batches) into one reload.</summary>
    private void QueueRefresh()
    {
        _ui.Post(() =>
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
            _ = RefreshSoonAsync();
        });
    }

    private async Task RefreshSoonAsync()
    {
        await Task.Delay(150);
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        _refreshQueued = false;
        var version = ++_refreshVersion;
        var all = await _mail.GetMessagesAsync();
        var accounts = await _mail.GetAccountsAsync();
        if (version != _refreshVersion) return; // a newer refresh superseded this one

        // In views that mix accounts, each row gets its account's colour.
        var accountsById = accounts.Count > 1 && Scope.Kind != MailboxScopeKind.AccountFolder
            ? accounts.ToDictionary(a => a.Id)
            : null;

        var settings = _settings.Current;
        var inScope = all.Where(m => MailboxScopeMatcher.Matches(m, Scope, _junk.IsLocallyJunk)).ToList();

        var sections = MessageListBuilder.Build(inScope, new MessageListOptions
        {
            SearchText = SearchText,
            Search = new SearchContext { BodyMatches = _bodyMatches, ServerMatches = _serverMatches },
            Filter = Filter,
            Sort = Sort,
            ShowPinnedSection = settings.ShowPinnedSection && Scope.Kind != MailboxScopeKind.Pinned,
            PinnedSectionCollapsed = settings.PinnedSectionCollapsed,
            Now = _clock.Now,
            FirstDayOfWeek = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek,
        });

        var now = _clock.Now;
        var compact = settings.Density == ListDensity.Compact;
        var desired = new List<object>();
        var seen = new HashSet<Guid>();

        foreach (var section in sections)
        {
            if (section.Kind != MessageSectionKind.All)
            {
                if (!_headers.TryGetValue(section.Kind, out var header))
                {
                    header = new ListHeaderViewModel(section.Kind, TogglePinnedSection);
                    _headers[section.Kind] = header;
                }

                header.Count = section.Messages.Count;
                header.IsCollapsed = section.IsCollapsed;
                desired.Add(header);
            }

            if (section.IsCollapsed) continue;

            foreach (var message in section.Messages)
            {
                var context = new MessageDisplayContext(
                    now,
                    SpamClassifier.Classify(message.Spam, settings.SpamThreshold),
                    _junk.IsLocallyJunk(message),
                    _rules.IsBlocked(message.From),
                    accountsById?.GetValueOrDefault(message.AccountId));

                if (_items.TryGetValue(message.Id, out var item))
                {
                    item.Update(message, context);
                }
                else
                {
                    item = new MessageItemViewModel(message, context, _commands);
                    _items[message.Id] = item;
                }

                item.SetCompact(compact);
                seen.Add(message.Id);
                desired.Add(item);
            }
        }

        foreach (var stale in _items.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _items.Remove(stale);
        }

        var previous = SelectedItem;
        Entries.SyncTo(desired);

        // Keep the reading pane in step with flag changes, and auto-advance after archive/delete/move.
        if (previous is not null && Entries.Contains(previous))
        {
            Reading.UpdateSummary(previous.Summary);
            _lastSelectedIndex = Entries.IndexOf(previous);
        }
        else if (previous is not null && !_suppressAutoAdvance)
        {
            SelectedItem = NearestMessage(Math.Min(_lastSelectedIndex, Entries.Count - 1), forward: true);
        }

        var unread = inScope.Count(m => !m.IsRead);
        HasUnread = unread > 0;
        UnreadText = unread > 0 ? $"{unread} unread" : "All read";
        UpdateEmptyState(inScope.Count, desired.Count);
    }

    private void UpdateEmptyState(int scopeCount, int shown)
    {
        IsEmpty = shown == 0;
        if (!IsEmpty) return;

        if (scopeCount > 0 || !string.IsNullOrWhiteSpace(SearchText))
        {
            EmptyTitle = "No matching messages";
            EmptyMessage = string.IsNullOrWhiteSpace(SearchText)
                ? $"Nothing in {Title} matches the \"{FilterLabel}\" filter."
                : HasServerResults
                    ? $"Nothing matches \"{SearchText.Trim()}\", on this PC or on the server."
                    : $"Nothing stored on this PC matches \"{SearchText.Trim()}\". Try Search on server, other words, or operators like from:, subject:, after:2026-01-31 or has:attachment.";
            return;
        }

        (EmptyTitle, EmptyMessage) = Scope.Kind switch
        {
            MailboxScopeKind.Pinned => ("No pinned messages", "Right-click any message and choose \"Pin to top\" to keep it here, above everything else."),
            MailboxScopeKind.Favourites => ("No favourites yet", "Star a message to add it to your favourites."),
            MailboxScopeKind.Snoozed => ("Nothing snoozed", "Right-click a message and choose Snooze to hide it until later. Snoozing is kept on this PC only and isn't shared with other mail apps."),
            MailboxScopeKind.FollowUps => ("No follow-ups", "Right-click a message and choose \"Remind me to follow up\". You'll get a Windows notification when it's due."),
            _ when Scope.FolderKind == FolderKind.Junk => ("No junk", "Nice and tidy. Messages flagged by SpamAssassin or from blocked senders appear here."),
            _ => ("All caught up", $"There are no messages in {Title}."),
        };
    }
}
