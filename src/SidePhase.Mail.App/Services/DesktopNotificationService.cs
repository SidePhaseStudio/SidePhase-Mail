using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.App.Services;

/// <summary>
/// Windows notifications for new mail (Windows App SDK app notifications). Clicking one opens the message. If
/// notifications can't be registered on this PC, <see cref="IsAvailable"/> is false and callers fall back to
/// in-app notices. Windows' own notification settings (per app and Do Not Disturb) always apply.
/// </summary>
public sealed class DesktopNotificationService : IDisposable
{
    private const string MessageIdArgument = "messageId";
    private bool _registered;

    /// <summary>Raised (on a background thread) when the user clicks a message notification.</summary>
    public event EventHandler<Guid>? OpenMessageRequested;

    public bool IsAvailable
    {
        get
        {
            if (!_registered) return false;
            try
            {
                return AppNotificationManager.Default.Setting == AppNotificationSetting.Enabled;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public void Initialize()
    {
        try
        {
            // The handler must be attached before Register so clicks while the app runs are delivered here.
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            // Notifications are a convenience; never let them stop the app from starting.
            StartupDiagnostics.Log("desktop notifications unavailable", ex);
            _registered = false;
        }
    }

    /// <summary>The message to open if this launch came from clicking a notification while the app was closed.</summary>
    public static Guid? MessageFromLaunch()
    {
        try
        {
            var args = AppInstance.GetCurrent().GetActivatedEventArgs();
            return args.Kind == ExtendedActivationKind.AppNotification && args.Data is AppNotificationActivatedEventArgs notification
                ? ParseMessageId(notification.Arguments)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Shows one notification per message (or one summary for many). Returns false if nothing could be shown.</summary>
    public bool Show(IReadOnlyList<MessageSummary> messages)
    {
        if (messages.Count == 0 || !IsAvailable) return false;
        try
        {
            if (messages.Count > 3)
            {
                var latest = messages.OrderByDescending(m => m.Date).First();
                var summary = new AppNotificationBuilder()
                    .AddArgument(MessageIdArgument, latest.Id.ToString())
                    .AddText($"{messages.Count} new messages")
                    .AddText($"Latest from {latest.From.DisplayName}: {latest.DisplaySubject}")
                    .BuildNotification();
                summary.Tag = "summary";
                AppNotificationManager.Default.Show(summary);
                return true;
            }

            foreach (var message in messages)
            {
                var notification = new AppNotificationBuilder()
                    .AddArgument(MessageIdArgument, message.Id.ToString())
                    .AddText(message.From.DisplayName)
                    .AddText(message.DisplaySubject)
                    .AddText(message.Preview)
                    .BuildNotification();

                // Tag = message id, so the same message can never be shown twice.
                notification.Tag = message.Id.ToString("N")[..16];
                notification.Group = message.AccountId.ToString("N")[..16];
                AppNotificationManager.Default.Show(notification);
            }

            return true;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log("showing a notification failed", ex);
            return false;
        }
    }

    public void Dispose()
    {
        if (!_registered) return;
        try
        {
            // Lets a click after the app has closed start SidePhase Mail again.
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception)
        {
        }

        _registered = false;
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (ParseMessageId(args.Arguments) is { } id) OpenMessageRequested?.Invoke(this, id);
    }

    private static Guid? ParseMessageId(IDictionary<string, string> arguments) =>
        arguments.TryGetValue(MessageIdArgument, out var value) && Guid.TryParse(value, out var id) ? id : null;
}
