using SidePhase.Mail.Core.Abstractions;

namespace SidePhase.Mail.Core.Settings;

public sealed class SettingsService(ISettingsStore store) : ISettingsService
{
    private readonly Lock _gate = new();
    private AppSettings _current = new();
    private Task _pendingSave = Task.CompletedTask;

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        AppSettings loaded;
        try
        {
            loaded = await store.LoadAsync(cancellationToken).ConfigureAwait(false) ?? new AppSettings();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A corrupt settings file must never prevent the app from starting.
            loaded = new AppSettings();
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
            _pendingSave = SaveAfterAsync(_pendingSave, next);
        }

        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(next));
    }

    public Task FlushAsync()
    {
        lock (_gate) return _pendingSave;
    }

    // Saves are chained so they always reach the store in the order the changes were made.
    private async Task SaveAfterAsync(Task previous, AppSettings snapshot)
    {
        await previous.ConfigureAwait(false);
        try
        {
            await store.SaveAsync(snapshot).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Saving is best effort; the in-memory settings remain authoritative for this session.
        }
    }
}
