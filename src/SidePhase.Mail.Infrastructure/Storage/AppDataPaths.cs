namespace SidePhase.Mail.Infrastructure.Storage;

/// <summary>
/// Locations of SidePhase Mail's local data. The portable (unpackaged) build keeps it in
/// %LOCALAPPDATA%\SidePhase Mail; the installed (MSIX) app keeps it in its own package data folder
/// (%LOCALAPPDATA%\Packages\&lt;package&gt;\LocalState), which Windows keeps across updates and removes on uninstall.
/// The app chooses the root once at start-up with <see cref="UseDefaultRoot"/>.
/// </summary>
public sealed class AppDataPaths
{
    private static string? _defaultRoot;

    public AppDataPaths(string? root = null)
    {
        Root = root ?? DefaultRoot;
    }

    /// <summary>Where the portable build keeps its data (and where data from before MSIX packaging lives).</summary>
    public static string PortableRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "SidePhase Mail");

    /// <summary>The data root used when none is given: set by the app at start-up, otherwise the portable location.</summary>
    public static string DefaultRoot => _defaultRoot ?? PortableRoot;

    /// <summary>Sets the data root for this process (called once, before anything reads or writes data).</summary>
    public static void UseDefaultRoot(string root) => _defaultRoot = root;

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>WebView2 profile used to render messages. Holds no account credentials.</summary>
    public string WebViewDataFolder => Path.Combine(Root, "WebView2");

    /// <summary>SQLite cache of accounts, folders, message headers, bodies, pins and sync state.</summary>
    public string DatabaseFile => Path.Combine(Root, "mail-cache.db");

    public string LogsFolder => Path.Combine(Root, "Logs");

    /// <summary>Encrypted passwords and OAuth tokens (DPAPI, current Windows user).</summary>
    public string SecretsFolder => Path.Combine(Root, "Secrets");

    /// <summary>Copies of the database taken before schema upgrades (and set aside if it can't be opened).</summary>
    public string BackupsFolder => Path.Combine(Root, "Backups");

    public void EnsureCreated() => Directory.CreateDirectory(Root);
}
