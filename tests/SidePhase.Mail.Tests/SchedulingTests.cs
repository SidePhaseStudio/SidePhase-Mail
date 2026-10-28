using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Scheduling;

namespace SidePhase.Mail.Tests;

internal static class TestZones
{
    /// <summary>UK-style zone: UTC+0 in winter, UTC+1 from the last Sunday in March 01:00 to the last Sunday in October 02:00.</summary>
    public static readonly TimeZoneInfo London = TimeZoneInfo.CreateCustomTimeZone(
        "Test/London",
        TimeSpan.Zero,
        "Test London",
        "GMT",
        "BST",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date,
                DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);
}

public class QuickTimeTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    [Fact]
    public void Tomorrow_morning_is_8am_local_in_summer_and_winter()
    {
        // Wednesday 15 July 2026, 22:30 UTC = 23:30 BST
        Assert.Equal(Utc(2026, 7, 16, 7), QuickTimes.Resolve(QuickTime.TomorrowMorning, Utc(2026, 7, 15, 22, 30), TestZones.London));
        // Winter: 8:00 GMT = 8:00 UTC
        Assert.Equal(Utc(2026, 12, 2, 8), QuickTimes.Resolve(QuickTime.TomorrowMorning, Utc(2026, 12, 1, 10), TestZones.London));
    }

    [Fact]
    public void Late_evening_utc_can_already_be_tomorrow_locally()
    {
        // 23:30 UTC on 15 July is 00:30 BST on 16 July: "tomorrow" is the 17th.
        Assert.Equal(Utc(2026, 7, 17, 7), QuickTimes.Resolve(QuickTime.TomorrowMorning, Utc(2026, 7, 15, 23, 30), TestZones.London));
    }

    [Fact]
    public void Crossing_the_clocks_going_back_keeps_wall_clock_time()
    {
        // Saturday 24 Oct 2026 10:00 BST; clocks go back on Sunday 25 Oct. Next Monday 08:00 is GMT (08:00 UTC).
        Assert.Equal(Utc(2026, 10, 26, 8), QuickTimes.Resolve(QuickTime.NextWeek, Utc(2026, 10, 24, 9), TestZones.London));
        // Tomorrow morning (Sunday 25th, 08:00 GMT)
        Assert.Equal(Utc(2026, 10, 25, 8), QuickTimes.Resolve(QuickTime.TomorrowMorning, Utc(2026, 10, 24, 9), TestZones.London));
    }

    [Fact]
    public void Skipped_hour_moves_forward_and_repeated_hour_uses_first_occurrence()
    {
        // 29 March 2026: 01:00-02:00 GMT doesn't exist. 01:30 becomes 02:30 BST = 01:30 UTC.
        Assert.Equal(Utc(2026, 3, 29, 1, 30), QuickTimes.FromLocal(new DateOnly(2026, 3, 29), new TimeOnly(1, 30), TestZones.London));
        // 25 Oct 2026: 01:30 happens twice; the first is BST (00:30 UTC).
        Assert.Equal(Utc(2026, 10, 25, 0, 30), QuickTimes.FromLocal(new DateOnly(2026, 10, 25), new TimeOnly(1, 30), TestZones.London));
    }

    [Theory]
    [InlineData(2026, 10, 28, 2026, 10, 31)] // Wednesday -> Saturday
    [InlineData(2026, 10, 31, 2026, 11, 1)]  // Saturday -> Sunday
    [InlineData(2026, 11, 1, 2026, 11, 7)]   // Sunday -> next Saturday
    public void This_weekend(int y, int m, int d, int ey, int em, int ed)
    {
        var result = QuickTimes.Resolve(QuickTime.ThisWeekend, Utc(y, m, d, 12), TestZones.London);
        Assert.Equal(new DateTime(ey, em, ed), TimeZoneInfo.ConvertTime(result, TestZones.London).Date);
        Assert.Equal(9, TimeZoneInfo.ConvertTime(result, TestZones.London).Hour);
    }

    [Fact]
    public void Later_today_is_three_hours_rounded_up_to_the_hour_and_next_week_is_monday()
    {
        Assert.Equal(Utc(2026, 10, 28, 15), QuickTimes.Resolve(QuickTime.LaterToday, Utc(2026, 10, 28, 11, 10), TestZones.London));
        Assert.Equal(Utc(2026, 10, 28, 14), QuickTimes.Resolve(QuickTime.LaterToday, Utc(2026, 10, 28, 11, 0), TestZones.London));
        // Monday -> the following Monday
        var monday = QuickTimes.Resolve(QuickTime.NextWeek, Utc(2026, 11, 2, 9), TestZones.London);
        Assert.Equal(new DateTime(2026, 11, 9), monday.UtcDateTime.Date);
        Assert.Equal(Utc(2026, 10, 31, 9), QuickTimes.Resolve(QuickTime.InThreeDays, Utc(2026, 10, 28, 9), TestZones.London));
    }

    [Fact]
    public void Describe_uses_relative_days()
    {
        var now = Utc(2026, 10, 28, 9);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Assert.StartsWith("Today", QuickTimes.Describe(Utc(2026, 10, 28, 18), now, TestZones.London, culture));
        Assert.StartsWith("Tomorrow", QuickTimes.Describe(Utc(2026, 10, 29, 8), now, TestZones.London, culture));
    }
}

