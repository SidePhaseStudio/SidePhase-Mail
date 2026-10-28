using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SidePhase.Mail.App.Services;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Composition;
using SidePhase.Mail.Core.Contacts;
using SidePhase.Mail.Core.Formatting;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Scheduling;

namespace SidePhase.Mail.App.ViewModels;

/// <summary>The body text supplied by the editor control (rich or plain).</summary>
public sealed record ComposeBody(string Text, string? Html);

public sealed partial class ComposeAttachmentViewModel(DraftAttachment attachment, Action<ComposeAttachmentViewModel> remove)
{
    public DraftAttachment Attachment { get; } = attachment;
    public string FileName => Attachment.FileName;
    public string SizeText => FileSizeFormatter.Format(Attachment.SizeBytes);

    [RelayCommand]
    private void Remove() => remove(this);
}

public sealed partial class ComposeViewModel : ObservableObject
{
    /// <summary>Larger files are refused; most providers reject messages over ~25 MB.</summary>
    public const long MaxTotalAttachmentBytes = 25L * 1024 * 1024;

    private readonly IMailDataSource _mail;
    private readonly ToastService _toasts;
    private readonly ISettingsService _settings;
    private readonly OutboxService _outbox;
    private readonly WindowService _windows;
    private readonly IClock _clock;
    private readonly ContactService _contacts;
    private readonly IDraftRecoveryStore _recovery;
    private readonly DraftMessage _initial;
    private Guid? _draftId;

    public ComposeViewModel(
        IMailDataSource mail,
        ISettingsService settings,
        ToastService toasts,
        OutboxService outbox,
        WindowService windows,
        IClock clock,
        ContactService contacts,
        IDraftRecoveryStore recovery,
        DraftMessage draft)
    {
        _contacts = contacts;
        _recovery = recovery;
        _mail = mail;
        _settings = settings;
        _toasts = toasts;
        _outbox = outbox;
        _windows = windows;
        _clock = clock;
        _initial = draft;
        _draftId = draft.DraftId;
        SelectedAccountIndex = -1;

        To = RecipientParser.Format(draft.To);
        Cc = RecipientParser.Format(draft.Cc);
        Bcc = RecipientParser.Format(draft.Bcc);
        ShowCcBcc = draft.Cc.Count + draft.Bcc.Count > 0;
        Subject = draft.Subject;
        IsPlainText = draft.IsPlainText;
        StatusText = mail.IsDemo ? "Demo mode — sending saves to Sent without delivering anything." : string.Empty;
        foreach (var attachment in draft.Attachments)
        {
            Attachments.Add(new ComposeAttachmentViewModel(attachment, RemoveAttachment));
        }

        WindowTitle = draft.Mode switch
        {
            ComposeMode.Reply => "Reply",
            ComposeMode.ReplyAll => "Reply all",
            ComposeMode.Forward => "Forward",
            ComposeMode.EditDraft => "Edit draft",
            _ => "New message",
        };

        IsDirty = false;
    }

    /// <summary>Provided by the window: reads the editor's current content.</summary>
    public Func<ComposeBody>? BodyProvider { get; set; }

    /// <summary>Provided by the window: turns a schedule choice into a time (asking for one for "Custom").</summary>
    public Func<string, Task<DateTimeOffset?>>? PickScheduleTime { get; set; }

    public event EventHandler? CloseRequested;

    public ObservableCollection<MailAccount> Accounts { get; } = [];

    public ObservableCollection<ComposeAttachmentViewModel> Attachments { get; } = [];

    public string WindowTitle { get; }

    public bool IsDemo => _mail.IsDemo;

    /// <summary>Body text to load into the editor initially (drafts) — signature is added separately.</summary>
    public string InitialBodyText => _initial.TextBody;

    public string? QuotedText => _initial.QuotedText;

    public bool HasQuotedText => !string.IsNullOrEmpty(_initial.QuotedText);

    public bool IsNewMessage => _initial.Mode is ComposeMode.New or ComposeMode.Reply or ComposeMode.ReplyAll or ComposeMode.Forward;

    public bool BodyIncludesSignature => _initial.BodyIncludesSignature;

    [ObservableProperty]
    public partial int SelectedAccountIndex { get; set; }

    /// <summary>"No signature" followed by the signature library.</summary>
    public IReadOnlyList<string> SignatureNames => ["No signature", .. _settings.Current.SignatureLibrary.Select(s => s.Name)];

    /// <summary>0 = no signature; otherwise 1 + the index in the library.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Signature))]
    public partial int SelectedSignatureIndex { get; set; }

    private bool _signatureChosenByUser;
    private bool _settingSignature;

    public bool HasSignatures => _settings.Current.SignatureLibrary.Count > 0;

