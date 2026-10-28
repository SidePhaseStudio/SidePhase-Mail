using SidePhase.Mail.Core.Settings;

namespace SidePhase.Mail.Core.Abstractions;

public sealed class SettingsChangedEventArgs(AppSettings settings) : EventArgs
{
    public AppSettings Settings { get; } = settings;
}

public interface ISettingsService
{
    /// <summary>A snapshot of the current settings. Do not mutate; use <see cref="Update"/>.</summary>
    AppSettings Current { get; }

    event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies a change, raises <see cref="SettingsChanged"/> and saves in the background.</summary>
    void Update(Action<AppSettings> change);

    /// <summary>Waits for any pending save to finish.</summary>
    Task FlushAsync();

    /// <summary>
    /// Set when the settings file couldn't be read at start-up (and wasn't simply missing). Changes made in this
    /// session are then kept in memory only, so the unreadable file is never replaced with defaults.
    /// </summary>
    string? LoadProblem { get; }

    /// <summary>Set when settings were restored from the last good copy because the file was damaged.</summary>
    bool RestoredFromBackup { get; }
}
