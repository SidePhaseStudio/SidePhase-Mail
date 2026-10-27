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
}
