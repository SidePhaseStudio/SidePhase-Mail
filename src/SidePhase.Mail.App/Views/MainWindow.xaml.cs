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

        Shell.NavigationRebuilt += (_, _) => SelectScope(_mailList.Scope);
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
            NavView.SelectedItem = Shell.DefaultItem;
        }
        else
        {
            ShowWelcome();
        }
    }

    public void ShowWelcome()
    {
        _suppressNavigation = true;
        NavView.SelectedItem = AddAccountItem;
        _suppressNavigation = false;
        ContentFrame.Navigate(typeof(WelcomePage), null, new DrillInNavigationTransitionInfo());
    }

    public void ShowInbox()
    {
        NavView.SelectedItem = Shell.DefaultItem;
    }

    private void OnPaneToggleRequested(TitleBar sender, object args) => Shell.IsPaneOpen = !Shell.IsPaneOpen;

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        // Re-invoking the selected mailbox while in Settings returns to it.
        if (args.InvokedItemContainer?.DataContext is NavItemViewModel { Scope: not null } item
            && ReferenceEquals(sender.SelectedItem, item)
            && ContentFrame.CurrentSourcePageType != typeof(MailPage))
        {
            _ = NavigateToMailboxAsync(item);
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavigation) return;

        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            switch (tag)
            {
                case "settings":
                    ContentFrame.Navigate(typeof(SettingsPage), null, new EntranceNavigationTransitionInfo());
                    break;
                case "add-account":
                    ContentFrame.Navigate(typeof(WelcomePage), null, new EntranceNavigationTransitionInfo());
                    break;
            }

            return;
        }

        if (args.SelectedItem is NavItemViewModel { Scope: not null } item)
        {
            _ = NavigateToMailboxAsync(item);
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
        _suppressNavigation = true;
        NavView.SelectedItem = item;
        _suppressNavigation = false;
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
