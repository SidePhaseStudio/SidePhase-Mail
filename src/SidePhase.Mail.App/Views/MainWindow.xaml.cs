using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.App.ViewModels;
using SidePhase.Mail.Core.Abstractions;
using Windows.Graphics;

namespace SidePhase.Mail.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly MailListViewModel _mailList;
    private readonly DialogService _dialogs;
    private readonly DispatcherQueueTimer _toastTimer;
    private Func<Task>? _toastAction;
    private bool _suppressNavigation;
    private readonly Dictionary<NavItemViewModel, NavigationViewItem> _navContainers = [];

    public MainWindow()
    {
        Shell = App.GetService<ShellViewModel>();
        _settings = App.GetService<ISettingsService>();
        _mailList = App.GetService<MailListViewModel>();
        _dialogs = App.GetService<DialogService>();
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Title = "SidePhase Mail";
        WindowHelper.SetIcon(this);
        WindowHelper.SizeAndCenter(this, 1440, 900);
        WindowHelper.SetMinimumSize(this, 960, 600);

        App.GetService<ThemeService>().Register(this, RootGrid, SolidLayer);

        // Lift toasts above the content so the theme shadow is visible (Translation can't be set in XAML).
        ToastBar.Shadow = new ThemeShadow();
        ToastBar.Translation = new System.Numerics.Vector3(0, 0, 32);

        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => ToastBar.IsOpen = false;
        App.GetService<ToastService>().ToastRequested += (_, request) => DispatcherQueue.TryEnqueue(() => ShowToast(request));
        App.GetService<IMailDataSource>().ErrorOccurred += (_, e) => DispatcherQueue.TryEnqueue(() => ShowMailError(e));
        App.GetService<DesktopNotificationService>().OpenMessageRequested += (_, id) => DispatcherQueue.TryEnqueue(() => _ = OpenMessageAsync(id));
        App.GetService<Core.Scheduling.OutboxService>().AttemptFinished += (_, item) => DispatcherQueue.TryEnqueue(() => ShowSendResult(item));

        Shell.NavigationRebuilt += (_, _) =>
        {
            BuildNavigation();
            SelectScope(_mailList.Scope);
        };
        var connectivity = App.GetService<ConnectivityService>();
        OfflineBar.IsOpen = !connectivity.IsOnline;
        connectivity.Changed += (_, online) => DispatcherQueue.TryEnqueue(() => OfflineBar.IsOpen = !online);
        RootGrid.Loaded += OnRootLoaded;
        RootGrid.SizeChanged += (_, e) => Footer.UpdateAvailableSpace(e.NewSize.Height);
        Closed += (_, _) =>
        {
            App.GetService<DesktopNotificationService>().Dispose();
            _settings.FlushAsync().Wait(TimeSpan.FromSeconds(2));
        };
    }

    public ShellViewModel Shell { get; }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    /// <summary>Restores the window if minimised and brings it to the front (another launch was handed to this one).</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        _dialogs.DefaultXamlRoot = RootGrid.XamlRoot;
        await Shell.BuildAsync();

        if (_settings.Current.HasCompletedWelcome)
        {
            ShowInbox();
            await OpenFirstMessageForScreenshotAsync();
            await OfferRecoveredDraftsAsync();
        }
        else
        {
            ShowWelcome();
        }
    }

    /// <summary>
    /// Used by the CI smoke test (SIDEPHASE_OPEN_FIRST_MESSAGE=1) so its screenshot shows the reading pane.
    /// </summary>
    private async Task OpenFirstMessageForScreenshotAsync()
    {
        if (Environment.GetEnvironmentVariable("SIDEPHASE_OPEN_FIRST_MESSAGE") != "1") return;
        await Task.Delay(1500);
        _mailList.SelectedItem = _mailList.Entries.OfType<MessageItemViewModel>().FirstOrDefault();
    }

    /// <summary>Messages that were open when SidePhase Mail last closed unexpectedly can be reopened.</summary>
    private async Task OfferRecoveredDraftsAsync()
    {
        try
        {
            var store = App.GetService<Core.Composition.IDraftRecoveryStore>();
            var recovered = await store.ListAsync();
            if (recovered.Count == 0 || Environment.GetEnvironmentVariable("SIDEPHASE_OPEN_FIRST_MESSAGE") == "1") return;

            var subjects = string.Join(Environment.NewLine, recovered.Take(5).Select(r =>
                "• " + (string.IsNullOrWhiteSpace(r.Draft.Subject) ? "(No subject)" : r.Draft.Subject)));
            var dialog = new ContentDialog
            {
                Title = recovered.Count == 1 ? "Recover an unsent message?" : $"Recover {recovered.Count} unsent messages?",
                Content = new TextBlock
                {
                    Text = "SidePhase Mail closed while you were writing:" + Environment.NewLine + subjects,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420,
                },
                PrimaryButtonText = "Reopen",
                SecondaryButtonText = "Discard",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Primary,
            };

            var choice = await _dialogs.ShowAsync(dialog);
            if (choice == ContentDialogResult.None) return;
            foreach (var item in recovered)
            {
                if (choice == ContentDialogResult.Primary)
                {
                    App.GetService<WindowService>().OpenCompose(item.Draft with { BodyIncludesSignature = true });
                }

                // The reopened window keeps its own recovery copy from now on.
                await store.DeleteAsync(item.SessionId);
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log("recovering unsent messages", ex);
        }
    }

    public void ShowWelcome()
    {
        _suppressNavigation = true;
        NavView.SelectedItem = AddAccountItem;
        _suppressNavigation = false;
        ContentFrame.Navigate(typeof(WelcomePage), null, new DrillInNavigationTransitionInfo());
    }

    /// <summary>Opens account setup, optionally preset for Google, Microsoft or a Microsoft 365 work account.</summary>
    public void ShowAddAccount(AddAccountPreset preset = AddAccountPreset.Any)
    {
        _suppressNavigation = true;
        NavView.SelectedItem = AddAccountItem;
        _suppressNavigation = false;
        ContentFrame.Navigate(typeof(AddAccountPage), preset, new DrillInNavigationTransitionInfo());
    }

    /// <summary>Settings section to open next time Settings is shown (e.g. "accounts" from a sign-in problem).</summary>
    private string? _settingsSection;

    public void ShowSettings(string? section = null)
    {
        _settingsSection = section;
        if (NavView.SelectedItem == SettingsItem && section is not null && ContentFrame.Content is SettingsPage page)
        {
            page.ShowSection(section);
            return;
        }

        NavView.SelectedItem = SettingsItem;
    }

    private readonly Dictionary<string, DateTimeOffset> _recentErrors = [];

    /// <summary>
    /// Shows a mail problem as a toast. The same message is shown at most once a minute so a server that's down
    /// doesn't flood the screen, and one failing account never interrupts the others.
    /// </summary>
    private void ShowMailError(MailErrorEventArgs e)
    {
        var now = DateTimeOffset.Now;
        if (_recentErrors.TryGetValue(e.Message, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
        _recentErrors[e.Message] = now;

        var toasts = App.GetService<ToastService>();
        if (e.RequiresSignIn)
        {
            toasts.ShowWithAction(e.Message, ToastKind.Error, "Sign in again", () =>
            {
                ShowSettings("accounts");
                return Task.CompletedTask;
            });
        }
        else
        {
            toasts.Show(e.Message, ToastKind.Warning);
        }
    }

    /// <summary>Reports how a message from the outbox (undo window or scheduled) got on.</summary>
    private void ShowSendResult(Core.Scheduling.OutboxItem item)
    {
        var toasts = App.GetService<ToastService>();
        var subject = string.IsNullOrWhiteSpace(item.Draft.Subject) ? "(No subject)" : item.Draft.Subject;
        switch (item.State)
        {
            case Core.Scheduling.OutboxState.Accepted:
                toasts.Show(item.Kind == Core.Scheduling.OutboxKind.Scheduled ? $"Scheduled message sent: \"{subject}\"" : "Message sent.", ToastKind.Success);
                break;
            case Core.Scheduling.OutboxState.Failed:
                toasts.ShowWithAction($"\"{subject}\" wasn't sent: {item.LastError}", ToastKind.Error, "View", ShowOutboxAsync);
                break;
            case Core.Scheduling.OutboxState.Uncertain:
                toasts.ShowWithAction($"\"{subject}\" may or may not have been delivered. It won't be sent again automatically.", ToastKind.Warning, "View", ShowOutboxAsync);
                break;
        }
    }

    private Task ShowOutboxAsync()
    {
        var item = Shell.NavItems.FirstOrDefault(i => i.PageTag == "outbox");
        if (item is not null && _navContainers.TryGetValue(item, out var container)) NavView.SelectedItem = container;
        return Task.CompletedTask;
    }

    /// <summary>Brings the window forward and opens a message (from a notification click).</summary>
    public async Task OpenMessageAsync(Guid messageId)
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
            Activate();

            if (ContentFrame.CurrentSourcePageType != typeof(MailPage)) ShowInbox();
            if (!await _mailList.SelectMessageAsync(messageId))
            {
                // Not in the current view (e.g. a folder was open): switch to the Unified Inbox and try again.
                ShowInbox();
                await _mailList.NavigateAsync(Core.Models.MailboxScope.UnifiedInbox, "Unified Inbox");
                if (!await _mailList.SelectMessageAsync(messageId))
                {
                    // Elsewhere (Archive, Sent, a folder): open the folder it's in.
                    var mail = App.GetService<IMailDataSource>();
                    if (await mail.GetMessageAsync(messageId) is { } message
                        && (await mail.GetFoldersAsync(message.AccountId)).FirstOrDefault(f => f.Id == message.FolderId) is { } folder)
                    {
                        var scope = Core.Models.MailboxScope.ForFolder(folder);
                        SelectScope(scope);
                        await _mailList.NavigateAsync(scope, folder.Name);
                        await _mailList.SelectMessageAsync(messageId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log("opening a message from a notification", ex);
        }
    }

    public void ShowInbox()
    {
        if (Shell.DefaultItem is { } item && _navContainers.TryGetValue(item, out var container))
        {
            NavView.SelectedItem = container;
        }
    }

    private void OnPaneToggleRequested(TitleBar sender, object args) => Shell.IsPaneOpen = !Shell.IsPaneOpen;

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is NavItemViewModel { PageTag: "new-smart-folder" })
        {
            _ = App.GetService<SmartFolderEditor>().EditAsync(null);
            return;
        }

        // Re-invoking the selected mailbox while in Settings returns to it.
        if (args.InvokedItemContainer?.Tag is NavItemViewModel { Scope: not null } item
            && ReferenceEquals(sender.SelectedItem, args.InvokedItemContainer)
            && ContentFrame.CurrentSourcePageType != typeof(MailPage))
        {
            _ = NavigateToMailboxAsync(item);
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavigation) return;

        switch (args.SelectedItemContainer?.Tag)
        {
            case "settings":
                ContentFrame.Navigate(typeof(SettingsPage), _settingsSection, new EntranceNavigationTransitionInfo());
                _settingsSection = null;
                break;
            case "attachments":
                ContentFrame.Navigate(typeof(AttachmentsPage), null, new EntranceNavigationTransitionInfo());
                break;
            case "contacts":
                ContentFrame.Navigate(typeof(ContactsPage), null, new EntranceNavigationTransitionInfo());
                break;
            case "add-account":
                ContentFrame.Navigate(typeof(WelcomePage), null, new EntranceNavigationTransitionInfo());
                break;
            case NavItemViewModel { PageTag: "outbox" }:
                ContentFrame.Navigate(typeof(OutboxPage), null, new EntranceNavigationTransitionInfo());
                break;
            case NavItemViewModel { Scope: not null } item:
                _ = NavigateToMailboxAsync(item);
                break;
        }
    }

    private async Task NavigateToMailboxAsync(NavItemViewModel item)
    {
        if (ContentFrame.CurrentSourcePageType != typeof(MailPage))
        {
            ContentFrame.Navigate(typeof(MailPage), null, new EntranceNavigationTransitionInfo());
        }

        await _mailList.NavigateAsync(item.Scope!, item.Kind == NavItemKind.Account ? $"{item.Title} · Inbox" : item.Title);
    }

    private void SelectScope(Core.Models.MailboxScope scope)
    {
        var item = Shell.FindByScope(scope) ?? Shell.DefaultItem;
        if (item is null || ContentFrame.CurrentSourcePageType != typeof(MailPage)) return;
        if (!_navContainers.TryGetValue(item, out var container)) return;
        _suppressNavigation = true;
        NavView.SelectedItem = container;
        _suppressNavigation = false;
    }

    /// <summary>
    /// Builds the sidebar from the shell view model. Items are created in code (not through MenuItemsSource data
    /// templates) because NavigationView does not reliably hand .NET view models back to x:Bind templates.
    /// </summary>
    private void BuildNavigation()
    {
        _navContainers.Clear();
        NavView.MenuItems.Clear();
        foreach (var item in Shell.NavItems)
        {
            NavView.MenuItems.Add(CreateNavItem(item));
        }
    }

    private NavigationViewItemBase CreateNavItem(NavItemViewModel model)
    {
        switch (model.Kind)
        {
            case NavItemKind.Header:
                return new NavigationViewItemHeader { Content = model.Title };
            case NavItemKind.Separator:
                return new NavigationViewItemSeparator();
        }

        var icon = new FontIcon { Glyph = model.Glyph };
        var container = new NavigationViewItem
        {
            Tag = model,
            Icon = icon,
            // "New smart folder…" is an action, so it isn't selected when clicked.
            SelectsOnInvoked = model.Scope is not null || model.PageTag is "outbox",
        };

        if (model.Kind == NavItemKind.Account)
        {
            icon.Foreground = Helpers.X.HexBrush(model.ColorHex);
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = model.Title, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new TextBlock
            {
                Text = model.Subtitle,
                FontSize = 11,
                Opacity = 0.62,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            container.Content = content;
            ToolTipService.SetToolTip(container, model.Subtitle);
        }
        else
        {
            container.Content = model.Title;
            ToolTipService.SetToolTip(container, model.IsFavouriteFolder ? $"{model.Title} · {model.Subtitle}" : model.Title);
        }

        if (model.Scope is { Kind: Core.Models.MailboxScopeKind.AccountFolder, FolderId: { } folderId })
        {
            container.ContextFlyout = CreateFolderMenu(folderId);
        }
        else if (model.SmartFolder is { } smart)
        {
            var editor = App.GetService<SmartFolderEditor>();
            var edit = new MenuFlyoutItem { Text = "Edit smart folder…", Icon = new FontIcon { Glyph = Helpers.Glyphs.Edit } };
            edit.Click += async (_, _) => await editor.EditAsync(smart);
            var delete = new MenuFlyoutItem { Text = "Delete smart folder", Icon = new FontIcon { Glyph = Helpers.Glyphs.Delete } };
            delete.Click += async (_, _) => await editor.DeleteAsync(smart);
            var menu = new MenuFlyout();
            menu.Items.Add(edit);
            menu.Items.Add(delete);
            container.ContextFlyout = menu;
            ToolTipService.SetToolTip(container, $"{smart.Name} · {smart.Query}");
        }

        if (model.Children is { Count: > 0 } children)
        {
            foreach (var child in children)
            {
                container.MenuItems.Add(CreateNavItem(child));
            }

            container.IsExpanded = model.IsExpanded;
        }

        EnableMessageDrop(model, container);

        var badge = new InfoBadge();
        if (Application.Current.Resources.TryGetValue("SpInfoBadgeStyle", out var style) && style is Style badgeStyle)
        {
            badge.Style = badgeStyle;
        }

        container.InfoBadge = badge;
        UpdateBadge(model, container, badge);
        model.PropertyChanged += (_, _) => UpdateBadge(model, container, badge);

        _navContainers[model] = container;
        return container;
    }

    /// <summary>Folders in the sidebar accept messages dragged from the list (moved on the server, with undo).</summary>
    private void EnableMessageDrop(NavItemViewModel model, NavigationViewItem container)
    {
        var scope = model.Scope;
        var accepts = scope is { Kind: Core.Models.MailboxScopeKind.AccountFolder }
            || scope is { Kind: Core.Models.MailboxScopeKind.UnifiedFolder, FolderKind: Core.Models.FolderKind.Inbox or Core.Models.FolderKind.Archive or Core.Models.FolderKind.Junk or Core.Models.FolderKind.Trash }
            || scope is { Kind: Core.Models.MailboxScopeKind.UnifiedInbox };
        if (!accepts) return;

        container.AllowDrop = true;
        container.DragOver += (_, e) =>
        {
            if (!e.DataView.Properties.ContainsKey(MailPage.MessageDragFormat)) return;
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            if (e.DragUIOverride is not null) e.DragUIOverride.Caption = "Move to " + model.Title;
        };
        container.Drop += async (_, e) =>
        {
            if (!e.DataView.Properties.TryGetValue(MailPage.MessageDragFormat, out var value) || value is not string text) return;
            var ids = text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToHashSet();
            var messages = (await App.GetService<IMailDataSource>().GetMessagesAsync()).Where(m => ids.Contains(m.Id)).ToList();
            if (messages.Count == 0) return;

            var commands = App.GetService<MessageCommandService>();
            if (scope!.Kind == Core.Models.MailboxScopeKind.AccountFolder)
            {
                var folder = (await App.GetService<IMailDataSource>().GetFoldersAsync(scope.AccountId!.Value)).FirstOrDefault(f => f.Id == scope.FolderId);
                if (folder is not null) await commands.MoveToFolderAsync(messages, folder);
                return;
            }

            switch (scope.FolderKind ?? Core.Models.FolderKind.Inbox)
            {
                case Core.Models.FolderKind.Archive:
                    await commands.ArchiveAsync(messages);
                    break;
                case Core.Models.FolderKind.Trash:
                    await commands.DeleteAsync(messages);
                    break;
                case Core.Models.FolderKind.Junk:
                    await commands.MoveToJunkAsync(messages);
                    break;
                default:
                    var moves = await App.GetService<IMailDataSource>().MoveToAsync(messages.Select(m => m.Id).ToList(), Core.Models.FolderKind.Inbox);
                    // Nothing moved: the server's error is already showing; don't cover it with a success notice.
                    if (moves.Count > 0)
                        App.GetService<ToastService>().ShowWithUndo(moves.Count == 1 ? "Moved to Inbox" : $"Moved {moves.Count} messages to Inbox", () => App.GetService<IMailDataSource>().RestoreAsync(moves));
                    break;
            }
        };
    }

    private MenuFlyout CreateFolderMenu(Guid folderId)
    {
        var toggle = new MenuFlyoutItem { Icon = new FontIcon { Glyph = Helpers.Glyphs.Star } };
        toggle.Click += async (_, _) => await Shell.ToggleFavouriteFolderAsync(folderId);
        var menu = new MenuFlyout();
        menu.Items.Add(toggle);
        menu.Opening += (_, _) => toggle.Text = Shell.IsFavouriteFolder(folderId) ? "Remove from favourite folders" : "Add to favourite folders";
        return menu;
    }

    private static void UpdateBadge(NavItemViewModel model, NavigationViewItem container, InfoBadge badge)
    {
        if (model.Kind == NavItemKind.Account && model.ProblemText is { Length: > 0 } problem)
        {
            // A warning instead of a count: the account can't sync until this is fixed.
            badge.Value = -1;
            badge.IconSource = new FontIconSource { Glyph = "\uE7BA" };
            badge.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(container, $"{model.Subtitle} · {problem}");
        }
        else
        {
            badge.IconSource = null;
            badge.Value = model.Count;
            badge.Visibility = model.BadgeVisibility;
            if (model.Kind == NavItemKind.Account) ToolTipService.SetToolTip(container, model.Subtitle);
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(container, model.AutomationName);
    }

    private void ShowToast(ToastRequest request)
    {
        ToastBar.IsOpen = false;
        ToastBar.Message = request.Message;
        ToastBar.Severity = request.Kind switch
        {
            ToastKind.Success => InfoBarSeverity.Success,
            ToastKind.Warning => InfoBarSeverity.Warning,
            ToastKind.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        _toastAction = request.Action;
        ToastActionButton.Content = request.ActionText;
        ToastActionButton.Visibility = request.Action is null ? Visibility.Collapsed : Visibility.Visible;
        ToastBar.IsOpen = true;

        _toastTimer.Stop();
        _toastTimer.Interval = request.Duration;
        _toastTimer.Start();
    }

    private async void OnToastActionClick(object sender, RoutedEventArgs e)
    {
        var action = _toastAction;
        _toastAction = null;
        ToastBar.IsOpen = false;
        if (action is not null) await action();
    }
}

/// <summary>Window sizing and icon helpers shared by the main and compose windows.</summary>
internal static class WindowHelper
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    public static double GetScale(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    public static void SetIcon(Window window)
    {
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) window.AppWindow.SetIcon(icon);
    }

    /// <summary>Sizes the window (in effective pixels) and centres it on the current display's work area.</summary>
    public static void SizeAndCenter(Window window, int width, int height)
    {
        var scale = GetScale(window);
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var w = Math.Min((int)(width * scale), (int)(area.Width * 0.94));
        var h = Math.Min((int)(height * scale), (int)(area.Height * 0.94));
        window.AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    public static void SetMinimumSize(Window window, int width, int height)
    {
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            var scale = GetScale(window);
            presenter.PreferredMinimumWidth = (int)(width * scale);
            presenter.PreferredMinimumHeight = (int)(height * scale);
        }
    }
}
