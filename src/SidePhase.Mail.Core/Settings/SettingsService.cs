using SidePhase.Mail.Core.Abstractions;

namespace SidePhase.Mail.Core.Settings;

/// <summary>Thrown by a settings store when the file exists but can't be read (locked, no access, disk error).</summary>
public sealed class SettingsUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Result of loading settings: the settings (null if none were saved) and whether a backup was used.</summary>
public sealed record SettingsLoadResult(AppSettings? Settings, bool RestoredFromBackup);

public sealed class SettingsService(ISettingsStore store) : ISettingsService
{
    private readonly Lock _gate = new();
    private AppSettings _current = new();
    private Task _pendingSave = Task.CompletedTask;
    private volatile bool _savesBlocked;

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public string? LoadProblem { get; private set; }

    public bool RestoredFromBackup { get; private set; }

    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        AppSettings loaded;
        try
        {
            var result = store is IRecoveringSettingsStore recovering
                ? await recovering.LoadWithRecoveryAsync(cancellationToken).ConfigureAwait(false)
                : new SettingsLoadResult(await store.LoadAsync(cancellationToken).ConfigureAwait(false), false);
            loaded = result.Settings ?? new AppSettings();
            RestoredFromBackup = result.RestoredFromBackup;
            LoadProblem = null;
            _savesBlocked = false;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // An unreadable settings file must never prevent the app from starting, and must never be overwritten
            // with defaults: run with defaults in memory and leave the file alone until the next start.
            loaded = new AppSettings();
            LoadProblem = ex.Message;
            _savesBlocked = true;
        }

        loaded.Normalize();
        lock (_gate) _current = loaded;
        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(loaded));
    }

    public void Update(Action<AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings next;
        lock (_gate)
        {
            next = _current.Clone();
            change(next);
            next.Normalize();
            _current = next;
            if (!_savesBlocked) _pendingSave = SaveAfterAsync(_pendingSave, next);
        }

        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(next));
    }

    public Task FlushAsync()
    {
        lock (_gate) return _pendingSave;
    }

    /// <summary>Raised (on a background thread) when saving settings failed.</summary>
    public event EventHandler<Exception>? SaveFailed;

    // Saves are chained so they always reach the store in the order the changes were made.
    private async Task SaveAfterAsync(Task previous, AppSettings snapshot)
    {
        await previous.ConfigureAwait(false);
        try
        {
            await store.SaveAsync(snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The in-memory settings remain authoritative for this session; the next change retries the save.
            SaveFailed?.Invoke(this, ex);
        }
    }
}
