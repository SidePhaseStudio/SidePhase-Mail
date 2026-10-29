using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
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

        // First start of the installed app: bring over accounts, settings and the mail cache from the portable build
        // (copied, never moved; nothing is overwritten). See docs/PACKAGING.md.
        var import = AppIdentity.IsPackaged
            ? await Task.Run(() => PortableDataImporter.ImportIfNeeded(AppDataPaths.PortableRoot, _paths.Root))
            : DataImportResult.NothingToDo;

        // Keep the WebView2 profile with the rest of the app data (the exe folder may be read-only).
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", _paths.WebViewDataFolder);

        // Let message bodies render on the translucent reading pane rather than an opaque white/black page.
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00000000");

        // No speculative connections from message content: no DNS prefetching or preconnecting to links (which would
        // tell a sender the message was opened even with remote content blocked).
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
            "--disable-features=AnchorElementPreloader,NetworkPrediction,SpeculationRulesPrefetchFuture --dns-prefetch-disable");

        _services = ConfigureServices(_paths);

        // Open (and if needed upgrade) the mail cache before anything uses it. Problems are explained, never "fixed"
        // by deleting data.
        if (!await PrepareMailCacheAsync()) return;

        await GetService<ISettingsService>().LoadAsync();
        GetService<ThemeService>().InitializeResources();

        var main = new MainWindow();
        Main = main;
        main.Activate();
        ReportStartupNotices(import);

        // Later launches (Start menu, mailto: links) are handed to this instance: bring the window forward.
        AppInstance.GetCurrent().Activated += (_, e) => main.DispatcherQueue.TryEnqueue(() =>
        {
            main.BringToFront();
            HandleActivation(e);
        });
        main.DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(1500); // let the first list load
            HandleActivation(AppInstance.GetCurrent().GetActivatedEventArgs());
        });

        // Windows notifications for new mail (falls back to in-app notices if unavailable).
        var notifications = GetService<DesktopNotificationService>();
        notifications.Initialize();
        GetService<NewMailCoordinator>().Start();
        // (A launch from a notification click opens its message through HandleActivation above.)

        // Messages held for Undo Send or scheduled for later are sent from here while the app runs. Anything left
        // "sending" by a crash is marked uncertain (never resent automatically).
        var outbox = GetService<OutboxService>();
        var contacts = GetService<Core.Contacts.ContactService>();
        // A trail of what happened to each sent message (states, server replies, where the Sent copy went), so a
        // "sent but never arrived" report can be diagnosed. No addresses, subjects or content.
        GetService<LiveMailDataSource>().SendTrace += (_, text) => StartupDiagnostics.Trace("send", text);
        outbox.AttemptFinished += (_, item) => StartupDiagnostics.Trace("outbox",
            $"item {item.Id.ToString("N")[..8]} ({item.Kind}{(item.IsDemo ? ", demo: nothing leaves this PC" : string.Empty)}) is {item.State} after {item.Attempts} attempt(s)" +
            (item.LastError is { } error ? ": " + error : string.Empty));
        outbox.AttemptFinished += (_, item) =>
        {
            // Remember who real messages went to, for autocomplete (demo messages aren't remembered).
            if (item.State == OutboxState.Accepted && !item.IsDemo) _ = contacts.RecordSentAsync(item.Draft);
        };
        outbox.ProblemOccurred += (_, message) =>
        {
            StartupDiagnostics.Log("outbox", new IOException(message));
            GetService<ToastService>().Show(message, ToastKind.Error);
        };
        _ = Task.Run(async () =>
        {
            try
            {
                // Tidying old items is optional; it must never stop scheduled mail from being sent.
                await outbox.PruneAsync(TimeSpan.FromDays(30), Shutdown.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StartupDiagnostics.Log("tidying the outbox", ex);
            }

            try
            {
                await outbox.RunAsync(Shutdown.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Log("outbox", ex);
                GetService<ToastService>().Show("Scheduled and queued messages can't be sent in this session. Restart SidePhase Mail to try again.", ToastKind.Error);
            }
        });

        // The local spam filter's learned word counts (small; loaded in the background).
        _ = Task.Run(async () =>
        {
            try
            {
                await GetService<Core.Spam.LocalSpamFilter>().LoadAsync(Shutdown.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StartupDiagnostics.Log("loading the local spam filter", ex);
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

        // Back online: sync now, send what was waiting for a connection, and file any Sent copies still pending.
        GetService<ConnectivityService>().Changed += (_, online) =>
        {
            StartupDiagnostics.Trace("network", online ? "connected" : "offline");
            if (!online || GetService<ISettingsService>().Current.DataMode != DataMode.Live) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await GetService<OutboxService>().ConnectionRestoredAsync(Shutdown.Token);
                    var live = GetService<LiveMailDataSource>();
                    await live.SyncAsync(Shutdown.Token);
                    await live.FilePendingSentCopiesAsync(Shutdown.Token);
                    await live.UploadLocalDraftsAsync(Shutdown.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    StartupDiagnostics.Log("resuming after reconnecting", ex);
                }
            });
        };

        // Real accounts sync in the background (only while real accounts, not the demo, are shown).
        var settings = GetService<ISettingsService>();
        GetService<LiveMailDataSource>().StartBackgroundSync(
            accountId => settings.Current.SyncIntervalFor(accountId),
            () => settings.Current.DataMode == DataMode.Live);
    }

    /// <summary>
    /// Opens and upgrades the mail cache. If it was made by a newer version, or is damaged, the user is told and
    /// decides; nothing is deleted. Returns false if the app should close.
    /// </summary>
    private async Task<bool> PrepareMailCacheAsync()
    {
        var database = GetService<MailDatabase>();
        try
        {
            await database.MigrateAsync();
            return true;
        }
        catch (MailCacheException ex) when (ex.Problem == MailCacheProblem.NewerVersion)
        {
            StartupDiagnostics.Log("mail cache", ex);
            StartupDiagnostics.ShowMessage(ex.Message + "\n\nSidePhase Mail will close now.", warning: false);
            Exit();
            return false;
        }
        catch (MailCacheException ex)
        {
            StartupDiagnostics.Log("mail cache", ex);
            var setAside = StartupDiagnostics.AskYesNo(
                "SidePhase Mail's local mail cache is damaged and can't be opened.\n\n" +
                "Choose Yes to set the damaged file aside (it's kept in the Backups folder) and start with a fresh cache. " +
                "Your mail on the server is safe and downloads again, and saved passwords are kept, but accounts must be added again " +
                "and local-only items (scheduled messages, snoozes, pins, rules) start empty.\n\n" +
                "Choose No to close SidePhase Mail without changing anything.");
            if (!setAside)
            {
                Exit();
                return false;
            }

            var movedTo = database.SetAside();
            StartupDiagnostics.Log("mail cache set aside", new IOException(movedTo));
            await database.MigrateAsync();
            return true;
        }
    }

    private void ReportStartupNotices(DataImportResult import)
    {
        var toasts = GetService<ToastService>();
        var settings = GetService<ISettingsService>();
        if (import.Outcome == DataImportOutcome.Imported)
            toasts.Show("Your accounts and settings were copied from the portable version of SidePhase Mail. Its own copy was left as it was.");
        else if (import.Outcome == DataImportOutcome.Failed)
            toasts.Show("Your data from the portable version of SidePhase Mail couldn't be copied (" + import.Detail + "). It wasn't changed; restart to try again.", ToastKind.Warning);

        if (settings.LoadProblem is { } problem)
            toasts.Show("Your settings couldn't be read (" + problem + "). Defaults are used for now and your settings file wasn't changed; restart SidePhase Mail to try again.", ToastKind.Warning);
        else if (settings.RestoredFromBackup)
            toasts.Show("Your settings file was damaged, so the last good copy was used. The damaged file was kept next to it.", ToastKind.Warning);

        if (settings is SettingsService service)
        {
            service.SaveFailed += (_, ex) =>
            {
                StartupDiagnostics.Log("saving settings", ex);
                toasts.Show("A settings change couldn't be saved (" + ex.Message + "). It applies until SidePhase Mail closes.", ToastKind.Warning);
            };
        }
    }

    /// <summary>Acts on how the app was started or re-activated: a mailto: link opens a new message.</summary>
    private static void HandleActivation(AppActivationArguments args)
    {
        try
        {
            // A notification click that started a second process (handed over here) opens its message.
            if (DesktopNotificationService.MessageFromActivation(args) is { } messageId)
            {
                if (Main is { } window) _ = window.OpenMessageAsync(messageId);
                return;
            }

            var link = args.Kind switch
            {
                ExtendedActivationKind.Protocol when args.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol => protocol.Uri.OriginalString,
                ExtendedActivationKind.Launch when args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch => FindMailto(launch.Arguments),
                _ => null,
            };
            if (Core.Composition.MailtoLink.Parse(link) is { } mailto) _ = GetService<MessageCommandService>().ComposeFromLinkAsync(mailto);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log("handling activation", ex);
        }
    }

    private static string? FindMailto(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;
        var start = arguments.IndexOf("mailto:", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var rest = arguments[start..];
        var end = rest.IndexOf('"');
        return (end < 0 ? rest : rest[..end]).Trim();
    }

    private static ServiceProvider ConfigureServices(AppDataPaths paths)
    {
        var services = new ServiceCollection();

        services.AddSidePhaseMailCore();
        services.AddSidePhaseMailInfrastructure(paths);

        // The footer promotion: a separate module (SidePhase.Mail.Advertising) that is given no mail data. Shows only approved
        // SidePhase Studio promotions bundled with the app (none yet, so the footer stays collapsed); the separate Ads API
        // is used only if explicitly configured (docs/ADVERTISING.md).
        services.AddSingleton(_ =>
        {
            var store = new global::SidePhase.Mail.Advertising.AdvertisingSettingsStore(paths.Root);
            global::SidePhase.Mail.Advertising.AdService? ads = null;
            var entitlement = new global::SidePhase.Mail.Advertising.LocalAdEntitlement(() => ads?.Settings ?? store.Load());
            ads = new global::SidePhase.Mail.Advertising.AdService(store, entitlement, AppIdentity.Version, System.Globalization.CultureInfo.CurrentUICulture.Name,
                promotionsFile: Path.Combine(AppContext.BaseDirectory, "Assets", "Promotions", "promotions.json"));
            return ads;
        });

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
        services.AddSingleton<SmartFolderEditor>();
        services.AddSingleton<ConnectivityService>();
        services.AddSingleton<RuleEditor>();

        // View models
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MailListViewModel>();
        services.AddSingleton<ReadingPaneViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<OutboxViewModel>();
        services.AddSingleton<ContactsViewModel>();
        services.AddTransient<AddAccountViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Log the failure (never message contents or credentials) and keep the app running where possible.
        StartupDiagnostics.LogError("unhandled UI error", e.Exception);

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