internal sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly Dictionary<Guid, OutboxItem> _items = [];
    private readonly List<OutboxAttempt> _attempts = [];
    private readonly Lock _gate = new();

    public Task AddAsync(OutboxItem item, CancellationToken ct = default)
    {
        lock (_gate) _items.Add(item.Id, item);
        return Task.CompletedTask;
    }

    public Task<OutboxItem?> GetAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_items.GetValueOrDefault(id));
    }

    public Task<IReadOnlyList<OutboxItem>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<OutboxItem>>(_items.Values.ToList());
    }

    public Task<bool> TryTransitionAsync(Guid id, OutboxState expected, OutboxItem updated, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var current) || current.State != expected) return Task.FromResult(false);
            _items[id] = updated;
            return Task.FromResult(true);
        }
    }

    public Task AddAttemptAsync(OutboxAttempt attempt, CancellationToken ct = default)
    {
        lock (_gate) _attempts.Add(attempt);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxAttempt>> GetAttemptsAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<OutboxAttempt>>(_attempts.Where(a => a.OutboxId == id).ToList());
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate) _items.Remove(id);
        return Task.CompletedTask;
    }

    /// <summary>Simulates a crash: puts an item straight into a state.</summary>
    public void Force(Guid id, OutboxState state)
    {
        lock (_gate) _items[id] = _items[id] with { State = state };
    }
}

internal sealed class ScriptedTransport : IOutgoingTransport
{
    public Queue<SendOutcome> Outcomes { get; } = new();
    public List<Guid> Sent { get; } = [];
    public Func<Task>? DuringSend { get; set; }

    public async Task<SendAttemptResult> SendAsync(OutboxItem item, CancellationToken ct)
    {
        Sent.Add(item.Id);
        if (DuringSend is not null) await DuringSend();
        var outcome = Outcomes.Count > 0 ? Outcomes.Dequeue() : SendOutcome.Accepted;
        return new SendAttemptResult(outcome, outcome.ToString());
    }
}

public class OutboxTests
{
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryOutboxStore _store = new();
    private readonly ScriptedTransport _transport = new();
    private readonly OutboxService _outbox;

    public OutboxTests() => _outbox = new OutboxService(_store, _transport, _clock);

    private static DraftMessage Draft() => new() { AccountId = Guid.NewGuid(), To = [new EmailAddress(null, "bob@example.test")], Subject = "Hi" };

    private Task<OutboxItem> QueueIn(TimeSpan delay, OutboxKind kind = OutboxKind.Scheduled) =>
        _outbox.QueueAsync(Draft(), _clock.Now + delay, kind, isDemo: false);

