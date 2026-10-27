using System.Collections.Concurrent;
using MailKit;
using MailFolder = SidePhase.Mail.Core.Models.MailFolder;
using MessageSummary = SidePhase.Mail.Core.Models.MessageSummary;
using MimeKit;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Infrastructure.Accounts;
using SidePhase.Mail.Infrastructure.Mail;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Infrastructure.Live;

/// <summary>
/// Real accounts: reads come from the SQLite cache, changes go to the IMAP server, mail is sent with SMTP.
/// Failures in one account are reported through <see cref="ErrorOccurred"/> and never stop the others.
/// </summary>
public sealed class LiveMailDataSource : IMailDataSource, IAsyncDisposable
{
    private readonly MailCache _cache;
    private readonly MailConnector _connector;
    private readonly CredentialProvider _credentials;
    private readonly ImapSessionPool _imap;
    private readonly ImapSynchronizer _synchronizer;
    private readonly IClock _clock;
    private readonly Lock _syncGate = new();
    private readonly ConcurrentDictionary<DraftMessage, byte> _sending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task<SyncResult>? _runningSync;
    private Task<SyncResult>? _queuedSync;
    private IReadOnlyList<StoredAccount>? _accounts;
    private bool _initialized;

    public LiveMailDataSource(MailCache cache, MailConnector connector, CredentialProvider credentials, IClock clock)
    {
        _cache = cache;
        _connector = connector;
        _credentials = credentials;
        _clock = clock;
        _imap = new ImapSessionPool(connector, credentials);
        _synchronizer = new ImapSynchronizer(cache);
    }

    public bool IsDemo => false;

    public event EventHandler<MailDataChangedEventArgs>? DataChanged;

    public event EventHandler<MailErrorEventArgs>? ErrorOccurred;

    /// <summary>Raised after a sync finds new messages in an Inbox (for notifications).</summary>
    public event EventHandler<IReadOnlyList<MessageSummary>>? NewMailArrived;

    // ------------------------------------------------------------------ reads (cache only)

    // Reads run on the thread pool: SQLite I/O is synchronous, and callers are usually on the UI thread.