    [ObservableProperty] public partial string To { get; set; }
    [ObservableProperty] public partial string Cc { get; set; }
    [ObservableProperty] public partial string Bcc { get; set; }
    [ObservableProperty] public partial bool ShowCcBcc { get; set; }
    [ObservableProperty] public partial string Subject { get; set; }
    [ObservableProperty] public partial bool IsPlainText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(SaveDraftCommand))]
    public partial bool IsSending { get; set; }

    [ObservableProperty] public partial double SendProgress { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty] public partial bool StatusIsError { get; set; }

    /// <summary>Unsaved changes. Every change also bumps a version so autosave knows what it has already saved.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            _isDirty = value;
            if (value) _changeVersion++;
        }
    }

    private bool _isDirty;
    private int _changeVersion;
    private int _recoveredVersion;
    private int _serverSavedVersion;
    private readonly CancellationTokenSource _autosaveStop = new();

    /// <summary>Identifies this window's crash-recovery copy.</summary>
    public Guid SessionId { get; } = Guid.NewGuid();

    /// <summary>A crash-recovery copy is written this often while the message changes.</summary>
    public static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(5);

    /// <summary>Changes are also saved to the account's Drafts folder this often.</summary>
    public static readonly TimeSpan DraftAutosaveInterval = TimeSpan.FromMinutes(2);

    public bool HasAttachments => Attachments.Count > 0;

    public MailAccount? SelectedAccount =>
        SelectedAccountIndex >= 0 && SelectedAccountIndex < Accounts.Count ? Accounts[SelectedAccountIndex] : null;

    public string Signature
    {
        get
        {
            var library = _settings.Current.SignatureLibrary;
            return SelectedSignatureIndex > 0 && SelectedSignatureIndex <= library.Count ? library[SelectedSignatureIndex - 1].Text : string.Empty;
        }
    }

    partial void OnSelectedSignatureIndexChanged(int value)
    {
        if (!_settingSignature) _signatureChosenByUser = true;
    }

    /// <summary>Switching account switches to that account's default signature, unless one was picked by hand.</summary>
    partial void OnSelectedAccountIndexChanged(int value)
    {
        if (_signatureChosenByUser || SelectedAccount is not { } account || !IsNewMessage || BodyIncludesSignature) return;
        var library = _settings.Current.SignatureLibrary;
        var preferred = _settings.Current.DefaultSignatureFor(account.Id);
        _settingSignature = true;
        SelectedSignatureIndex = preferred is null ? 0 : library.FindIndex(x => x.Id == preferred.Id) + 1;
        _settingSignature = false;
    }

    public async Task InitializeAsync()
    {
        foreach (var account in await _mail.GetAccountsAsync()) Accounts.Add(account);
        var index = Accounts.ToList().FindIndex(a => a.Id == _initial.AccountId);
        SelectedAccountIndex = index >= 0 ? index : (Accounts.Count > 0 ? 0 : -1);
        _ = AutosaveLoopAsync(_autosaveStop.Token);
    }

    /// <summary>
    /// Keeps a crash-recovery copy on this PC every few seconds, and saves to Drafts every couple of minutes, while
    /// the message is being changed. Runs on the UI thread (it reads the editor).
    /// </summary>
    private async Task AutosaveLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(RecoveryInterval);
        var lastServerSave = _clock.Now;
        var reported = false;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (IsSending) continue;
                var version = _changeVersion;

                // A failure (disk, network) is reported once and retried on the next tick: autosave never stops.
                try
                {
                    if (version != _recoveredVersion && BuildDraft(out _, requireRecipients: false) is { } copy)
                    {
                        await _recovery.SaveAsync(SessionId, copy with { DraftId = _draftId }, ct);
                        _recoveredVersion = version;
                    }

                    if (IsDirty && version != _serverSavedVersion && _clock.Now - lastServerSave >= DraftAutosaveInterval && HasContent())
                    {
                        lastServerSave = _clock.Now;
                        if (BuildDraft(out _, requireRecipients: false) is { } draft)
                        {
                            _draftId = await _mail.SaveDraftAsync(draft, ct);
                            _serverSavedVersion = version;
                            if (_changeVersion == version) IsDirty = false;
                            SetStatus($"Draft saved automatically at {DateTime.Now.ToString("t", CultureInfo.CurrentCulture)}", error: false);
                        }
                    }

                    reported = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (!reported) SetStatus("Couldn't save the draft automatically (" + ex.Message + "). Trying again shortly.", error: true);
                    reported = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool HasContent() =>
        !string.IsNullOrWhiteSpace(To) || !string.IsNullOrWhiteSpace(Cc) || !string.IsNullOrWhiteSpace(Bcc)
        || !string.IsNullOrWhiteSpace(Subject) || Attachments.Count > 0
        || (BodyProvider?.Invoke().Text.Trim().Length ?? 0) > Signature.Trim().Length;

    /// <summary>Called when the window closes normally: the crash-recovery copy is no longer needed.</summary>
    public async Task CloseAsync()
    {
        _autosaveStop.Cancel();
        await _recovery.DeleteAsync(SessionId);
    }

    partial void OnToChanged(string value) => IsDirty = true;
    partial void OnCcChanged(string value) => IsDirty = true;
    partial void OnBccChanged(string value) => IsDirty = true;
    partial void OnSubjectChanged(string value) => IsDirty = true;

    [RelayCommand]
    private void ToggleCcBcc()
    {
        // Never hide addresses that will be sent.
        if (ShowCcBcc && (!string.IsNullOrWhiteSpace(Cc) || !string.IsNullOrWhiteSpace(Bcc)))
        {
            SetStatus("Cc and Bcc stay visible while they contain addresses.", error: false);
            return;
        }

        ShowCcBcc = !ShowCcBcc;
    }

    public void AddFiles(IEnumerable<(string Path, string Name, long Size, string ContentType)> files)
    {
        var total = Attachments.Sum(a => a.Attachment.SizeBytes);
        var skipped = new List<string>();
        var risky = new List<string>();
        foreach (var file in files)
        {
            if (Attachments.Any(a => string.Equals(a.Attachment.SourcePath, file.Path, StringComparison.OrdinalIgnoreCase))) continue;
            if (total + file.Size > MaxTotalAttachmentBytes)
            {
                skipped.Add(file.Name);
                continue;
            }

            total += file.Size;

            // Two files with the same name would arrive as indistinguishable attachments: number the later one.
            var name = AttachmentSafety.UniqueName(
                AttachmentSafety.SafeFileName(file.Name),
                n => Attachments.Any(a => string.Equals(a.FileName, n, StringComparison.OrdinalIgnoreCase)));
            Attachments.Add(new ComposeAttachmentViewModel(new DraftAttachment(name, file.Size, file.Path, file.ContentType), RemoveAttachment));
            if (AttachmentSafety.IsDangerous(name)) risky.Add(name);
        }

        IsDirty = true;
        OnPropertyChanged(nameof(HasAttachments));
        if (skipped.Count > 0)
        {
            SetStatus($"Not attached (25 MB limit): {string.Join(", ", skipped)}", error: true);
        }
        else if (risky.Count > 0)
        {
            SetStatus($"Heads up: {string.Join(", ", risky)} can run code, so many mail servers (including Gmail and Outlook) refuse it. Consider sharing it through a link instead.", error: false);
        }
    }

    private void RemoveAttachment(ComposeAttachmentViewModel item)
    {
        Attachments.Remove(item);
        IsDirty = true;
        OnPropertyChanged(nameof(HasAttachments));
    }

    private bool CanSend() => !IsSending && !_sendUncertain;

    private bool CanSaveDraft() => !IsSending;

    // Set when the server connection dropped mid-send: the message may have been delivered, so sending it again from
    // this window is blocked (the user can still copy the text or save it as a draft from a new message).
    private bool _sendUncertain;

    /// <summary>
    /// The message has been handed over (queued in the outbox or accepted by the server): the crash-recovery copy must
    /// go now, not when the window closes, or a crash in between would offer to "recover" a message that was sent.
    /// </summary>
    private async Task ForgetRecoveryCopyAsync()
    {
        _autosaveStop.Cancel();
        try
        {
            await _recovery.DeleteAsync(SessionId);
        }
        catch (Exception)
        {
            // Deleted again when the window closes.
        }
    }

    /// <summary>
    /// Sends the message. With Undo Send on, it is held in the outbox for a few seconds first: the window closes and a
    /// notice offers Undo, which brings the message back here. Undo only works until the message is handed to the
    /// mail server; after that it can't be recalled.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var draft = BuildDraft(out var error);
        if (draft is null)
        {
            SetStatus(error!, error: true);
            return;
        }

        var delay = _settings.Current.UndoSendSeconds;
        if (delay > 0)
        {
            await QueueAsync(draft, _clock.Now.AddSeconds(delay), OutboxKind.UndoWindow);
            return;
        }

        await SendDirectAsync(draft);
    }

    /// <param name="choice">A QuickTime name or "Custom".</param>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task ScheduleSendAsync(string choice)
    {
        var draft = BuildDraft(out var error);
        if (draft is null)
        {
            SetStatus(error!, error: true);
            return;
        }

        if (PickScheduleTime is null) return;
        var when = await PickScheduleTime(choice);
        if (when is null) return;
        if (when <= _clock.Now)
        {
            SetStatus("Choose a time in the future.", error: true);
            return;
        }

        await QueueAsync(draft, when.Value, OutboxKind.Scheduled);
    }

    private async Task QueueAsync(DraftMessage draft, DateTimeOffset sendAt, OutboxKind kind)
    {
        IsSending = true;
        try
        {
            var item = await _outbox.QueueAsync(draft with { BodyIncludesSignature = true }, sendAt, kind, _mail.IsDemo);
            await ForgetRecoveryCopyAsync();
            IsDirty = false;
            CloseRequested?.Invoke(this, EventArgs.Empty);

            if (kind == OutboxKind.UndoWindow)
            {
                _toasts.ShowWithAction("Sending…", ToastKind.Info, "Undo", () => UndoAsync(item.Id), sendAt - _clock.Now);
            }
            else
            {
                _toasts.Show(
                    $"Scheduled for {QuickTimes.Describe(sendAt, _clock.Now, TimeZoneInfo.Local)}. SidePhase Mail needs to be open then to send it.",
                    ToastKind.Success);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't queue the message: " + ex.Message, error: true);
        }
        finally
        {
            IsSending = false;
        }
    }

    private async Task UndoAsync(Guid outboxId)
    {
        var draft = await _outbox.CancelAsync(outboxId);
        if (draft is null)
        {
            _toasts.Show("Too late to undo — the message has already gone to the mail server.", ToastKind.Warning);
            return;
        }

        _windows.OpenCompose(draft);
    }

    private async Task SendDirectAsync(DraftMessage draft)
    {
        IsSending = true;
        SendProgress = 0;
        SetStatus("Sending…", error: false);
        try
        {
            var progress = new Progress<double>(p => SendProgress = p * 100);
            var result = await _mail.SendAsync(draft, progress);
            if (!result.Succeeded)
            {
                if (result.Outcome == SendOutcome.Uncertain)
                {
                    _sendUncertain = true;
                    await ForgetRecoveryCopyAsync();
                    SendCommand.NotifyCanExecuteChanged();
                }

                SetStatus(result.Message, error: true);
                return;
            }

            await ForgetRecoveryCopyAsync();
            IsDirty = false;
            if (!_mail.IsDemo) _ = _contacts.RecordSentAsync(draft);
            _toasts.Show(result.Message, ToastKind.Success);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't send: " + ex.Message, error: true);
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveDraft))]
    private async Task SaveDraftAsync()
    {
        var draft = BuildDraft(out _, requireRecipients: false);
        if (draft is null)
        {
            SetStatus("Choose an account to save this draft.", error: true);
            return;
        }

        _draftId = await _mail.SaveDraftAsync(draft);
        IsDirty = false;
        SetStatus($"Draft saved at {DateTime.Now.ToString("t", CultureInfo.CurrentCulture)}", error: false);
    }

    [RelayCommand]
    private void Discard()
    {
        IsDirty = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Validates the form and returns a draft, or null with an error message.</summary>
    public DraftMessage? BuildDraft(out string? error, bool requireRecipients = true)
    {
        error = null;
        var account = SelectedAccount;
        if (account is null)
        {
            error = "Choose the account to send from.";
            return null;
        }

        var to = RecipientParser.Parse(To);
        var cc = RecipientParser.Parse(Cc);
        var bcc = RecipientParser.Parse(Bcc);
        var invalid = to.Invalid.Concat(cc.Invalid).Concat(bcc.Invalid).ToList();
        if (invalid.Count > 0)
        {
            error = $"Check these addresses: {string.Join(", ", invalid)}";
            return null;
        }

        if (requireRecipients && to.Addresses.Count + cc.Addresses.Count + bcc.Addresses.Count == 0)
        {
            error = "Add at least one recipient.";
            return null;
        }

        var body = BodyProvider?.Invoke() ?? new ComposeBody(string.Empty, null);
        return new DraftMessage
        {
            DraftId = _draftId,
            AccountId = account.Id,
            Mode = _initial.Mode,
            SourceMessageId = _initial.SourceMessageId,
            To = to.Addresses,
            Cc = cc.Addresses,
            Bcc = bcc.Addresses,
            Subject = Subject.Trim(),
            IsPlainText = IsPlainText,
            TextBody = body.Text,
            HtmlBody = IsPlainText ? null : body.Html,
            QuotedHtml = _initial.QuotedHtml,
            QuotedText = _initial.QuotedText,
            Attachments = Attachments.Select(a => a.Attachment).ToList(),
        };
    }

    public void ReportStatus(string text, bool error) => SetStatus(text, error);

    private void SetStatus(string text, bool error)
    {
        StatusText = text;
        StatusIsError = error;
    }
}