    [Fact]
    public async Task Nothing_is_sent_before_its_time_and_it_is_sent_once_when_due()
    {
        var item = await QueueIn(TimeSpan.FromHours(1));
        Assert.Equal(0, await _outbox.ProcessDueAsync());
        _clock.Now += TimeSpan.FromMinutes(59);
        Assert.Equal(0, await _outbox.ProcessDueAsync());
        _clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal(1, await _outbox.ProcessDueAsync());
        Assert.Equal(0, await _outbox.ProcessDueAsync());
        Assert.Single(_transport.Sent);
        var stored = (await _store.GetAsync(item.Id))!;
        Assert.Equal(OutboxState.Accepted, stored.State);
        Assert.Single(await _outbox.GetAttemptsAsync(item.Id));
    }

    [Fact]
    public async Task Clock_moving_backwards_does_not_send_early()
    {
        await QueueIn(TimeSpan.FromHours(2));
        _clock.Now -= TimeSpan.FromHours(5); // e.g. the system clock is corrected backwards
        Assert.Equal(0, await _outbox.ProcessDueAsync());
        Assert.Empty(_transport.Sent);
    }

    [Fact]
    public async Task Scheduling_in_the_past_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => QueueIn(TimeSpan.FromMinutes(-1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _outbox.QueueAsync(new DraftMessage { AccountId = Guid.NewGuid() }, _clock.Now.AddHours(1), OutboxKind.Scheduled, false));
    }

    [Fact]
    public async Task Undo_cancels_before_sending_and_returns_the_draft()
    {
        var item = await QueueIn(TimeSpan.FromSeconds(10), OutboxKind.UndoWindow);
        var draft = await _outbox.CancelAsync(item.Id);
        Assert.Equal("Hi", draft!.Subject);
        _clock.Now += TimeSpan.FromSeconds(30);
        Assert.Equal(0, await _outbox.ProcessDueAsync());
        Assert.Empty(_transport.Sent);
        Assert.Null(await _outbox.CancelAsync(item.Id)); // already cancelled
    }

