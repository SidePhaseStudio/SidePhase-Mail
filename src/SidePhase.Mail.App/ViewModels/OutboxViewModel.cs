using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Scheduling;

namespace SidePhase.Mail.App.ViewModels;

/// <summary>One message in the outbox: scheduled, waiting out its Undo Send delay, failed, uncertain or sent.</summary>
public sealed partial class OutboxItemViewModel(OutboxItem item, string statusText, string accountText, OutboxViewModel owner) : ObservableObject
{
    public OutboxItem Item { get; } = item;
    public Guid Id => Item.Id;

    public string Subject => string.IsNullOrWhiteSpace(Item.Draft.Subject) ? "(No subject)" : Item.Draft.Subject;

    public string Recipients => "To " + string.Join(", ", Item.Draft.To.Concat(Item.Draft.Cc).Concat(Item.Draft.Bcc).Select(a => a.DisplayName));

    public string AccountText { get; } = accountText;
    public string StatusText { get; } = statusText;
    public string ErrorText => Item.State is OutboxState.Failed or OutboxState.Uncertain ? Item.LastError ?? string.Empty : string.Empty;
    public bool HasError => ErrorText.Length > 0;
    public bool HasAttachments => Item.Draft.Attachments.Count > 0;
    public string AttachmentText => Item.Draft.Attachments.Count switch
    {
        0 => string.Empty,
        1 => "1 attachment",
        var n => $"{n} attachments",
    };

    public bool IsQueued => Item.State == OutboxState.Queued;
    public bool IsSending => Item.State == OutboxState.Sending;
    public bool IsFailed => Item.State == OutboxState.Failed;
    public bool IsUncertain => Item.State == OutboxState.Uncertain;
    public bool IsSent => Item.State == OutboxState.Accepted;
    public bool CanEdit => IsQueued || IsFailed;
    public bool CanDiscard => IsFailed || IsUncertain || IsSent;

    public string StateGlyph => Item.State switch
    {
        OutboxState.Queued => "",
        OutboxState.Sending => "",
        OutboxState.Accepted => "",
        OutboxState.Failed => "",
        OutboxState.Uncertain => "",
        _ => "",
    };

    [RelayCommand] private Task SendNowAsync() => owner.SendNowAsync(this);
    [RelayCommand] private Task EditAsync() => owner.EditAsync(this);
    [RelayCommand] private Task CancelAsync() => owner.CancelAsync(this);
    [RelayCommand] private Task RetryAsync() => owner.RetryAsync(this);
    [RelayCommand] private Task DiscardAsync() => owner.DiscardAsync(this);
    [RelayCommand] private Task MarkSentAsync() => owner.MarkSentAsync(this);
    [RelayCommand] private Task SendAgainAsync() => owner.SendAgainAsync(this);

    /// <param name="choice">A QuickTime name or "Custom".</param>
    [RelayCommand] private Task RescheduleAsync(string choice) => owner.RescheduleAsync(this, choice);
}

/// <summary>
/// The Scheduled page: messages waiting to be sent by this app. Scheduled sending happens on this PC, so SidePhase
/// Mail must be running at the chosen time; nothing is scheduled on the mail server.
/// </summary>
public sealed partial class OutboxViewModel : ObservableObject
{
    private readonly OutboxService _outbox;
    private readonly IMailDataSource _mail;
    private readonly MessageCommandService _commands;
    private readonly WindowService _windows;
    private readonly ToastService _toasts;
    private readonly DialogService _dialogs;
    private readonly IClock _clock;
    private readonly IUiDispatcher _ui;
    private bool _refreshQueued;

    public OutboxViewModel(
        OutboxService outbox,
        IMailDataSource mail,
        MessageCommandService commands,
        WindowService windows,
        ToastService toasts,
        DialogService dialogs,
        IClock clock,
        IUiDispatcher ui)
    {
        _outbox = outbox;
        _mail = mail;
        _commands = commands;
        _windows = windows;
        _toasts = toasts;
        _dialogs = dialogs;
        _clock = clock;
        _ui = ui;
        _outbox.Changed += (_, _) => QueueRefresh();
        _mail.DataChanged += (_, e) =>
        {
            if (e.Kind == MailDataChangeKind.Reset) QueueRefresh();
        };
    }

    public ObservableCollection<OutboxItemViewModel> Pending { get; } = [];
    public ObservableCollection<OutboxItemViewModel> NeedsAttention { get; } = [];
    public ObservableCollection<OutboxItemViewModel> Sent { get; } = [];

    [ObservableProperty] public partial bool HasPending { get; set; }
    [ObservableProperty] public partial bool HasAttention { get; set; }
    [ObservableProperty] public partial bool HasSent { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }

