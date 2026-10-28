using SidePhase.Mail.Core.Abstractions;

namespace SidePhase.Mail.App.Services;

/// <summary>
/// Brings snoozed messages back when their time comes and announces follow-up reminders. Checks every 30 seconds
/// while the app is running, and straight away at start-up, so anything that came due while the app was closed is
/// handled as soon as it opens. All times are stored in UTC, so clock changes for daylight saving have no effect.
/// </summary>
public sealed class TimedEventsWatcher(
    IMailDataSource mail,
    IClock clock,
    DesktopNotificationService notifications,
    ToastService toasts,
    IUiDispatcher ui) : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Raised on the UI thread after a check found something, so time-based indicators can refresh.</summary>
    public event EventHandler? Ticked;

    public void Start() => _ = RunAsync(_stop.Token);

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct); // let the first list load
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await CheckAsync(ct);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var now = clock.Now;
            var back = await mail.ReleaseExpiredSnoozesAsync(now, ct);
            var due = await mail.TakeDueFollowUpsAsync(now, ct);

            if (back.Count > 0)
            {
                toasts.Show(back.Count == 1
                    ? $"\"{back[0].DisplaySubject}\" is back from snooze."
                    : $"{back.Count} snoozed messages are back.");
            }

            if (due.Count > 0 && !notifications.ShowFollowUps(due))
            {
                toasts.Show(due.Count == 1
                    ? $"Time to follow up: \"{due[0].DisplaySubject}\""
                    : $"{due.Count} follow-ups are due.", ToastKind.Warning);
            }

            if (back.Count > 0 || due.Count > 0) ui.Post(() => Ticked?.Invoke(this, EventArgs.Empty));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed check is retried on the next tick; it must never take the app down.
            StartupDiagnostics.Log("checking snoozes and reminders", ex);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
