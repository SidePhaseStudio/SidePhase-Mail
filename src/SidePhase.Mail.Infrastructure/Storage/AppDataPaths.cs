namespace SidePhase.Mail.Infrastructure.Storage;

/// <summary>Locations of SidePhase Mail's local data (under %LOCALAPPDATA%\SidePhase Mail on Windows).</summary>
public sealed class AppDataPaths
{
    public AppDataPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "SidePhase Mail");
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>WebView2 profile used to render messages. Holds no account credentials.</summary>
    public string WebViewDataFolder => Path.Combine(Root, "WebView2");

    /// <summary>Future SQLite message cache (Milestone 2).</summary>
    public string DatabaseFile => Path.Combine(Root, "mail-cache.db");

    public string LogsFolder => Path.Combine(Root, "Logs");

    public void EnsureCreated() => Directory.CreateDirectory(Root);
}
