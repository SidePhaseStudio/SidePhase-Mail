using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.App.ViewModels;
using SidePhase.Mail.App.Views;
using SidePhase.Mail.Core;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Scheduling;
using SidePhase.Mail.Core.Settings;
using SidePhase.Mail.Infrastructure;
using SidePhase.Mail.Infrastructure.Live;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.App;

public partial class App : Application
{
    private static IServiceProvider? _services;
    private static readonly CancellationTokenSource Shutdown = new();
    private AppDataPaths? _paths;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) StartupDiagnostics.Report("runtime", ex, showDialog: e.IsTerminating);
        };

        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Report("loading app resources", ex);
            throw;
        }

        UnhandledException += OnUnhandledException;
    }

    public static MainWindow? Main { get; private set; }

    public static T GetService<T>() where T : notnull =>
        (_services ?? throw new InvalidOperationException("Services are not configured yet.")).GetRequiredService<T>();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await LaunchAsync();
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Report("launch", ex);
            Exit();
        }
    }

    private async Task LaunchAsync()
    {
        _paths = new AppDataPaths();
        _paths.EnsureCreated();

        // Keep the WebView2 profile with the rest of the app data (the exe folder may be read-only).
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", _paths.WebViewDataFolder);

        // Let message bodies render on the translucent reading pane rather than an opaque white/black page.
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00000000");

        _services = ConfigureServices(_paths);

        await GetService<ISettingsService>().LoadAsync();
        GetService<ThemeService>().InitializeResources();

        Main = new MainWindow();
        Main.Activate();

        // Windows notifications for new mail (falls back to in-app notices if unavailable).
        var notifications = GetService<DesktopNotificationService>();
        notifications.Initialize();
        GetService<NewMailCoordinator>().Start();
        if (DesktopNotificationService.MessageFromLaunch() is { } launchMessage)
        {
            Main.DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(1500); // let the first list load
                await Main.OpenMessageAsync(launchMessage);
            });
        }

        // Messages held for Undo Send or scheduled for later are sent from here while the app runs. Anything left
        // "sending" by a crash is marked uncertain (never resent automatically).
        var outbox = GetService<OutboxService>();
        _ = Task.Run(async () =>
        {
            try
            {
                await outbox.PruneAsync(TimeSpan.FromDays(30), Shutdown.Token);
                await outbox.RunAsync(Shutdown.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Log("outbox", ex);
            }
        });

        // Snoozed messages come back and follow-up reminders fire on time.
        var watcher = GetService<TimedEventsWatcher>();
        watcher.Ticked += (_, _) => _ = GetService<MailListViewModel>().RefreshAsync();
        watcher.Start();
        Main.Closed += (_, _) =>
        {
            Shutdown.Cancel();
            watcher.Dispose();
        };

        // Real accounts sync in the background (only while real accounts, not the demo, are shown).
        var settings = GetService<ISettingsService>();
        GetService<LiveMailDataSource>().StartBackgroundSync(
            () => TimeSpan.FromMinutes(Math.Max(1, settings.Current.SyncIntervalMinutes)),
            () => settings.Current.DataMode == DataMode.Live);
    }

    private static ServiceProvider ConfigureServices(AppDataPaths paths)
    {
        var services = new ServiceCollection();

        services.AddSidePhaseMailCore();
        services.AddSidePhaseMailInfrastructure(paths);

        // UI services
        services.AddSingleton<IUiDispatcher, UiDispatcher>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<WindowService>();
        services.AddSingleton<MessageCommandService>();
        services.AddSingleton<DesktopNotificationService>();
        services.AddSingleton<NewMailCoordinator>();
        services.AddSingleton<TimedEventsWatcher>();

        // View models
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MailListViewModel>();
        services.AddSingleton<ReadingPaneViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<OutboxViewModel>();
        services.AddTransient<AddAccountViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Log the failure (never message contents or credentials) and keep the app running where possible.
        try
        {
            if (_paths is not null)
            {
                Directory.CreateDirectory(_paths.LogsFolder);
                var entry = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("O")).Append(' ')
                    .Append(e.Exception.GetType().FullName).Append(": ").AppendLine(e.Exception.Message)
                    .AppendLine(e.Exception.StackTrace)
                    .ToString();
                File.AppendAllText(Path.Combine(_paths.LogsFolder, "errors.log"), entry);
            }
        }
        catch (IOException)
        {
            // Logging must never crash the app.
        }

        e.Handled = true;
        try
        {
            _services?.GetService<ToastService>()?.Show("Something went wrong: " + e.Message, ToastKind.Error);
        }
        catch (Exception)
        {
            // Ignore: the UI may not be available.
        }
    }
}
