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
        App.GetService<Infrastructure.Live.LiveMailDataSource>().NewMailArrived += (_, messages) => DispatcherQueue.TryEnqueue(() => ShowNewMail(messages));

        Shell.NavigationRebuilt += (_, _) =>
        {
            BuildNavigation();
            SelectScope(_mailList.Scope);
        };
        RootGrid.Loaded += OnRootLoaded;
        Closed += (_, _) => _settings.FlushAsync().Wait(TimeSpan.FromSeconds(2));
    }

    public ShellViewModel Shell { get; }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        _dialogs.DefaultXamlRoot = RootGrid.XamlRoot;
        await Shell.BuildAsync();

        if (_settings.Current.HasCompletedWelcome)
        {
            ShowInbox();
            await OpenFirstMessageForScreenshotAsync();
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

    public void ShowSettings()
    {
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
                ShowSettings();
                return Task.CompletedTask;
            });
        }
        else
        {
            toasts.Show(e.Message, ToastKind.Warning);
        }
    }

    private void ShowNewMail(IReadOnlyList<Core.Models.MessageSummary> messages)
    {
        if (!_settings.Current.NotificationsEnabled || messages.Count == 0) return;
        var first = messages[0];
        var text = messages.Count == 1
            ? $"New message from {first.From.DisplayName}: {first.DisplaySubject}"
            : $"{messages.Count} new messages";
        App.GetService<ToastService>().Show(text);
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
                ContentFrame.Navigate(typeof(SettingsPage), null, new EntranceNavigationTransitionInfo());
                break;
            case "add-account":
                ContentFrame.Navigate(typeof(WelcomePage), null, new EntranceNavigationTransitionInfo());
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
            SelectsOnInvoked = model.Scope is not null,
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

        if (model.Children is { Count: > 0 } children)
        {
            foreach (var child in children)
            {
                container.MenuItems.Add(CreateNavItem(child));
            }

            container.IsExpanded = model.IsExpanded;
        }

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
        badge.Value = model.Count;
        badge.Visibility = model.BadgeVisibility;
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