    private void QueueRefresh() => _ui.Post(() =>
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        _ = RefreshSoonAsync();
    });

    private async Task RefreshSoonAsync()
    {
        await Task.Delay(100);
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        _refreshQueued = false;
        var items = await _outbox.GetItemsAsync(_mail.IsDemo);
        var accounts = (await _mail.GetAccountsAsync()).ToDictionary(a => a.Id);
        var now = _clock.Now;

        Pending.Clear();
        NeedsAttention.Clear();
        Sent.Clear();
        foreach (var item in items)
        {
            var account = accounts.TryGetValue(item.AccountId, out var a) ? $"From {a.EmailAddress}" : "From a removed account";
            var model = new OutboxItemViewModel(item, Status(item, now), account, this);
            switch (item.State)
            {
                case OutboxState.Queued or OutboxState.Sending:
                    Pending.Add(model);
                    break;
                case OutboxState.Failed or OutboxState.Uncertain:
                    NeedsAttention.Add(model);
                    break;
                case OutboxState.Accepted:
                    Sent.Add(model);
                    break;
            }
        }

        // Most recent first for sent messages.
        var sent = Sent.OrderByDescending(s => s.Item.AcceptedAt ?? s.Item.UpdatedAt).ToList();
        Sent.Clear();
        foreach (var s in sent.Take(50)) Sent.Add(s);

        HasPending = Pending.Count > 0;
        HasAttention = NeedsAttention.Count > 0;
        HasSent = Sent.Count > 0;
        IsEmpty = !HasPending && !HasAttention && !HasSent;
    }

    private string Status(OutboxItem item, DateTimeOffset now)
    {
        var attempts = item.Attempts > 1 ? $" · {item.Attempts} attempts" : string.Empty;
        return item.State switch
        {
            OutboxState.Queued when item.Kind == OutboxKind.UndoWindow => "Sending in a few seconds",
            OutboxState.Queued when item.Attempts > 0 => $"Trying again {_commands.Describe(item.SendAt)}{attempts}",
            OutboxState.Queued => $"Sends {_commands.Describe(item.SendAt)}",
            OutboxState.Sending => "Sending now…",
            OutboxState.Failed => $"Not sent{attempts}",
            OutboxState.Uncertain => "May or may not have been delivered",
            OutboxState.Accepted => $"Sent {_commands.Describe(item.AcceptedAt ?? item.UpdatedAt)}",
            _ => string.Empty,
        };
    }

    internal async Task SendNowAsync(OutboxItemViewModel model)
    {
        if (!await _outbox.SendNowAsync(model.Id)) TooLate();
    }

    /// <summary>Takes the message out of the outbox and reopens it for editing; it is sent again only from compose.</summary>
    internal async Task EditAsync(OutboxItemViewModel model)
    {
        DraftMessage? draft;
        if (model.IsFailed)
        {
            draft = model.Item.Draft;
            await _outbox.DiscardAsync(model.Id);
        }
        else
        {
            draft = await _outbox.CancelAsync(model.Id);
        }

        if (draft is null)
        {
            TooLate();
            return;
        }

        _windows.OpenCompose(draft with { BodyIncludesSignature = true });
    }

    internal async Task CancelAsync(OutboxItemViewModel model)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Cancel this message?",
            $"\"{model.Subject}\" won't be sent. You can reopen it from the undo notice straight after.",
            "Cancel sending",
            "Keep");
        if (!confirmed) return;

        var draft = await _outbox.CancelAsync(model.Id);
        if (draft is null)
        {
            TooLate();
            return;
        }

        _toasts.ShowWithAction("Message cancelled.", ToastKind.Info, "Reopen", () =>
        {
            _windows.OpenCompose(draft with { BodyIncludesSignature = true });
            return Task.CompletedTask;
        });
    }

    internal async Task RescheduleAsync(OutboxItemViewModel model, string choice)
    {
        var when = await _commands.ResolveTimeAsync(choice, "Reschedule", "Reschedule", model.Item.SendAt);
        if (when is null) return;
        if (await _outbox.RescheduleAsync(model.Id, when.Value)) _toasts.Show($"Rescheduled for {_commands.Describe(when.Value)}.", ToastKind.Success);
        else TooLate();
    }

    internal async Task RetryAsync(OutboxItemViewModel model)
    {
        if (!await _outbox.RetryAsync(model.Id)) TooLate();
    }

    internal async Task DiscardAsync(OutboxItemViewModel model)
    {
        if (model.IsSent)
        {
            await _outbox.ForgetAsync(model.Id);
            return;
        }

        {
            var confirmed = await _dialogs.ConfirmAsync(
                "Discard this message?",
                model.IsUncertain
                    ? $"\"{model.Subject}\" will be removed from the outbox. If it wasn't delivered, it won't be sent."
                    : $"\"{model.Subject}\" will be removed from the outbox without being sent.",
                "Discard");
            if (!confirmed) return;
        }

        await _outbox.DiscardAsync(model.Id);
    }

    internal Task MarkSentAsync(OutboxItemViewModel model) => _outbox.MarkUncertainAsSentAsync(model.Id);

    /// <summary>Sends a message whose earlier attempt is uncertain. Asks first: the recipients may get it twice.</summary>
    internal async Task SendAgainAsync(OutboxItemViewModel model)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Send it again?",
            "The connection dropped after the message was sent, so the server may already have delivered it. " +
            "Check your Sent folder or ask a recipient first — sending again may deliver it twice.",
            "Send again");
        if (confirmed && !await _outbox.ResendUncertainAsync(model.Id)) TooLate();
    }

    private void TooLate() => _toasts.Show("That message has already changed — the list has been updated.", ToastKind.Warning);
}