    [Fact]
    public async Task Undo_is_too_late_once_sending_has_started()
    {
        var item = await QueueIn(TimeSpan.FromSeconds(5), OutboxKind.UndoWindow);
        _clock.Now += TimeSpan.FromSeconds(5);
        DraftMessage? cancelled = null;
        _transport.DuringSend = async () => cancelled = await _outbox.CancelAsync(item.Id);
        await _outbox.ProcessDueAsync();
        Assert.Null(cancelled);
        Assert.Equal(OutboxState.Accepted, (await _store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Temporary_failures_retry_with_backoff_then_fail()
    {
        var item = await QueueIn(TimeSpan.FromSeconds(1));
        _transport.Outcomes.Enqueue(SendOutcome.TemporaryFailure);
        _transport.Outcomes.Enqueue(SendOutcome.TemporaryFailure);
        _transport.Outcomes.Enqueue(SendOutcome.TemporaryFailure);

        _clock.Now += TimeSpan.FromSeconds(1);
        await _outbox.ProcessDueAsync();
        var afterFirst = (await _store.GetAsync(item.Id))!;
        Assert.Equal(OutboxState.Queued, afterFirst.State);
        Assert.Equal(_clock.Now + TimeSpan.FromMinutes(1), afterFirst.SendAt);
        Assert.Equal(0, await _outbox.ProcessDueAsync()); // backing off

        _clock.Now += TimeSpan.FromMinutes(1);
        await _outbox.ProcessDueAsync();
        _clock.Now += TimeSpan.FromMinutes(5);
        await _outbox.ProcessDueAsync();
        var final = (await _store.GetAsync(item.Id))!;
        Assert.Equal(OutboxState.Failed, final.State);
        Assert.Equal(3, final.Attempts);
        Assert.Equal(3, _transport.Sent.Count);

        // The user can retry a failed message.
        Assert.True(await _outbox.RetryAsync(item.Id));
        await _outbox.ProcessDueAsync();
        Assert.Equal(OutboxState.Accepted, (await _store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Rejections_fail_immediately_without_retrying()
    {
        var item = await QueueIn(TimeSpan.FromSeconds(1));
        _transport.Outcomes.Enqueue(SendOutcome.Rejected);
        _clock.Now += TimeSpan.FromSeconds(1);
        await _outbox.ProcessDueAsync();
        _clock.Now += TimeSpan.FromHours(1);
        await _outbox.ProcessDueAsync();
        Assert.Single(_transport.Sent);
        Assert.Equal(OutboxState.Failed, (await _store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Uncertain_delivery_is_never_retried_automatically()
    {
        var item = await QueueIn(TimeSpan.FromSeconds(1));
        _transport.Outcomes.Enqueue(SendOutcome.Uncertain);
        _clock.Now += TimeSpan.FromSeconds(1);
        await _outbox.ProcessDueAsync();
        _clock.Now += TimeSpan.FromDays(1);
        await _outbox.ProcessDueAsync();
        Assert.Single(_transport.Sent);
        Assert.Equal(OutboxState.Uncertain, (await _store.GetAsync(item.Id))!.State);
        Assert.False(await _outbox.RetryAsync(item.Id)); // only failed items retry

        // The user decides: it's in Sent after all.
        Assert.True(await _outbox.MarkUncertainAsSentAsync(item.Id));
        Assert.Equal(OutboxState.Accepted, (await _store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Transport_exceptions_are_treated_as_uncertain()
    {
        var item = await QueueIn(TimeSpan.FromSeconds(1));
        _transport.DuringSend = () => throw new IOException("boom");
        _clock.Now += TimeSpan.FromSeconds(1);
        await _outbox.ProcessDueAsync();
        Assert.Equal(OutboxState.Uncertain, (await _store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Interrupted_sends_become_uncertain_after_a_restart()
    {
        var item = await QueueIn(TimeSpan.FromSeconds(1));
        _store.Force(item.Id, OutboxState.Sending); // the app died mid-send
        var restarted = new OutboxService(_store, _transport, _clock);
        await restarted.RecoverAsync();
        _clock.Now += TimeSpan.FromMinutes(5);
        await restarted.ProcessDueAsync();
        Assert.Empty(_transport.Sent);
        Assert.Equal(OutboxState.Uncertain, (await _store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task Missed_schedules_are_sent_late_within_a_day_otherwise_held()
    {
        var recent = await QueueIn(TimeSpan.FromHours(1));
        var old = await QueueIn(TimeSpan.FromMinutes(30));
        _clock.Now += TimeSpan.FromHours(24).Add(TimeSpan.FromMinutes(45)); // app wasn't running
        await _outbox.ProcessDueAsync();
        Assert.Equal(OutboxState.Accepted, (await _store.GetAsync(recent.Id))!.State);
        var held = (await _store.GetAsync(old.Id))!;
        Assert.Equal(OutboxState.Failed, held.State);
        Assert.Contains("wasn't running", held.LastError);
        Assert.Single(_transport.Sent);
    }

    [Fact]
    public async Task Queued_messages_can_be_edited_and_rescheduled_but_not_after_sending()
    {
        var item = await QueueIn(TimeSpan.FromHours(1));
        Assert.True(await _outbox.UpdateDraftAsync(item.Id, item.Draft with { Subject = "Edited" }));
        Assert.True(await _outbox.RescheduleAsync(item.Id, _clock.Now.AddHours(3)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _outbox.RescheduleAsync(item.Id, _clock.Now.AddHours(-1)));
        Assert.True(await _outbox.SendNowAsync(item.Id));
        await _outbox.ProcessDueAsync();
        var sent = (await _store.GetAsync(item.Id))!;
        Assert.Equal("Edited", sent.Draft.Subject);
        Assert.False(await _outbox.UpdateDraftAsync(item.Id, item.Draft));
    }

    [Fact]
    public async Task Concurrent_processing_sends_once()
    {
        await QueueIn(TimeSpan.FromSeconds(1));
        _clock.Now += TimeSpan.FromSeconds(1);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _outbox.ProcessDueAsync()));
        Assert.Single(_transport.Sent);
    }

    [Fact]
    public async Task Demo_and_real_items_are_listed_separately()
    {
        await _outbox.QueueAsync(Draft(), _clock.Now.AddHours(1), OutboxKind.Scheduled, isDemo: true);
        await QueueIn(TimeSpan.FromHours(1));
        Assert.Single(await _outbox.GetItemsAsync(isDemo: true));
        Assert.Single(await _outbox.GetItemsAsync(isDemo: false));
    }

    [Fact]
    public async Task Only_finished_messages_can_be_removed_from_the_history()
    {
        var item = await QueueIn(TimeSpan.FromMinutes(5));
        Assert.False(await _outbox.ForgetAsync(item.Id)); // still waiting to send

        _clock.Now += TimeSpan.FromMinutes(5);
        await _outbox.ProcessDueAsync();
        Assert.True(await _outbox.ForgetAsync(item.Id));
        Assert.Empty(await _outbox.GetItemsAsync(isDemo: false));
        Assert.Single(_transport.Sent);
    }

    [Fact]
    public async Task Undo_window_messages_are_sent_after_their_short_delay()
    {
        var item = await QueueIn(TimeSpan.FromSeconds(10), OutboxKind.UndoWindow);
        _clock.Now += TimeSpan.FromSeconds(9);
        Assert.Equal(0, await _outbox.ProcessDueAsync());
        _clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(1, await _outbox.ProcessDueAsync());
        Assert.Equal(OutboxState.Accepted, (await _store.GetAsync(item.Id))!.State);
    }
}

public class SnoozeAndFollowUpTests
{
    [Fact]
    public void Snoozed_messages_only_appear_in_the_snoozed_view()
    {
        var snoozed = Samples.Message() with { SnoozedUntil = Samples.Now.AddHours(3) };
        var followUp = Samples.Message() with { FollowUpAt = Samples.Now.AddDays(1) };
        static bool Junk(MessageSummary _) => false;
        Assert.False(Core.Services.MailboxScopeMatcher.Matches(snoozed, MailboxScope.UnifiedInbox, Junk));
        Assert.False(Core.Services.MailboxScopeMatcher.Matches(snoozed with { IsPinned = true }, MailboxScope.Pinned, Junk));
        Assert.True(Core.Services.MailboxScopeMatcher.Matches(snoozed, MailboxScope.Snoozed, Junk));
        Assert.True(Core.Services.MailboxScopeMatcher.Matches(followUp, MailboxScope.FollowUps, Junk));
        Assert.True(Core.Services.MailboxScopeMatcher.Matches(followUp, MailboxScope.UnifiedInbox, Junk));
        Assert.False(Core.Services.MailboxScopeMatcher.Matches(followUp with { FolderKind = FolderKind.Trash }, MailboxScope.FollowUps, Junk));
    }

    [Fact]
    public async Task Demo_source_snoozes_and_releases()
    {
        var clock = new FixedClock(Samples.Now);
        var demo = new Infrastructure.Demo.DemoMailDataSource(clock, TimeSpan.Zero);
        var message = (await demo.GetMessagesAsync()).First(m => m.FolderKind == FolderKind.Inbox);
        await demo.SetSnoozeAsync([message.Id], Samples.Now.AddHours(1));
        Assert.True((await demo.GetMessageAsync(message.Id))!.IsSnoozed);
        Assert.Empty(await demo.ReleaseExpiredSnoozesAsync(Samples.Now));
        Assert.Equal(message.Id, Assert.Single(await demo.ReleaseExpiredSnoozesAsync(Samples.Now.AddHours(1))).Id);
        Assert.False((await demo.GetMessageAsync(message.Id))!.IsSnoozed);

        await demo.SetFollowUpAsync([message.Id], Samples.Now.AddMinutes(5));
        Assert.Empty(await demo.TakeDueFollowUpsAsync(Samples.Now));
        Assert.Single(await demo.TakeDueFollowUpsAsync(Samples.Now.AddMinutes(5)));
        Assert.Empty(await demo.TakeDueFollowUpsAsync(Samples.Now.AddMinutes(10)));
    }
}
