using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.App.ViewModels;
using SidePhase.Mail.App.Views;
using SidePhase.Mail.Core;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Infrastructure;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.App;

public partial class App : Application
{
    private static IServiceProvider? _services;
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

        _services = ConfigureServices(_paths);

        await GetService<ISettingsService>().LoadAsync();
        GetService<ThemeService>().InitializeResources();

        Main = new MainWindow();
        Main.Activate();
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

        // View models
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MailListViewModel>();
        services.AddSingleton<ReadingPaneViewModel>();
        services.AddSingleton<SettingsViewModel>();

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
