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

    /// <summary>SQLite cache of accounts, folders, message headers, bodies, pins and sync state.</summary>
    public string DatabaseFile => Path.Combine(Root, "mail-cache.db");

    public string LogsFolder => Path.Combine(Root, "Logs");

    /// <summary>Encrypted passwords and OAuth tokens (DPAPI, current Windows user).</summary>
    public string SecretsFolder => Path.Combine(Root, "Secrets");

    public void EnsureCreated() => Directory.CreateDirectory(Root);
}