    public Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<MailAccount>>(async () =>
            (await GetStoredAccountsAsync(cancellationToken).ConfigureAwait(false)).Select(a => a.Account).ToList(), cancellationToken);

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<MailFolder>>(async () =>
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return (await _cache.GetFoldersAsync(accountId, cancellationToken).ConfigureAwait(false))
                .Where(f => f.Synchronize)
                .Select(f => f.ToMailFolder())
                .ToList();
        }, cancellationToken);

    public Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return await _cache.GetSummariesAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<MessageSummary?> GetMessageAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return await _cache.GetSummaryAsync(messageId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<MessageBody?> GetMessageBodyAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadBodyAsync(messageId, cancellationToken), cancellationToken);

    private async Task<MessageBody?> LoadBodyAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (await _cache.GetBodyAsync(messageId, cancellationToken).ConfigureAwait(false) is { } cached) return cached;

        var (reference, account) = await ResolveAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (reference is null || account is null) return null;

        try
        {
            var mime = await _imap.RunAsync(account, c => ImapOperations.FetchMessageAsync(c, reference.FolderPath, reference.Uid, cancellationToken), cancellationToken).ConfigureAwait(false);
            var body = MessageBodyExtractor.Extract(messageId, mime);
            await _cache.SaveBodyAsync(body, cancellationToken).ConfigureAwait(false);

            // The full header set gives a more complete spam assessment (e.g. X-Spam-Report) than the sync fetch.
            await _cache.UpdateSpamAsync(messageId, MessageBodyExtractor.ReadSpamHeaders(mime.Headers), cancellationToken).ConfigureAwait(false);
            Raise(MailDataChangeKind.FlagsChanged, [messageId]);
            return body;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MessageNotFoundException)
        {
            Report(account, "This message is no longer on the server. It may have been moved or deleted elsewhere.");
            return null;
        }
        catch (Exception ex)
        {
            Report(account, ex, "Couldn't download this message");
            return null;
        }
    }

    public async Task<byte[]> GetAttachmentContentAsync(Guid messageId, int attachmentIndex, CancellationToken cancellationToken = default)
    {
        var (reference, account) = await ResolveAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (reference is null || account is null) throw new InvalidOperationException("This message is no longer available.");

        try
        {
            var mime = await _imap.RunAsync(account, c => ImapOperations.FetchMessageAsync(c, reference.FolderPath, reference.Uid, cancellationToken), cancellationToken).ConfigureAwait(false);
            return MessageBodyExtractor.GetAttachmentContent(mime, attachmentIndex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(MailErrorTranslator.Describe(ex, account.Settings.Imap.Host).Message, ex);
        }
    }

    // ------------------------------------------------------------------ flags (optimistic, reverted on failure)

    public Task SetReadAsync(IReadOnlyCollection<Guid> messageIds, bool isRead, CancellationToken cancellationToken = default) =>
        ChangeFlagsAsync(messageIds, MessageFlags.Seen, isRead, (id, value) => _cache.SetFlagsAsync(id, value, null, CancellationToken.None), r => r.IsRead);

    public Task SetStarredAsync(IReadOnlyCollection<Guid> messageIds, bool isStarred, CancellationToken cancellationToken = default) =>
        ChangeFlagsAsync(messageIds, MessageFlags.Flagged, isStarred, (id, value) => _cache.SetFlagsAsync(id, null, value, CancellationToken.None), r => r.IsStarred);

    private async Task ChangeFlagsAsync(
        IReadOnlyCollection<Guid> ids,
        MessageFlags flag,
        bool value,
        Func<Guid, bool, Task> setLocal,
        Func<StoredMessageRef, bool> current)
    {
        await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);
        var accounts = await GetStoredAccountsAsync(CancellationToken.None).ConfigureAwait(false);
        var changes = new List<(StoredMessageRef Ref, StoredAccount Account)>();
        foreach (var id in ids)
        {
            var reference = await _cache.GetMessageRefAsync(id).ConfigureAwait(false);
            var account = accounts.FirstOrDefault(a => a.Account.Id == reference?.AccountId);
            if (reference is null || account is null) continue;
            if (flag == MessageFlags.Flagged && !value) await _cache.SetLocalStarAsync(reference.AccountId, reference.IdentityKey, false).ConfigureAwait(false);
            if (current(reference) == value && flag != MessageFlags.Flagged) continue;
            await setLocal(id, value).ConfigureAwait(false);
            changes.Add((reference, account));
        }

        if (changes.Count == 0) return;
        Raise(MailDataChangeKind.FlagsChanged, changes.Select(c => c.Ref.Id).ToList());

        // Server update in the background so the UI stays instant.
        _ = Task.Run(async () =>
        {
            var reverted = new List<Guid>();
            foreach (var group in changes.GroupBy(c => c.Account.Account.Id))
            {
                var account = group.First().Account;
                try
                {
                    await _imap.RunAsync(account, async client =>
                    {
                        foreach (var (reference, _) in group)
                        {
                            if (flag == MessageFlags.Flagged && value && !await ImapOperations.SupportsFlaggedAsync(client, reference.FolderPath, _shutdown.Token).ConfigureAwait(false))
                            {
                                // The server can't store stars: keep a local favourite instead.
                                await _cache.SetLocalStarAsync(reference.AccountId, reference.IdentityKey, true).ConfigureAwait(false);
                                continue;
                            }

                            await ImapOperations.SetFlagsAsync(client, reference.FolderPath, reference.Uid, flag, value, _shutdown.Token).ConfigureAwait(false);
                        }
                    }, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    foreach (var (reference, _) in group)
                    {
                        await setLocal(reference.Id, !value).ConfigureAwait(false);
                        reverted.Add(reference.Id);
                    }

                    Report(account, ex, flag == MessageFlags.Seen ? "Couldn't update read status" : "Couldn't update the star");
                }
            }

            if (reverted.Count > 0) Raise(MailDataChangeKind.FlagsChanged, reverted);
        });
    }

    /// <summary>Pins are local to this computer and attach to the message's identity, so they survive moves.</summary>
    public async Task SetPinnedAsync(IReadOnlyCollection<Guid> messageIds, bool isPinned, CancellationToken cancellationToken = default)
    {
        var changed = new List<Guid>();
        foreach (var id in messageIds)
        {
            var reference = await _cache.GetMessageRefAsync(id, cancellationToken).ConfigureAwait(false);
            if (reference is null) continue;
            await _cache.SetPinnedAsync(reference.AccountId, reference.IdentityKey, isPinned ? _clock.Now : null, cancellationToken).ConfigureAwait(false);
            changed.Add(id);
        }

        Raise(MailDataChangeKind.FlagsChanged, changed);
    }

    // ------------------------------------------------------------------ moves & deletes (explicit user actions)

    public async Task<IReadOnlyList<MoveRecord>> MoveToAsync(IReadOnlyCollection<Guid> messageIds, FolderKind target, CancellationToken cancellationToken = default)
    {
        var moves = new List<MoveRecord>();
        foreach (var id in messageIds)
        {
            var (reference, account) = await ResolveAsync(id, cancellationToken).ConfigureAwait(false);
            if (reference is null || account is null || reference.FolderKind == target) continue;

            try
            {
                var destination = await FindOrCreateFolderAsync(account, target, cancellationToken).ConfigureAwait(false);
                if (destination is null)
                {
                    Report(account, $"{account.Account.DisplayName} has no {target} folder.");
                    continue;
                }

                var newId = await MoveOneAsync(account, reference, destination, cancellationToken).ConfigureAwait(false);
                moves.Add(new MoveRecord(newId, reference.FolderId));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Report(account, ex, "Couldn't move the message");
            }
        }

        if (moves.Count > 0) Raise(MailDataChangeKind.Moved, moves.Select(m => m.MessageId).Concat(messageIds).ToList());
        return moves;
    }

    public async Task RestoreAsync(IReadOnlyCollection<MoveRecord> moves, CancellationToken cancellationToken = default)
    {
        var restored = new List<Guid>();
        foreach (var move in moves)
        {
            var (reference, account) = await ResolveAsync(move.MessageId, cancellationToken).ConfigureAwait(false);
            if (reference is null || account is null) continue;
            var original = (await _cache.GetFoldersAsync(account.Account.Id, cancellationToken).ConfigureAwait(false)).FirstOrDefault(f => f.Id == move.FromFolderId);
            if (original is null) continue;
            try
            {
                restored.Add(await MoveOneAsync(account, reference, original, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Report(account, ex, "Couldn't undo the move");
            }
        }

        Raise(MailDataChangeKind.Moved, restored);
    }

    private async Task<Guid> MoveOneAsync(StoredAccount account, StoredMessageRef reference, StoredFolder destination, CancellationToken ct)
    {
        var result = await _imap.RunAsync(account, async client =>
        {
            // Teach server-side spam filters: junk in, or rescued out of junk.
            var junkChange = destination.Kind == FolderKind.Junk ? true : reference.FolderKind == FolderKind.Junk ? false : (bool?)null;
            if (junkChange is { } isJunk)
            {
                try
                {
                    await ImapOperations.SetJunkKeywordAsync(client, reference.FolderPath, reference.Uid, isJunk, ct).ConfigureAwait(false);
                }
                catch (MailKit.Net.Imap.ImapCommandException)
                {
                    // Keywords are a hint; the move itself is what matters.
                }
            }

            var moved = await ImapOperations.MoveAsync(client, reference.FolderPath, reference.Uid, destination.Path, ct).ConfigureAwait(false);
            if (moved.NewUid is null && reference.MessageIdHeader is { } mid)
            {
                var found = await ImapOperations.FindByMessageIdAsync(client, destination.Path, mid, ct).ConfigureAwait(false);
                if (found is { } f) moved = new MoveResult(f.Uid, f.UidValidity);
            }

            return moved;
        }, ct).ConfigureAwait(false);

        if (result.NewUid is { } uid && destination.Synchronize)
        {
            var newId = MessageIdentity.ForImapMessage(account.Account.Id, destination.Path, result.DestinationUidValidity, uid);
            await _cache.MoveCachedMessageAsync(reference.Id, newId, destination.Id, result.DestinationUidValidity, uid, ct).ConfigureAwait(false);
            return newId;
        }

        // Moved somewhere that isn't cached (e.g. Gmail's All Mail) or the new UID is unknown: drop the cached copy.
        // Undo can still find it by Message-ID through a temporary reference.
        await _cache.DeleteMessageAsync(reference.Id, ct).ConfigureAwait(false);
        if (result.NewUid is { } hiddenUid)
        {
            return RememberHidden(account, reference, destination, result.DestinationUidValidity, hiddenUid);
        }

        return reference.Id;
    }

    /// <summary>
    /// Remembers (in memory only) where a message went when its new folder isn't cached, e.g. Gmail's All Mail, so
    /// that undo can move it back.
    /// </summary>
    private Guid RememberHidden(StoredAccount account, StoredMessageRef reference, StoredFolder destination, uint uidValidity, uint uid)
    {
        var id = MessageIdentity.ForImapMessage(account.Account.Id, destination.Path, uidValidity, uid);
        _hidden[id] = reference with { Id = id, FolderId = destination.Id, FolderPath = destination.Path, FolderKind = destination.Kind, Uid = uid, UidValidity = uidValidity };
        return id;
    }

    private readonly ConcurrentDictionary<Guid, StoredMessageRef> _hidden = new();

    public async Task DeletePermanentlyAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken = default)
    {
        var removed = new List<Guid>();
        foreach (var id in messageIds)
        {
            var (reference, account) = await ResolveAsync(id, cancellationToken).ConfigureAwait(false);
            if (reference is null || account is null || reference.FolderKind is not (FolderKind.Trash or FolderKind.Junk or FolderKind.Drafts)) continue;
            try
            {
                await _imap.RunAsync(account, c => ImapOperations.DeleteAsync(c, reference.FolderPath, reference.Uid, cancellationToken), cancellationToken).ConfigureAwait(false);
                await _cache.DeleteMessageAsync(id, cancellationToken).ConfigureAwait(false);
                removed.Add(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Report(account, ex, "Couldn't delete the message");
            }
        }

        Raise(MailDataChangeKind.Removed, removed);
    }

    // ------------------------------------------------------------------ drafts

    public async Task<Guid> SaveDraftAsync(DraftMessage draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var account = (await GetStoredAccountsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(a => a.Account.Id == draft.AccountId)
            ?? throw new InvalidOperationException("Choose an account for this draft.");

        // Always keep a local copy first, so nothing typed is lost if the server is unreachable.
        var localId = draft.DraftId ?? Guid.NewGuid();
        await _cache.SaveDraftAsync(localId, draft, cancellationToken).ConfigureAwait(false);

        try
        {
            var folder = await FindOrCreateFolderAsync(account, FolderKind.Drafts, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("This account has no Drafts folder.");
            var mime = MimeMessageBuilder.Build(draft, account.Account, null, null, ReadableAttachmentsOnly(draft));
            var oldRef = draft.DraftId is { } oldId ? await _cache.GetMessageRefAsync(oldId, cancellationToken).ConfigureAwait(false) : null;

            var appended = await _imap.RunAsync(account, async client =>
            {
                var result = await ImapOperations.AppendAsync(client, folder.Path, mime, MessageFlags.Draft | MessageFlags.Seen, cancellationToken).ConfigureAwait(false);

                // Replacing your own earlier draft copy is part of saving it.
                if (oldRef is { FolderKind: FolderKind.Drafts })
                {
                    await ImapOperations.DeleteAsync(client, oldRef.FolderPath, oldRef.Uid, cancellationToken).ConfigureAwait(false);
                }

                return result;
            }, cancellationToken).ConfigureAwait(false);

            if (oldRef is not null) await _cache.DeleteMessageAsync(oldRef.Id, cancellationToken).ConfigureAwait(false);

            await SyncFolderQuietlyAsync(account, folder, cancellationToken).ConfigureAwait(false);
            if (appended.Uid is { } uid)
            {
                var newId = MessageIdentity.ForImapMessage(account.Account.Id, folder.Path, appended.UidValidity, uid);
                await _cache.SaveDraftAsync(newId, draft with { DraftId = newId }, cancellationToken).ConfigureAwait(false);
                if (newId != localId) await _cache.DeleteDraftAsync(localId, cancellationToken).ConfigureAwait(false);
                Raise(MailDataChangeKind.Added, [newId]);
                return newId;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(account, ex, "The draft was saved on this computer but couldn't be saved to the server");
        }

        return localId;
    }

    public async Task<DraftMessage?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        if (await _cache.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false) is { } local) return local;

        // A draft written in another app: rebuild an editable copy from the server message.
        var summary = await _cache.GetSummaryAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (summary is null || summary.FolderKind != FolderKind.Drafts) return null;
        var body = await GetMessageBodyAsync(draftId, cancellationToken).ConfigureAwait(false);
        return new DraftMessage
        {
            DraftId = draftId,
            AccountId = summary.AccountId,
            Mode = ComposeMode.EditDraft,
            To = summary.To,
            Cc = summary.Cc,
            Bcc = body?.Bcc ?? [],
            Subject = summary.Subject,
            IsPlainText = body?.HtmlBody is null,
            HtmlBody = body?.HtmlBody,
            TextBody = body?.TextBody ?? summary.Preview,
        };
    }

    // ------------------------------------------------------------------ sending

    public async Task<SendResult> SendAsync(DraftMessage draft, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!draft.HasAnyRecipient) return new SendResult(false, "Add at least one recipient before sending.");

        // The same message can't be sent twice at once (double-click, retry while the first attempt is running).
        if (!_sending.TryAdd(draft, 0)) return new SendResult(false, "This message is already being sent.");
        try
        {
            var account = (await GetStoredAccountsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(a => a.Account.Id == draft.AccountId);
            if (account is null) return new SendResult(false, "The account for this message no longer exists.");

            MimeMessage mime;
            try
            {
                var (inReplyTo, references) = draft.SourceMessageId is { } source && draft.Mode is ComposeMode.Reply or ComposeMode.ReplyAll
                    ? await _cache.GetThreadHeadersAsync(source, cancellationToken).ConfigureAwait(false)
                    : (null, []);
                mime = MimeMessageBuilder.Build(draft, account.Account, inReplyTo, references);
            }
            catch (IOException ex)
            {
                return new SendResult(false, ex.Message);
            }

            try
            {
                var credential = await _credentials.GetAsync(account.Account.Id, account.Settings, ct: cancellationToken).ConfigureAwait(false);
                using var smtp = await _connector.ConnectSmtpAsync(account.Settings.Smtp, credential, cancellationToken).ConfigureAwait(false);
                await SmtpSender.SendAsync(smtp, mime, progress, cancellationToken).ConfigureAwait(false);
                try { await smtp.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { /* already sent */ }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new SendResult(false, "Sending was cancelled. The message was not sent.");
            }
            catch (Exception ex)
            {
                var error = MailErrorTranslator.Describe(ex, account.Settings.Smtp.Host);
                return new SendResult(false, "Not sent. " + error.Message);
            }

            // From here on the server has accepted the message. Filing the Sent copy and tidying the draft happen in
            // the background; a problem there is reported separately and never turns the send into a failure.
            _ = Task.Run(() => AfterSendAsync(account, draft, mime));
            return new SendResult(true, "Sent.");
        }
        finally
        {
            _sending.TryRemove(draft, out _);
        }
    }

    private async Task AfterSendAsync(StoredAccount account, DraftMessage draft, MimeMessage mime)
    {
        try
        {
            // Gmail and Outlook.com/Microsoft 365 save sent mail themselves; other servers need a copy appended.
            if (account.Account.Provider is not (MailProviderKind.Gmail or MailProviderKind.Microsoft))
            {
                var sent = await FindOrCreateFolderAsync(account, FolderKind.Sent, CancellationToken.None).ConfigureAwait(false);
                if (sent is not null)
                {
                    mime.Bcc.Clear();
                    foreach (var b in draft.Bcc) mime.Bcc.Add(new MailboxAddress(b.Name ?? string.Empty, b.Address));
                    await _imap.RunAsync(account, async c =>
                    {
                        // Some servers file a copy of everything sent through SMTP themselves. Only add ours if the
                        // server hasn't, so the message never appears twice in Sent.
                        if (await SentCopyExistsAsync(c, sent.Path, mime.MessageId).ConfigureAwait(false)) return;
                        await ImapOperations.AppendAsync(c, sent.Path, mime, MessageFlags.Seen, CancellationToken.None).ConfigureAwait(false);
                    }, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            Report(account, ex, "The message was sent, but a copy couldn't be saved to Sent");
        }

        try
        {
            if (draft.DraftId is { } draftId)
            {
                var reference = await _cache.GetMessageRefAsync(draftId).ConfigureAwait(false);
                if (reference is { FolderKind: FolderKind.Drafts })
                {
                    await _imap.RunAsync(account, c => ImapOperations.DeleteAsync(c, reference.FolderPath, reference.Uid, CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
                    await _cache.DeleteMessageAsync(draftId).ConfigureAwait(false);
                }

                await _cache.DeleteDraftAsync(draftId).ConfigureAwait(false);
            }

            if (draft.SourceMessageId is { } source && draft.Mode is ComposeMode.Reply or ComposeMode.ReplyAll)
            {
                var original = await _cache.GetMessageRefAsync(source).ConfigureAwait(false);
                if (original is not null)
                {
                    await _imap.RunAsync(account, c => ImapOperations.SetFlagsAsync(c, original.FolderPath, original.Uid, MessageFlags.Answered, true, CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception)
        {
            // Tidying up drafts or marking the original as answered is cosmetic; the message was sent.
        }

        await SyncAccountKindsAsync(account, [FolderKind.Sent, FolderKind.Drafts]).ConfigureAwait(false);
        SendCompleted?.Invoke(this, draft);
    }

    /// <summary>Raised after a sent message's follow-up work (Sent copy, draft clean-up, Sent sync) has finished.</summary>
    public event EventHandler<DraftMessage>? SendCompleted;

    /// <summary>Checks for a server-filed Sent copy, giving the server a moment to file it first.</summary>
    internal static async Task<bool> SentCopyExistsAsync(MailKit.Net.Imap.ImapClient client, string sentPath, string? messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await Task.Delay(1500).ConfigureAwait(false);
            if (await ImapOperations.FindByMessageIdAsync(client, sentPath, messageId, CancellationToken.None).ConfigureAwait(false) is not null) return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ synchronisation

    /// <summary>
    /// Synchronises every account. A request made while a sync is running gets one more pass after it (shared by
    /// everyone who asks in the meantime), so changes made during the running pass are never missed.
    /// </summary>
    public Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncGate)
        {
            if (_runningSync is { IsCompleted: false } running)
            {
                return _queuedSync ??= running.ContinueWith(_ =>
                {
                    lock (_syncGate)
                    {
                        _queuedSync = null;
                        _runningSync = Task.Run(() => SyncAllAsync(_shutdown.Token), CancellationToken.None);
                        return _runningSync;
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }

            _runningSync = Task.Run(() => SyncAllAsync(_shutdown.Token), CancellationToken.None);
            return _runningSync;
        }
    }

    private async Task<SyncResult> SyncAllAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var accounts = await GetStoredAccountsAsync(ct).ConfigureAwait(false);
        if (accounts.Count == 0) return new SyncResult(true, 0, "No accounts to synchronise.");

        var results = await Task.WhenAll(accounts.Select(a => SyncAccountAsync(a, null, ct))).ConfigureAwait(false);
        var failed = results.Count(r => !r.Ok);
        var newCount = results.Sum(r => r.New);
        return failed == 0
            ? new SyncResult(true, newCount, newCount == 0 ? "Up to date." : $"{newCount} new message{(newCount == 1 ? "" : "s")}.")
            : new SyncResult(false, newCount, failed == accounts.Count ? "Couldn't reach your mail servers." : $"{failed} of {accounts.Count} accounts couldn't be synchronised.");
    }

    private Task SyncAccountKindsAsync(StoredAccount account, IReadOnlyCollection<FolderKind> kinds) =>
        Task.Run(() => SyncAccountAsync(account, kinds, _shutdown.Token));

    private async Task<(bool Ok, int New)> SyncAccountAsync(StoredAccount account, IReadOnlyCollection<FolderKind>? onlyKinds, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<StoredFolder> folders;
            var before = (await _cache.GetFoldersAsync(account.Account.Id, ct).ConfigureAwait(false)).Select(f => f.Path).ToHashSet();
            if (onlyKinds is null || before.Count == 0)
            {
                folders = await _imap.RunAsync(account, c => _synchronizer.SyncFolderListAsync(c, account.Account.Id, ct), ct).ConfigureAwait(false);
                if (!before.SetEquals(folders.Select(f => f.Path))) Raise(MailDataChangeKind.Reset, []);
            }
            else
            {
                folders = await _cache.GetFoldersAsync(account.Account.Id, ct).ConfigureAwait(false);
            }

            var total = 0;
            var inboxNew = new List<Guid>();
            foreach (var folder in folders.Where(f => f.Synchronize && (onlyKinds is null || onlyKinds.Contains(f.Kind))).OrderBy(f => f.SortOrder))
            {
                // Each folder is its own short operation so user actions can run in between.
                var current = (await _cache.GetFoldersAsync(account.Account.Id, ct).ConfigureAwait(false)).FirstOrDefault(f => f.Id == folder.Id) ?? folder;
                var result = await _imap.RunAsync(account, c => _synchronizer.SyncFolderAsync(c, current, ct), ct).ConfigureAwait(false);
                if (result.WasReset) Raise(MailDataChangeKind.Reset, []);
                if (result.NewMessageIds.Count > 0)
                {
                    total += result.NewMessageIds.Count;
                    if (folder.Kind == FolderKind.Inbox && current.UidValidity != 0) inboxNew.AddRange(result.NewMessageIds);
                    Raise(MailDataChangeKind.Added, result.NewMessageIds);
                }

                if (result.FlagChanges > 0 || result.Removed > 0) Raise(MailDataChangeKind.FlagsChanged, []);
            }

            if (inboxNew.Count > 0 && NewMailArrived is { } handler)
            {
                var summaries = new List<MessageSummary>();
                foreach (var id in inboxNew)
                {
                    if (await _cache.GetSummaryAsync(id, ct).ConfigureAwait(false) is { IsRead: false } s) summaries.Add(s);
                }

                if (summaries.Count > 0) handler(this, summaries);
            }

            return (true, total);
        }
        catch (OperationCanceledException)
        {
            return (false, 0);
        }
        catch (Exception ex)
        {
            Report(account, ex, $"Couldn't synchronise {account.Account.DisplayName}");
            return (false, 0);
        }
    }

    private async Task SyncFolderQuietlyAsync(StoredAccount account, StoredFolder folder, CancellationToken ct)
    {
        try
        {
            var current = (await _cache.GetFoldersAsync(account.Account.Id, ct).ConfigureAwait(false)).First(f => f.Id == folder.Id);
            if (current.Synchronize) await _imap.RunAsync(account, c => _synchronizer.SyncFolderAsync(c, current, ct), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The next full sync picks it up.
        }
    }

    /// <summary>
    /// Runs <see cref="SyncAsync"/> now and then every <paramref name="interval"/> until the app closes, whenever
    /// <paramref name="isActive"/> says real accounts are in use.
    /// </summary>
    public void StartBackgroundSync(Func<TimeSpan> interval, Func<bool> isActive)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    if (isActive() && (await GetStoredAccountsAsync(_shutdown.Token).ConfigureAwait(false)).Count > 0)
                    {
                        await SyncAsync().ConfigureAwait(false);
                    }

                    await Task.Delay(interval(), _shutdown.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // The cache itself failed (e.g. disk full). Report it once rather than crashing the app.
                ErrorOccurred?.Invoke(this, new MailErrorEventArgs(null, "Background sync stopped: " + MailErrorTranslator.Describe(ex).Message));
            }
        });
    }

    public async Task ClearLocalDataAsync(CancellationToken cancellationToken = default)
    {
        await _cache.ClearMessagesAsync(cancellationToken).ConfigureAwait(false);
        Raise(MailDataChangeKind.Reset, []);
        _ = SyncAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------ accounts

    /// <summary>Call after an account is added, re-authenticated or removed.</summary>
    public async Task OnAccountsChangedAsync(Guid? removedAccountId = null)
    {
        _accounts = null;
        if (removedAccountId is { } id) await _imap.CloseAsync(id).ConfigureAwait(false);
        Raise(MailDataChangeKind.Reset, []);
    }

    /// <summary>Drops the cached connection for an account so the next operation signs in with new credentials.</summary>
    public Task ResetConnectionAsync(Guid accountId) => _imap.CloseAsync(accountId);

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _imap.DisposeAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ helpers

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _cache.InitializeAsync(ct).ConfigureAwait(false);
        _initialized = true;
    }

    private async Task<IReadOnlyList<StoredAccount>> GetStoredAccountsAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return _accounts ??= await _cache.GetAccountsAsync(ct).ConfigureAwait(false);
    }

    private async Task<(StoredMessageRef? Ref, StoredAccount? Account)> ResolveAsync(Guid messageId, CancellationToken ct)
    {
        var reference = await _cache.GetMessageRefAsync(messageId, ct).ConfigureAwait(false);
        if (reference is null) _hidden.TryGetValue(messageId, out reference);
        if (reference is null) return (null, null);
        var account = (await GetStoredAccountsAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.Account.Id == reference.AccountId);
        return (reference, account);
    }

    private async Task<StoredFolder?> FindOrCreateFolderAsync(StoredAccount account, FolderKind kind, CancellationToken ct)
    {
        var folders = await _cache.GetFoldersAsync(account.Account.Id, ct).ConfigureAwait(false);
        if (folders.Count == 0)
        {
            folders = await _imap.RunAsync(account, c => _synchronizer.SyncFolderListAsync(c, account.Account.Id, ct), ct).ConfigureAwait(false);
        }

        var match = folders.Where(f => f.Kind == kind).OrderByDescending(f => f.Synchronize).FirstOrDefault();
        if (match is not null || kind is FolderKind.Inbox or FolderKind.Custom) return match;

        // Creating a standard folder the server lacks is part of the action the user asked for (e.g. Archive).
        var name = kind.ToString();
        await _imap.RunAsync(account, c => ImapOperations.CreateFolderAsync(c, name, ct), ct).ConfigureAwait(false);
        folders = await _imap.RunAsync(account, c => _synchronizer.SyncFolderListAsync(c, account.Account.Id, ct), ct).ConfigureAwait(false);
        Raise(MailDataChangeKind.Reset, []);
        return folders.FirstOrDefault(f => f.Kind == kind);
    }

    private static Dictionary<DraftAttachment, byte[]> ReadableAttachmentsOnly(DraftMessage draft)
    {
        // A draft can be saved even if an attachment file has since been moved; it's simply left out of the server copy.
        var result = new Dictionary<DraftAttachment, byte[]>();
        foreach (var a in draft.Attachments)
        {
            try
            {
                if (a.SourcePath is not null && File.Exists(a.SourcePath)) result[a] = File.ReadAllBytes(a.SourcePath);
            }
            catch (IOException)
            {
            }
        }

        return result;
    }

    private void Report(StoredAccount account, Exception ex, string context)
    {
        var error = MailErrorTranslator.Describe(ex, account.Settings.Imap.Host);
        ErrorOccurred?.Invoke(this, new MailErrorEventArgs(account.Account.Id, $"{context}: {error.Message}", error.RequiresSignIn));
    }

    private void Report(StoredAccount account, string message) =>
        ErrorOccurred?.Invoke(this, new MailErrorEventArgs(account.Account.Id, message));

    private void Raise(MailDataChangeKind kind, IReadOnlyCollection<Guid> ids)
    {
        if (kind != MailDataChangeKind.Reset && kind != MailDataChangeKind.FlagsChanged && ids.Count == 0) return;
        if (kind == MailDataChangeKind.FlagsChanged && ids.Count == 0)
        {
            // Sync-detected changes: we don't track exactly which rows changed, so ask views to reload their lists.
            DataChanged?.Invoke(this, new MailDataChangedEventArgs(MailDataChangeKind.Added, []));
            return;
        }

        DataChanged?.Invoke(this, new MailDataChangedEventArgs(kind, ids));
    }
}
