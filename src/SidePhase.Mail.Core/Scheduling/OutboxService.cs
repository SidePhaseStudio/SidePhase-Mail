using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Scheduling;

/// <summary>
/// Sends queued messages (Undo Send window, Schedule send) at the right time, exactly once.
/// </summary>
/// <remarks>
/// <para>Every state change is a compare-and-set, so a cancel and a send can never both win.</para>
/// <para>A message is only retried automatically when it definitely wasn't accepted and no message data reached the
/// server. If the connection fails after the data was sent, the item becomes <see cref="OutboxState.Uncertain"/> and
/// waits for the user, because retrying could deliver it twice. The same applies to anything left mid-send when the
/// app closed.</para>
/// <para>Times are compared in UTC. A scheduled message is never sent before its time; one whose time passed while
/// SidePhase Mail wasn't running is sent when the app next runs, unless it's more than
/// <see cref="MissedSendLimit"/> late, in which case it waits for the user to decide.</para>
/// </remarks>
public sealed class OutboxService(IOutboxStore store, IOutgoingTransport transport, IClock clock, IOutboxAttachmentStore? attachments = null)
{
    private readonly IOutboxAttachmentStore _attachments = attachments ?? new NoOutboxAttachmentStore();

    public static readonly TimeSpan MissedSendLimit = TimeSpan.FromHours(24);
    public const int MaxAutomaticAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    private readonly SemaphoreSlim _processing = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);

    /// <summary>Raised after any item changes (may be raised on a background thread).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when an item reaches Accepted, Failed or Uncertain after a send attempt.</summary>
    public event EventHandler<OutboxItem>? AttemptFinished;

    public async Task<OutboxItem> QueueAsync(DraftMessage draft, DateTimeOffset sendAt, OutboxKind kind, bool isDemo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!draft.HasAnyRecipient) throw new InvalidOperationException("Add at least one recipient before sending.");
        var now = clock.Now;
        if (kind == OutboxKind.Scheduled && sendAt <= now) throw new InvalidOperationException("Choose a time in the future.");

        var id = Guid.NewGuid();
        var kept = await _attachments.KeepAsync(id, draft, ct).ConfigureAwait(false);
        var item = new OutboxItem
        {
            Id = id,
            AccountId = draft.AccountId,
            Draft = kept,
            SendAt = sendAt.ToUniversalTime(),
            Kind = kind,
            CreatedAt = now,
            UpdatedAt = now,
            IsDemo = isDemo,
        };
        await store.AddAsync(item, ct).ConfigureAwait(false);
        OnChanged();
        return item;
    }

    public async Task<IReadOnlyList<OutboxItem>> GetItemsAsync(bool isDemo, CancellationToken ct = default) =>
        (await store.ListAsync(ct).ConfigureAwait(false)).Where(i => i.IsDemo == isDemo).OrderBy(i => i.SendAt).ToList();

    public Task<IReadOnlyList<OutboxAttempt>> GetAttemptsAsync(Guid id, CancellationToken ct = default) => store.GetAttemptsAsync(id, ct);

    /// <summary>Cancels a message that hasn't started sending. Returns the draft so it can be reopened, or null if too late.</summary>
    public async Task<DraftMessage?> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var item = await store.GetAsync(id, ct).ConfigureAwait(false);
        if (item is not { State: OutboxState.Queued }) return null;
        if (!await store.TryTransitionAsync(id, OutboxState.Queued, item with { State = OutboxState.Cancelled, UpdatedAt = clock.Now }, ct).ConfigureAwait(false)) return null;
        OnChanged();
        // The attachment copies are kept until the item is pruned, so the reopened draft can still use them.
        return item.Draft;
    }

    public Task<bool> RescheduleAsync(Guid id, DateTimeOffset sendAt, CancellationToken ct = default) =>
        sendAt <= clock.Now
            ? throw new InvalidOperationException("Choose a time in the future.")
            : UpdateQueuedAsync(id, i => i with { SendAt = sendAt.ToUniversalTime(), Kind = OutboxKind.Scheduled }, ct);

    public async Task<bool> UpdateDraftAsync(Guid id, DraftMessage draft, CancellationToken ct = default)
    {
        var kept = await _attachments.KeepAsync(id, draft, ct).ConfigureAwait(false);
        return await UpdateQueuedAsync(id, i => i with { Draft = kept, AccountId = kept.AccountId }, ct).ConfigureAwait(false);
    }

    /// <summary>Sends a queued message straight away.</summary>
    public async Task<bool> SendNowAsync(Guid id, CancellationToken ct = default)
    {
        var ok = await UpdateQueuedAsync(id, i => i with { SendAt = clock.Now }, ct).ConfigureAwait(false);
        if (ok) Wake();
        return ok;
    }

    /// <summary>Tries a failed message again (user action).</summary>
    public Task<bool> RetryAsync(Guid id, CancellationToken ct = default) =>
        TransitionAsync(id, OutboxState.Failed, i => i with { State = OutboxState.Queued, SendAt = clock.Now, LastError = null }, wake: true, ct);

    /// <summary>
    /// Sends an uncertain message again. Only on explicit user request, after they've checked it isn't already in
    /// Sent or with the recipient.
    /// </summary>
    public Task<bool> ResendUncertainAsync(Guid id, CancellationToken ct = default) =>
        TransitionAsync(id, OutboxState.Uncertain, i => i with { State = OutboxState.Queued, SendAt = clock.Now, LastError = null }, wake: true, ct);

    /// <summary>The user confirmed an uncertain message did go out.</summary>
    public Task<bool> MarkUncertainAsSentAsync(Guid id, CancellationToken ct = default) =>
        TransitionAsync(id, OutboxState.Uncertain, i => i with { State = OutboxState.Accepted, AcceptedAt = clock.Now }, wake: false, ct);

    /// <summary>Removes a failed or uncertain message from the outbox.</summary>
    public async Task<bool> DiscardAsync(Guid id, CancellationToken ct = default)
    {
        var item = await store.GetAsync(id, ct).ConfigureAwait(false);
        if (item is null || item.State is not (OutboxState.Failed or OutboxState.Uncertain)) return false;
        return await TransitionAsync(id, item.State, i => i with { State = OutboxState.Cancelled }, wake: false, ct).ConfigureAwait(false);
    }

    /// <summary>Removes finished items (sent or cancelled) older than <paramref name="age"/>.</summary>
    public async Task PruneAsync(TimeSpan age, CancellationToken ct = default)
    {
        var cutoff = clock.Now - age;
        foreach (var item in await store.ListAsync(ct).ConfigureAwait(false))
        {
            if (!item.IsFinished || item.UpdatedAt >= cutoff) continue;
            await store.DeleteAsync(item.Id, ct).ConfigureAwait(false);
            await _attachments.ReleaseAsync(item.Id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Call once at start-up: anything still marked Sending was interrupted (crash, power cut), so its delivery is
    /// unknown.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        var changed = false;
        foreach (var item in await store.ListAsync(ct).ConfigureAwait(false))
        {
            if (item.State != OutboxState.Sending) continue;
            var uncertain = item with
            {
                State = OutboxState.Uncertain,
                UpdatedAt = clock.Now,
                LastError = "SidePhase Mail closed while this message was being sent, so it may or may not have been delivered. Check your Sent folder before sending it again.",
            };
            changed |= await store.TryTransitionAsync(item.Id, OutboxState.Sending, uncertain, ct).ConfigureAwait(false);
        }

        if (changed) OnChanged();
    }

    /// <summary>Sends everything that is due. Returns how many attempts were made.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        await _processing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var attempts = 0;
            var now = clock.Now;
            var due = (await store.ListAsync(ct).ConfigureAwait(false))
                .Where(i => i.State == OutboxState.Queued && i.SendAt <= now)
                .OrderBy(i => i.SendAt)
                .ToList();

            foreach (var item in due)
            {
                ct.ThrowIfCancellationRequested();
                if (now - item.SendAt > MissedSendLimit)
                {
                    var missed = item with
                    {
                        State = OutboxState.Failed,
                        UpdatedAt = now,
                        LastError = $"This was scheduled for {item.SendAt.ToLocalTime():g} but SidePhase Mail wasn't running then. Review it and send it again if it's still relevant.",
                    };
                    if (await store.TryTransitionAsync(item.Id, OutboxState.Queued, missed, ct).ConfigureAwait(false)) OnChanged();
                    continue;
                }

                var sending = item with { State = OutboxState.Sending, Attempts = item.Attempts + 1, UpdatedAt = now };
                if (!await store.TryTransitionAsync(item.Id, OutboxState.Queued, sending, ct).ConfigureAwait(false)) continue; // cancelled or edited meanwhile
                OnChanged();
                attempts++;

                SendAttemptResult result;
                try
                {
                    result = await transport.SendAsync(sending, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // We can't tell how far it got: treat as uncertain rather than risk a duplicate.
                    result = new SendAttemptResult(SendOutcome.Uncertain, "Sending stopped unexpectedly: " + ex.Message);
                }

                var finished = clock.Now;
                await store.AddAttemptAsync(new OutboxAttempt(item.Id, finished, result.Outcome, result.Message), CancellationToken.None).ConfigureAwait(false);
                var next = result.Outcome switch
                {
                    SendOutcome.Accepted => sending with { State = OutboxState.Accepted, AcceptedAt = finished, UpdatedAt = finished, LastError = null },
                    SendOutcome.TemporaryFailure when sending.Attempts < MaxAutomaticAttempts => sending with
                    {
                        State = OutboxState.Queued,
                        SendAt = finished + RetryDelays[Math.Min(sending.Attempts - 1, RetryDelays.Length - 1)],
                        UpdatedAt = finished,
                        LastError = result.Message + " Trying again shortly.",
                    },
                    SendOutcome.Uncertain => sending with { State = OutboxState.Uncertain, UpdatedAt = finished, LastError = result.Message },
                    _ => sending with { State = OutboxState.Failed, UpdatedAt = finished, LastError = result.Message },
                };

                await store.TryTransitionAsync(item.Id, OutboxState.Sending, next, CancellationToken.None).ConfigureAwait(false);
                OnChanged();
                if (next.State is OutboxState.Accepted or OutboxState.Failed or OutboxState.Uncertain) AttemptFinished?.Invoke(this, next);
            }

            return attempts;
        }
        finally
        {
            _processing.Release();
        }
    }

    /// <summary>When the next queued item is due, if any.</summary>
    public async Task<DateTimeOffset?> NextDueAsync(CancellationToken ct = default)
    {
        var queued = (await store.ListAsync(ct).ConfigureAwait(false)).Where(i => i.State == OutboxState.Queued).ToList();
        return queued.Count == 0 ? null : queued.Min(i => i.SendAt);
    }

    /// <summary>
    /// Runs until cancelled: recovers interrupted sends, then sends items as they fall due. Wakes early when items are
    /// queued or changed, and re-checks at least every 30 seconds (so clock changes are noticed).
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        await RecoverAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessDueAsync(ct).ConfigureAwait(false);
                var next = await NextDueAsync(ct).ConfigureAwait(false);
                var wait = next is { } when ? when - clock.Now : TimeSpan.FromSeconds(30);
                wait = TimeSpan.FromMilliseconds(Math.Clamp(wait.TotalMilliseconds, 200, 30_000));
                await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // A storage problem shouldn't stop future sends; try again shortly.
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
    }

    public void Wake() => _wake.Release();

    private Task<bool> UpdateQueuedAsync(Guid id, Func<OutboxItem, OutboxItem> change, CancellationToken ct) =>
        TransitionAsync(id, OutboxState.Queued, change, wake: true, ct);

    private async Task<bool> TransitionAsync(Guid id, OutboxState expected, Func<OutboxItem, OutboxItem> change, bool wake, CancellationToken ct)
    {
        var item = await store.GetAsync(id, ct).ConfigureAwait(false);
        if (item is null || item.State != expected) return false;
        var updated = change(item) with { UpdatedAt = clock.Now };
        if (!await store.TryTransitionAsync(id, expected, updated, ct).ConfigureAwait(false)) return false;
        OnChanged();
        if (wake) Wake();
        return true;
    }

    private void OnChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        Wake();
    }
}
