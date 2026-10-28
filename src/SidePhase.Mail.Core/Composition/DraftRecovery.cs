using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Composition;

/// <summary>A message that was being written when SidePhase Mail last closed unexpectedly.</summary>
public sealed record RecoveredDraft(Guid SessionId, DraftMessage Draft, DateTimeOffset SavedAt);

/// <summary>
/// Crash protection for the compose window: every open message is copied to this PC every few seconds while it
/// changes, and the copy is removed when the message is sent, saved as a draft or deliberately closed. Anything
/// left over at start-up was interrupted and can be reopened.
/// </summary>
public interface IDraftRecoveryStore
{
    Task SaveAsync(Guid sessionId, DraftMessage draft, CancellationToken ct = default);
    Task DeleteAsync(Guid sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<RecoveredDraft>> ListAsync(CancellationToken ct = default);
}
