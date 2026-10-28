using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Services;
using SidePhase.Mail.Infrastructure.Live;

namespace SidePhase.Mail.App.Services;

/// <summary>
/// Handles mail that arrives during background sync: applies the optional automatic Junk rule, then notifies about
/// what's left. Each message is announced at most once per session, and never for Junk, blocked senders or muted
/// accounts.
/// </summary>
public sealed class NewMailCoordinator(
    LiveMailDataSource live,
    AutoJunkService autoJunk,
    JunkPolicy junk,
    NotificationPolicy policy,
    ISettingsService settings,
    DesktopNotificationService desktop,
    ToastService toasts)
{
    private readonly HashSet<Guid> _announced = [];
    private readonly Lock _gate = new();

    public void Start() => live.NewMailArrived += (_, messages) => _ = HandleAsync(messages);

    internal async Task HandleAsync(IReadOnlyList<MessageSummary> arrived)
    {
        try
        {
            List<MessageSummary> fresh;
            lock (_gate) fresh = arrived.Where(m => _announced.Add(m.Id)).ToList();
            if (fresh.Count == 0) return;

            var movedToJunk = (await autoJunk.ProcessNewMailAsync(fresh).ConfigureAwait(false)).Select(m => m.Id).ToHashSet();
            var filtered = fresh.Count(m => movedToJunk.Contains(m.Id) || junk.IsLocallyJunk(m));
            if (filtered > 0 && settings.Current.NotifyWhenFiltered)
            {
                toasts.Show(filtered == 1 ? "1 new message was filtered into Junk." : $"{filtered} new messages were filtered into Junk.");
            }

            var notify = fresh.Where(m => !movedToJunk.Contains(m.Id) && policy.ShouldNotify(m)).ToList();
            if (notify.Count == 0) return;

            if (!desktop.Show(notify))
            {
                // Windows notifications unavailable or turned off for the app: fall back to an in-app notice.
                var first = notify[0];
                toasts.Show(notify.Count == 1
                    ? $"New message from {first.From.DisplayName}: {first.DisplaySubject}"
                    : $"{notify.Count} new messages");
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log("handling new mail", ex);
        }
    }
}
