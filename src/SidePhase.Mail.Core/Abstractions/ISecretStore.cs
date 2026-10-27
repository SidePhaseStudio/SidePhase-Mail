namespace SidePhase.Mail.Core.Abstractions;

/// <summary>
/// Stores passwords and OAuth refresh tokens encrypted for the current Windows user. Secrets never go into
/// settings.json, the SQLite cache or logs.
/// </summary>
public interface ISecretStore
{
    Task SetAsync(string key, string secret, CancellationToken cancellationToken = default);

    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes every secret whose key starts with <paramref name="prefix"/>.</summary>
    Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default);
}
