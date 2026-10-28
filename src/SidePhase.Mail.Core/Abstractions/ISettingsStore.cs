using SidePhase.Mail.Core.Settings;

namespace SidePhase.Mail.Core.Abstractions;

/// <summary>Persists <see cref="AppSettings"/>. Implemented in the infrastructure layer.</summary>
public interface ISettingsStore
{
    Task<AppSettings?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>A settings store that can fall back to the last good copy when the file is damaged.</summary>
public interface IRecoveringSettingsStore : ISettingsStore
{
    /// <summary>
    /// Loads settings. A damaged file is kept aside and the last good copy is used if there is one. Throws
    /// <see cref="SettingsUnavailableException"/> if the file exists but can't be read at all.
    /// </summary>
    Task<SettingsLoadResult> LoadWithRecoveryAsync(CancellationToken cancellationToken = default);
}
