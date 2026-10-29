using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Scheduling;

/// <summary>Where an outgoing message is in its life.</summary>
public enum OutboxState
{
    /// <summary>Waiting for its send time (Undo Send window or a scheduled time). Can be edited or cancelled.</summary>
    Queued,

    /// <summary>Being handed to the SMTP server right now.</summary>
    Sending,

    /// <summary>The SMTP server accepted it. Final.</summary>
    Accepted,

    /// <summary>The server refused it, or it couldn't be sent. Nothing was delivered; it can be retried.</summary>
    Failed,

    /// <summary>
    /// The connection broke after the whole message had been sent, so it may or may not have been accepted.
    /// Never retried automatically, to avoid sending it twice.
    /// </summary>
    Uncertain,

    /// <summary>Cancelled by the user before it was sent. Final.</summary>
    Cancelled,
}

public enum OutboxKind
{
    /// <summary>Held for a few seconds so the user can undo the send.</summary>
    UndoWindow,

    /// <summary>Scheduled for a chosen time.</summary>
    Scheduled,
}

/// <summary>How a send attempt ended, as far as the sender can tell.</summary>
public enum SendOutcome
{
    Accepted,

    /// <summary>Definitely not accepted, and retrying won't help without a change (e.g. recipient refused, sign-in failed).</summary>
    Rejected,

    /// <summary>Definitely not accepted, but trying again later may work (e.g. no network before any data was sent).</summary>
    TemporaryFailure,

    /// <summary>The message data was sent but the server's answer never arrived.</summary>
    Uncertain,

    /// <summary>
    /// The outgoing server couldn't be reached at all (offline, no DNS, connection refused, sign-in service down), so
    /// nothing was sent. Retried for as long as it takes, without counting as a failed attempt.
    /// </summary>
    NotConnected,
}

public sealed record SendAttemptResult(SendOutcome Outcome, string Message);

public sealed record OutboxItem
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required DraftMessage Draft { get; init; }
    public required DateTimeOffset SendAt { get; init; }
    public required OutboxKind Kind { get; init; }
    public OutboxState State { get; init; } = OutboxState.Queued;
    public int Attempts { get; init; }
    public string? LastError { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }

    /// <summary>Belongs to the demonstration mailbox (never sent anywhere).</summary>
    public bool IsDemo { get; init; }

    public bool CanEdit => State is OutboxState.Queued;
    public bool CanRetry => State is OutboxState.Failed;
    public bool IsFinished => State is OutboxState.Accepted or OutboxState.Cancelled;
}

public sealed record OutboxAttempt(Guid OutboxId, DateTimeOffset At, SendOutcome Outcome, string Detail);

/// <summary>Persistence for the outbox. <see cref="TryTransitionAsync"/> must be atomic (compare-and-set).</summary>
public interface IOutboxStore
{
    Task AddAsync(OutboxItem item, CancellationToken ct = default);
    Task<OutboxItem?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxItem>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Replaces the item only if it is currently in <paramref name="expected"/> state. Returns false if another
    /// actor changed it first (e.g. the user cancelled while the processor was about to send).
    /// </summary>
    Task<bool> TryTransitionAsync(Guid id, OutboxState expected, OutboxItem updated, CancellationToken ct = default);

    Task AddAttemptAsync(OutboxAttempt attempt, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxAttempt>> GetAttemptsAsync(Guid id, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Hands one message to the outgoing server and classifies the result.</summary>
public interface IOutgoingTransport
{
    Task<SendAttemptResult> SendAsync(OutboxItem item, CancellationToken ct);
}

/// <summary>
/// Keeps private copies of a queued message's attachments, so a scheduled message can still be sent if the original
/// files are moved or deleted in the meantime.
/// </summary>
public interface IOutboxAttachmentStore
{
    Task<DraftMessage> KeepAsync(Guid itemId, DraftMessage draft, CancellationToken ct = default);
    Task ReleaseAsync(Guid itemId, CancellationToken ct = default);
}

public sealed class NoOutboxAttachmentStore : IOutboxAttachmentStore
{
    public Task<DraftMessage> KeepAsync(Guid itemId, DraftMessage draft, CancellationToken ct = default) => Task.FromResult(draft);
    public Task ReleaseAsync(Guid itemId, CancellationToken ct = default) => Task.CompletedTask;
}
