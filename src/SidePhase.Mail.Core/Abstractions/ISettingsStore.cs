using SidePhase.Mail.Core.Settings;

namespace SidePhase.Mail.Core.Abstractions;

/// <summary>Persists <see cref="AppSettings"/>. Implemented in the infrastructure layer.</summary>
public interface ISettingsStore
{
    Task<AppSettings?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
