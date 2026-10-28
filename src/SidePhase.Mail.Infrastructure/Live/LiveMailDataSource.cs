using System.Collections.Concurrent;
using MailKit;
using MailFolder = SidePhase.Mail.Core.Models.MailFolder;
using MessageSummary = SidePhase.Mail.Core.Models.MessageSummary;
using MimeKit;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Scheduling;
using SidePhase.Mail.Core.Services;
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

    private readonly PendingSentCopies? _pendingSentCopies;

    public LiveMailDataSource(MailCache cache, MailConnector connector, CredentialProvider credentials, IClock clock, PendingSentCopies? pendingSentCopies = null)
    {
        _pendingSentCopies = pendingSentCopies;
        _cache = cache;
        _connector = connector;
        _credentials = credentials;
        _clock = clock;
        _imap = new ImapSessionPool(connector, credentials);
        _synchronizer = new ImapSynchronizer(cache);
    }

    public bool IsDemo => false;

    /// <summary>How many of the newest messages per folder the first sync downloads (older ones via server search).</summary>
    public int InitialMessageLimit
    {
        get => _synchronizer.InitialMessageLimit;
        set => _synchronizer.InitialMessageLimit = value;
    }

    public event EventHandler<MailDataChangedEventArgs>? DataChanged;

    public event EventHandler<MailErrorEventArgs>? ErrorOccurred;

    private readonly ConcurrentDictionary<Guid, AccountStatus> _status = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastAutoSync = new();

    /// <summary>Raised (on a background thread) when an account's connection status changes.</summary>
    public event EventHandler<Guid>? AccountStatusChanged;

    /// <summary>The result of the account's most recent sync on this run of the app.</summary>
    public AccountStatus GetAccountStatus(Guid accountId) => _status.GetValueOrDefault(accountId, AccountStatus.Unknown);

    private void SetStatus(Guid accountId, Func<AccountStatus, AccountStatus> change)
    {
        _status.AddOrUpdate(accountId, _ => change(AccountStatus.Unknown), (_, old) => change(old));
        AccountStatusChanged?.Invoke(this, accountId);
    }

    /// <summary>
    /// Raised after a sync finds messages that newly arrived in an Inbox (read or unread), for notifications and
    /// automatic Junk rules. Never raised for the first download of a folder.
    /// </summary>
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
            var mime = await _imap.RunAsync(account, c => ImapOperations.FetchMessageAsync(c, reference.FolderPath, reference.Uid, reference.UidValidity, cancellationToken), cancellationToken).ConfigureAwait(false);
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
            var mime = await _imap.RunAsync(account, c => ImapOperations.FetchMessageAsync(c, reference.FolderPath, reference.Uid, reference.UidValidity, cancellationToken), cancellationToken).ConfigureAwait(false);
            return MessageBodyExtractor.GetAttachmentContent(mime, attachmentIndex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(MailErrorTranslator.Describe(ex, account.Settings.Imap.Host).Message, ex);
        }
    }

    public async Task SaveAttachmentAsync(Guid messageId, int attachmentIndex, Stream destination, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var (reference, account) = await ResolveAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (reference is null || account is null) throw new InvalidOperationException("This message is no longer available.");

        // The raw message goes to a private temporary file that is deleted as soon as it's closed, then the
        // attachment is decoded from disk. Neither the message nor the attachment is held in memory.
        var temp = Path.Combine(Path.GetTempPath(), "sidephase-" + Guid.NewGuid().ToString("N") + ".eml");
        try
        {
            await using var raw = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            var download = progress is null ? null : new Progress<double>(p => progress.Report(p * 0.9));
            await _imap.RunAsync(account, async c =>
            {
                raw.SetLength(0); // a retry after a dropped connection starts again
                await ImapOperations.DownloadMessageAsync(c, reference.FolderPath, reference.Uid, reference.UidValidity, raw, download, cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);

            raw.Position = 0;
            await Task.Run(() =>
            {
                var parser = new MimeKit.MimeParser(raw, persistent: true);
                using var message = parser.ParseMessage(cancellationToken);
                MessageBodyExtractor.WriteAttachmentTo(message, attachmentIndex, destination, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
            progress?.Report(1.0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException(MailErrorTranslator.Describe(ex, account.Settings.Imap.Host).Message, ex);
        }
    }

    // ------------------------------------------------------------------ flags (optimistic, reverted on failure)

    // Cache work for many messages runs off the caller's thread (SQLite calls complete synchronously), so bulk
    // actions never freeze the window.
    public Task SetReadAsync(IReadOnlyCollection<Guid> messageIds, bool isRead, CancellationToken cancellationToken = default) =>
        Task.Run(() => ChangeFlagsAsync(messageIds, MessageFlags.Seen, isRead, (id, value) => _cache.SetFlagsAsync(id, value, null, CancellationToken.None), r => r.IsRead), cancellationToken);

    public Task SetStarredAsync(IReadOnlyCollection<Guid> messageIds, bool isStarred, CancellationToken cancellationToken = default) =>
        Task.Run(() => ChangeFlagsAsync(messageIds, MessageFlags.Flagged, isStarred, (id, value) => _cache.SetFlagsAsync(id, null, value, CancellationToken.None), r => r.IsStarred), cancellationToken);

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

                            await ImapOperations.SetFlagsAsync(client, reference.FolderPath, reference.Uid, reference.UidValidity, flag, value, _shutdown.Token).ConfigureAwait(false);
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

    // ------------------------------------------------------------------ snooze & follow-up (local to this PC)

    public Task SetSnoozeAsync(IReadOnlyCollection<Guid> messageIds, DateTimeOffset? until, CancellationToken cancellationToken = default) =>
        SetLocalMarkerAsync(messageIds, (r, ct) => _cache.SetSnoozeAsync(r.AccountId, r.IdentityKey, until, _clock.Now, ct), cancellationToken);

    public Task SetCategoryAsync(IReadOnlyCollection<Guid> messageIds, string category, bool add, CancellationToken cancellationToken = default) =>
        SetLocalMarkerAsync(messageIds, (r, ct) => _cache.SetCategoryAsync(r.AccountId, r.IdentityKey, category.Trim(), add, ct), cancellationToken);

    public Task SetFollowUpAsync(IReadOnlyCollection<Guid> messageIds, DateTimeOffset? due, CancellationToken cancellationToken = default) =>
        SetLocalMarkerAsync(messageIds, (r, ct) => _cache.SetFollowUpAsync(r.AccountId, r.IdentityKey, due, _clock.Now, ct), cancellationToken);

    public Task<IReadOnlyList<MessageSummary>> ReleaseExpiredSnoozesAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<MessageSummary>>(async () =>
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var released = await _cache.ReleaseExpiredSnoozesAsync(now, cancellationToken).ConfigureAwait(false);
            if (released.Count == 0) return [];
            var back = new List<MessageSummary>();
            foreach (var (accountId, identityKey) in released)
            {
                foreach (var id in await _cache.FindAllByIdentityAsync(accountId, identityKey, cancellationToken).ConfigureAwait(false))
                {
                    if (await _cache.GetSummaryAsync(id, cancellationToken).ConfigureAwait(false) is { } summary) back.Add(summary);
                }
            }

            Raise(MailDataChangeKind.FlagsChanged, back.Count > 0 ? back.Select(m => m.Id).ToList() : []);
            return back;
        }, cancellationToken);

    public Task<IReadOnlyList<MessageSummary>> TakeDueFollowUpsAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<MessageSummary>>(async () =>
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var ids = await _cache.TakeDueRemindersAsync(now, cancellationToken).ConfigureAwait(false);
            var result = new List<MessageSummary>();
            foreach (var id in ids)
            {
                if (await _cache.GetSummaryAsync(id, cancellationToken).ConfigureAwait(false) is { } s) result.Add(s);
            }

            return result;
        }, cancellationToken);

    private Task SetLocalMarkerAsync(IReadOnlyCollection<Guid> ids, Func<StoredMessageRef, CancellationToken, Task> apply, CancellationToken ct) =>
        Task.Run(async () =>
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            var changed = new List<Guid>();
            foreach (var id in ids)
            {
                var reference = await _cache.GetMessageRefAsync(id, ct).ConfigureAwait(false);
                if (reference is null) continue;
                await apply(reference, ct).ConfigureAwait(false);
                changed.Add(id);
            }

            Raise(MailDataChangeKind.FlagsChanged, changed);
        }, ct);

    // ------------------------------------------------------------------ moves & deletes (explicit user actions)

    public Task<IReadOnlyList<MoveRecord>> MoveToAsync(IReadOnlyCollection<Guid> messageIds, FolderKind target, CancellationToken cancellationToken = default) =>
        MoveManyAsync(messageIds, async (account, reference, ct) =>
        {
            if (reference.FolderKind == target) return null;
            var destination = await FindOrCreateFolderAsync(account, target, ct).ConfigureAwait(false);
            if (destination is null) Report(account, $"{account.Account.DisplayName} has no {target} folder.");
            return destination;
        }, null, cancellationToken);

    public Task<IReadOnlyList<MoveRecord>> MoveToFolderAsync(IReadOnlyCollection<Guid> messageIds, Guid folderId, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        MoveManyAsync(messageIds, async (account, reference, ct) =>
        {
            if (reference.FolderId == folderId) return null;
            var destination = (await _cache.GetFoldersAsync(account.Account.Id, ct).ConfigureAwait(false)).FirstOrDefault(f => f.Id == folderId);
            if (destination is null) Report(account, "Messages can only be moved to folders of the same account.");
            return destination;
        }, progress, cancellationToken);

    public async Task RestoreAsync(IReadOnlyCollection<MoveRecord> moves, CancellationToken cancellationToken = default)
    {
        var from = moves.ToDictionary(m => m.MessageId, m => m.FromFolderId);
        await MoveManyAsync(from.Keys, async (account, reference, ct) =>
            (await _cache.GetFoldersAsync(account.Account.Id, ct).ConfigureAwait(false)).FirstOrDefault(f => f.Id == from[reference.Id]),
            null, cancellationToken, undo: true).ConfigureAwait(false);
    }

    /// <summary>Most messages moved in one IMAP command.</summary>
    private const int MoveBatchSize = 200;

    /// <summary>
    /// Moves messages grouped by account, source and destination folder, a batch per IMAP command, keeping the cache
    /// in step: moved rows are re-keyed to their new UID so the list updates at once, and messages whose new location
    /// isn't cached are remembered so they can still be moved back.
    /// </summary>
    private Task<IReadOnlyList<MoveRecord>> MoveManyAsync(
        IReadOnlyCollection<Guid> messageIds,
        Func<StoredAccount, StoredMessageRef, CancellationToken, Task<StoredFolder?>> destinationFor,
        IProgress<double>? progress,
        CancellationToken ct,
        bool undo = false) =>
        Task.Run(() => MoveManyCoreAsync(messageIds.ToList(), destinationFor, progress, ct, undo), ct);

    private async Task<IReadOnlyList<MoveRecord>> MoveManyCoreAsync(
        IReadOnlyCollection<Guid> messageIds,
        Func<StoredAccount, StoredMessageRef, CancellationToken, Task<StoredFolder?>> destinationFor,
        IProgress<double>? progress,
        CancellationToken ct,
        bool undo)
    {
        var moves = new List<MoveRecord>();
        var touched = new List<Guid>(messageIds);
        var groups = new Dictionary<(Guid Account, Guid Source, Guid Destination), (StoredAccount Account, StoredFolder Destination, List<StoredMessageRef> Refs)>();
        foreach (var id in messageIds)
        {
            var (reference, account) = await ResolveAsync(id, ct).ConfigureAwait(false);
            if (reference is null || account is null) continue;
            StoredFolder? destination;
            try
            {
                destination = await destinationFor(account, reference, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Report(account, ex, "Couldn't find the folder to move to");
                continue;
            }

            if (destination is null || destination.Id == reference.FolderId) continue;
            var key = (account.Account.Id, reference.FolderId, destination.Id);
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = (account, destination, []);
            group.Refs.Add(reference);
        }

        var total = groups.Values.Sum(g => g.Refs.Count);
        var done = 0;
        foreach (var (account, destination, refs) in groups.Values)
        {
            foreach (var batch in refs.Chunk(MoveBatchSize))
            {
                try
                {
                    var (newUids, validity) = await _imap.RunAsync(account, async client =>
                    {
                        // Teach server-side spam filters: junk in, or rescued out of junk.
                        var junkChange = destination.Kind == FolderKind.Junk ? true : batch[0].FolderKind == FolderKind.Junk ? false : (bool?)null;
                        if (junkChange is { } isJunk)
                        {
                            try
                            {
                                await ImapOperations.SetJunkKeywordAsync(client, batch[0].FolderPath, batch.Select(r => r.Uid).ToList(), batch[0].UidValidity, isJunk, ct).ConfigureAwait(false);
                            }
                            catch (MailKit.Net.Imap.ImapCommandException)
                            {
                                // Keywords are a hint; the move itself is what matters.
                            }
                        }

                        var result = await ImapOperations.MoveManyAsync(client, batch[0].FolderPath, batch.Select(r => r.Uid).ToList(), batch[0].UidValidity, destination.Path, ct).ConfigureAwait(false);

                        // Servers without COPYUID: find the moved copies by Message-ID.
                        var map = new Dictionary<uint, uint>(result.NewUids);
                        var uidValidity = result.DestinationUidValidity;
                        foreach (var r in batch.Where(r => !map.ContainsKey(r.Uid) && r.MessageIdHeader is not null))
                        {
                            if (await ImapOperations.FindByMessageIdAsync(client, destination.Path, r.MessageIdHeader!, ct).ConfigureAwait(false) is { } found)
                            {
                                map[r.Uid] = found.Uid;
                                uidValidity = found.UidValidity;
                            }
                        }

                        return ((IReadOnlyDictionary<uint, uint>)map, uidValidity);
                    }, ct).ConfigureAwait(false);

                    foreach (var reference in batch)
                    {
                        var newId = await RecordMoveAsync(account, reference, destination, newUids.TryGetValue(reference.Uid, out var uid) ? uid : null, validity, ct).ConfigureAwait(false);
                        moves.Add(new MoveRecord(newId, reference.FolderId));
                        touched.Add(newId);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Report(account, ex, undo ? "Couldn't undo the move" : "Couldn't move the message" + (batch.Length > 1 ? "s" : string.Empty));
                }

                done += batch.Length;
                progress?.Report(total == 0 ? 1 : (double)done / total);
            }
        }

        if (moves.Count > 0 || undo) Raise(MailDataChangeKind.Moved, touched);
        return moves;
    }

    /// <summary>Updates the cache after a server-side move and returns the message's new id.</summary>
    private async Task<Guid> RecordMoveAsync(StoredAccount account, StoredMessageRef reference, StoredFolder destination, uint? newUid, uint uidValidity, CancellationToken ct)
    {
        if (newUid is { } uid && destination.Synchronize)
        {
            var newId = MessageIdentity.ForImapMessage(account.Account.Id, destination.Path, uidValidity, uid);
            await _cache.MoveCachedMessageAsync(reference.Id, newId, destination.Id, uidValidity, uid, ct).ConfigureAwait(false);
            _hidden.TryRemove(reference.Id, out _);
            return newId;
        }

        // Moved somewhere that isn't cached (e.g. Gmail's All Mail) or the new UID is unknown: drop the cached copy.
        // Undo can still find it by Message-ID through a temporary reference.
        await _cache.DeleteMessageAsync(reference.Id, ct).ConfigureAwait(false);
        return newUid is { } hiddenUid ? RememberHidden(account, reference, destination, uidValidity, hiddenUid) : reference.Id;
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

    public async Task<int> DeletePermanentlyAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken = default)
    {
        var removed = new List<Guid>();
        foreach (var id in messageIds)
        {
            var (reference, account) = await ResolveAsync(id, cancellationToken).ConfigureAwait(false);
            if (reference is null || account is null || reference.FolderKind is not (FolderKind.Trash or FolderKind.Junk or FolderKind.Drafts)) continue;
            try
            {
                await _imap.RunAsync(account, c => ImapOperations.DeleteAsync(c, reference.FolderPath, reference.Uid, reference.UidValidity, cancellationToken), cancellationToken).ConfigureAwait(false);
                await _cache.DeleteMessageAsync(id, cancellationToken).ConfigureAwait(false);
                removed.Add(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Report(account, ex, "Couldn't delete the message");
            }
        }

        Raise(MailDataChangeKind.Removed, removed);
        return removed.Count;
    }

    // ------------------------------------------------------------------ drafts

    public Task<Guid> SaveDraftAsync(DraftMessage draft, CancellationToken cancellationToken = default) =>
        SaveDraftCoreAsync(draft, reportFailure: true, cancellationToken);

    private async Task<Guid> SaveDraftCoreAsync(DraftMessage draft, bool reportFailure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var account = (await GetStoredAccountsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(a => a.Account.Id == draft.AccountId)
            ?? throw new InvalidOperationException("Choose an account for this draft.");

        // A draft first saved offline and uploaded since: keep updating the uploaded copy rather than adding another.
        if (draft.DraftId is { } earlier && _uploadedDrafts.TryGetValue(earlier, out var uploaded)) draft = draft with { DraftId = uploaded };

        // Always keep a local copy first, so nothing typed is lost if the server is unreachable.
        var localId = draft.DraftId ?? Guid.NewGuid();
        await _cache.SaveDraftAsync(localId, draft, cancellationToken).ConfigureAwait(false);

        try
        {
            var folder = await FindOrCreateFolderAsync(account, FolderKind.Drafts, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("This account has no Drafts folder.");
            var attachments = ReadableAttachmentsOnly(draft);
            var mime = MimeMessageBuilder.Build(draft, account.Account, null, null, attachments);
            var oldRef = draft.DraftId is { } oldId ? await _cache.GetMessageRefAsync(oldId, cancellationToken).ConfigureAwait(false) : null;

            // Never replace a server draft with a copy that has lost something: drafts written in another app are
            // edited without their attachments, and attachments whose files have moved can't be re-attached.
            var keepOld = false;
            if (oldRef is { FolderKind: FolderKind.Drafts }
                && await _cache.GetSummaryAsync(oldRef.Id, cancellationToken).ConfigureAwait(false) is { } oldSummary)
            {
                var oldAttachments = oldSummary.Attachments.Count > 0 ? oldSummary.Attachments.Count : oldSummary.HasAttachments ? 1 : 0;
                keepOld = oldAttachments > attachments.Count;
            }

            var appended = await _imap.RunAsync(account, async client =>
            {
                // If a dropped connection made this a retry, the first attempt may already have stored the draft.
                var result = mime.MessageId is { } messageId
                    && await ImapOperations.FindByMessageIdAsync(client, folder.Path, messageId, cancellationToken).ConfigureAwait(false) is { } existing
                        ? new AppendResult(existing.Uid, existing.UidValidity)
                        : await ImapOperations.AppendAsync(client, folder.Path, mime, MessageFlags.Draft | MessageFlags.Seen, cancellationToken).ConfigureAwait(false);

                // Replacing your own earlier draft copy is part of saving it.
                if (oldRef is { FolderKind: FolderKind.Drafts } && !keepOld)
                {
                    await ImapOperations.DeleteAsync(client, oldRef.FolderPath, oldRef.Uid, oldRef.UidValidity, cancellationToken).ConfigureAwait(false);
                }

                return result;
            }, cancellationToken).ConfigureAwait(false);

            if (keepOld)
            {
                ErrorOccurred?.Invoke(this, new MailErrorEventArgs(account.Account.Id,
                    "The draft was saved. Its earlier copy in Drafts was kept too, because it has attachments this copy doesn't."));
            }

            if (oldRef is not null && !keepOld) await _cache.DeleteMessageAsync(oldRef.Id, cancellationToken).ConfigureAwait(false);

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
            if (reportFailure) Report(account, ex, "The draft was saved on this computer but couldn't be saved to the server");
        }

        return localId;
    }

    // Local draft id → server draft id, for drafts saved offline and uploaded later in this session.
    private readonly ConcurrentDictionary<Guid, Guid> _uploadedDrafts = new();

    /// <summary>How long a local-only draft must be left alone before it's uploaded (so a window still editing it isn't raced).</summary>
    public static readonly TimeSpan DraftUploadQuietPeriod = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Uploads drafts that could only be saved on this PC (the server was unreachable) to their account's Drafts
    /// folder, so they appear in Drafts and in other mail apps. Each is uploaded once; a failure leaves it for next time.
    /// </summary>
    public async Task UploadLocalDraftsAsync(CancellationToken ct, TimeSpan? quietFor = null)
    {
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return;
        var drafts = await _cache.GetLocalOnlyDraftsAsync(quietFor ?? DraftUploadQuietPeriod, ct).ConfigureAwait(false);
        if (drafts.Count == 0) return;
        var accounts = (await GetStoredAccountsAsync(ct).ConfigureAwait(false)).Select(a => a.Account.Id).ToHashSet();
        foreach (var draft in drafts)
        {
            if (draft.DraftId is not { } localId || !accounts.Contains(draft.AccountId) || _uploadedDrafts.ContainsKey(localId)) continue;
            // Quietly: while still offline this simply tries again next time.
            var savedAs = await SaveDraftCoreAsync(draft, reportFailure: false, ct).ConfigureAwait(false);
            if (savedAs != localId)
            {
                _uploadedDrafts[localId] = savedAs;
                Trace($"a draft saved offline was uploaded to Drafts");
            }
        }
    }

    public async Task<DraftMessage?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        if (_uploadedDrafts.TryGetValue(draftId, out var uploaded)) draftId = uploaded;
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

            var tracker = new SmtpSendTracker();
            string serverReply;
            try
            {
                var credential = await _credentials.GetAsync(account.Account.Id, account.Settings, ct: cancellationToken).ConfigureAwait(false);
                MailKit.Net.Smtp.SmtpClient smtp;
                try
                {
                    smtp = await _connector.ConnectSmtpAsync(account.Settings.Smtp, credential, cancellationToken).ConfigureAwait(false);
                }
                catch (MailKit.Security.AuthenticationException) when (account.Settings.AuthMethod != AuthMethod.Password)
                {
                    // The access token was revoked or rotated before its expiry: refresh once and sign in again
                    // (nothing has been sent yet at this point).
                    credential = await _credentials.GetAsync(account.Account.Id, account.Settings, forceRefresh: true, cancellationToken).ConfigureAwait(false);
                    smtp = await _connector.ConnectSmtpAsync(account.Settings.Smtp, credential, cancellationToken).ConfigureAwait(false);
                }

                using var smtpLifetime = smtp;
                serverReply = await SmtpSender.SendAsync(smtp, mime, progress, cancellationToken, tracker).ConfigureAwait(false);
                try { await smtp.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { /* already sent */ }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !tracker.DataStarted)
            {
                return new SendResult(false, "Sending was cancelled. The message was not sent.") { Outcome = SendOutcome.NotConnected };
            }
            catch (Exception ex)
            {
                var outcome = SmtpSender.Classify(ex, tracker.DataStarted);
                var error = MailErrorTranslator.Describe(ex, account.Settings.Smtp.Host);
                var message = outcome == SendOutcome.Uncertain
                    ? "The connection was lost just after the message was sent, so it may or may not have been delivered. Check your Sent folder before sending it again. (" + error.Message + ")"
                    : "Not sent. " + error.Message;
                return new SendResult(false, message) { Outcome = outcome };
            }

            // From here on the server has accepted the message. Filing the Sent copy and tidying the draft happen in
            // the background; a problem there is reported separately and never turns the send into a failure. The
            // Sent copy is written to disk first, so it's filed later even if the app closes before that happens.
            string? pendingCopy = null;
            if (NeedsOwnSentCopy(account))
            {
                PrepareSentCopy(mime, draft);
                if (_pendingSentCopies is not null)
                {
                    try
                    {
                        pendingCopy = await _pendingSentCopies.SaveAsync(account.Account.Id, mime, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Still filed straight away below; only the crash safety net is missing.
                    }
                }
            }

            _ = Task.Run(() => AfterSendAsync(account, draft, mime, pendingCopy));
            Trace($"accepted by {account.Settings.Smtp.Host}:{account.Settings.Smtp.Port}, reply \"{OneLine(serverReply)}\"; " +
                  (NeedsOwnSentCopy(account) ? "filing a copy in Sent" : $"{account.Account.Provider} files the Sent copy itself"));
            return new SendResult(true, "Sent.");
        }
        finally
        {
            _sending.TryRemove(draft, out _);
        }
    }

    /// <summary>Gmail and Outlook.com/Microsoft 365 save sent mail themselves; other servers need a copy appended.</summary>
    private static bool NeedsOwnSentCopy(StoredAccount account) =>
        account.Account.Provider is not (MailProviderKind.Gmail or MailProviderKind.Microsoft);

    /// <summary>The Sent copy keeps the Bcc recipients (they aren't sent to anyone, but the sender should see them).</summary>
    private static void PrepareSentCopy(MimeMessage mime, DraftMessage draft)
    {
        mime.Bcc.Clear();
        foreach (var b in draft.Bcc) mime.Bcc.Add(new MailboxAddress(b.Name ?? string.Empty, b.Address));
    }

    private async Task FileSentCopyAsync(StoredAccount account, MimeMessage mime, CancellationToken ct)
    {
        var sent = await FindOrCreateFolderAsync(account, FolderKind.Sent, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This account has no Sent folder and one couldn't be created.");
        var outcome = await _imap.RunAsync(account, async c =>
        {
            // Some servers file a copy of everything sent through SMTP themselves. Only add ours if the
            // server hasn't, so the message never appears twice in Sent.
            if (await SentCopyExistsAsync(c, sent.Path, mime.MessageId).ConfigureAwait(false)) return "the server had already filed one";
            var result = await ImapOperations.AppendAsync(c, sent.Path, mime, MessageFlags.Seen, ct).ConfigureAwait(false);
            return result.Uid is { } uid ? $"appended as UID {uid}" : "appended";
        }, ct).ConfigureAwait(false);
        Trace($"Sent copy in \"{sent.Path}\": {outcome}");
    }

    /// <summary>Files Sent copies left over from a previous session (the app closed before they were filed).</summary>
    public async Task FilePendingSentCopiesAsync(CancellationToken ct)
    {
        if (_pendingSentCopies is null) return;
        var pending = _pendingSentCopies.List();
        if (pending.Count == 0) return;
        var accounts = await GetStoredAccountsAsync(ct).ConfigureAwait(false);
        foreach (var copy in pending)
        {
            var account = accounts.FirstOrDefault(a => a.Account.Id == copy.AccountId);
            if (account is null) continue; // account removed: given up on after PendingSentCopies.GiveUpAfter
            try
            {
                var mime = await PendingSentCopies.LoadAsync(copy, ct).ConfigureAwait(false);
                await FileSentCopyAsync(account, mime, ct).ConfigureAwait(false);
                PendingSentCopies.Remove(copy.File);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Report(account, ex, "A sent message's copy couldn't be saved to Sent yet (SidePhase Mail will try again)");
            }
        }
    }

    private async Task AfterSendAsync(StoredAccount account, DraftMessage draft, MimeMessage mime, string? pendingCopy)
    {
        try
        {
            if (NeedsOwnSentCopy(account))
            {
                await FileSentCopyAsync(account, mime, CancellationToken.None).ConfigureAwait(false);
                if (pendingCopy is not null) PendingSentCopies.Remove(pendingCopy);
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
                    await _imap.RunAsync(account, c => ImapOperations.DeleteAsync(c, reference.FolderPath, reference.Uid, reference.UidValidity, CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
                    await _cache.DeleteMessageAsync(draftId).ConfigureAwait(false);
                }

                await _cache.DeleteDraftAsync(draftId).ConfigureAwait(false);
            }

            if (draft.SourceMessageId is { } source && draft.Mode is ComposeMode.Reply or ComposeMode.ReplyAll)
            {
                var original = await _cache.GetMessageRefAsync(source).ConfigureAwait(false);
                if (original is not null)
                {
                    await _imap.RunAsync(account, c => ImapOperations.SetFlagsAsync(c, original.FolderPath, original.Uid, original.UidValidity, MessageFlags.Answered, true, CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            // The message was sent. A draft left behind in Drafts could be sent again by mistake, so say so.
            if (draft.DraftId is not null) Report(account, ex, "The message was sent, but its draft couldn't be removed from Drafts (delete it there so it isn't sent twice)");
        }

        await SyncAccountKindsAsync(account, [FolderKind.Sent, FolderKind.Drafts]).ConfigureAwait(false);
        SendCompleted?.Invoke(this, draft);
    }

    /// <summary>
    /// Step-by-step notes on sending (server replies, where Sent copies were filed), for the diagnostic log. Never
    /// includes addresses, subjects or message content.
    /// </summary>
    public event EventHandler<string>? SendTrace;

    private void Trace(string text) => SendTrace?.Invoke(this, text);

    private static string OneLine(string? text)
    {
        var line = (text ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
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
                    // First downloads and UIDVALIDITY resets aren't "new mail", just the cache filling up.
                    if (folder.Kind == FolderKind.Inbox && current.UidValidity != 0 && !result.WasReset) inboxNew.AddRange(result.NewMessageIds);
                    Raise(MailDataChangeKind.Added, result.NewMessageIds);
                }

                if (result.FlagChanges > 0 || result.Removed > 0) Raise(MailDataChangeKind.FlagsChanged, []);
            }

            if (inboxNew.Count > 0 && NewMailArrived is { } handler)
            {
                var summaries = new List<MessageSummary>();
                foreach (var id in inboxNew)
                {
                    if (await _cache.GetSummaryAsync(id, ct).ConfigureAwait(false) is { } s) summaries.Add(s);
                }

                if (summaries.Count > 0) handler(this, summaries);
            }

            SetStatus(account.Account.Id, s => s with { LastSyncedAt = DateTimeOffset.UtcNow, RequiresSignIn = false });
            return (true, total);
        }
        catch (OperationCanceledException)
        {
            return (false, 0);
        }
        catch (Exception ex)
        {
            var error = MailErrorTranslator.Describe(ex, account.Settings.Imap.Host);
            SetStatus(account.Account.Id, s => s with { LastError = error.Message, RequiresSignIn = error.RequiresSignIn, LastErrorAt = DateTimeOffset.UtcNow });
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
    public void StartBackgroundSync(Func<TimeSpan> interval, Func<bool> isActive) => StartBackgroundSync(_ => interval(), isActive);

    /// <summary>
    /// Checks each account on its own schedule (<paramref name="intervalFor"/>) while <paramref name="isActive"/> says
    /// real accounts are in use. Everything is checked once straight away.
    /// </summary>
    public void StartBackgroundSync(Func<Guid, TimeSpan> intervalFor, Func<bool> isActive)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var first = true;
                while (!_shutdown.IsCancellationRequested)
                {
                    var accounts = isActive() ? await GetStoredAccountsAsync(_shutdown.Token).ConfigureAwait(false) : [];
                    var now = DateTimeOffset.UtcNow;
                    var due = accounts.Where(a => first || !_lastAutoSync.TryGetValue(a.Account.Id, out var last) || now - last >= intervalFor(a.Account.Id)).ToList();
                    foreach (var account in due) _lastAutoSync[account.Account.Id] = now;

                    if (due.Count > 0 && due.Count == accounts.Count)
                    {
                        await SyncAsync().ConfigureAwait(false);
                    }
                    else if (due.Count > 0)
                    {
                        await Task.WhenAll(due.Select(a => Task.Run(() => SyncAccountAsync(a, null, _shutdown.Token)))).ConfigureAwait(false);
                    }

                    if (first) await FilePendingSentCopiesAsync(_shutdown.Token).ConfigureAwait(false);
                    if (accounts.Count > 0) await UploadLocalDraftsAsync(_shutdown.Token).ConfigureAwait(false);
                    first = false;
                    await Task.Delay(TimeSpan.FromSeconds(30), _shutdown.Token).ConfigureAwait(false);
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

    // ------------------------------------------------------------------ search

    public Task<IReadOnlyDictionary<string, IReadOnlySet<Guid>>> SearchDownloadedTextAsync(IReadOnlyCollection<string> terms, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyDictionary<string, IReadOnlySet<Guid>>>(async () =>
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var result = new Dictionary<string, IReadOnlySet<Guid>>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in terms.Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // The FTS5 index covers subjects, people, downloaded text and attachment names of cached mail.
                var fts = SearchIndexQuery.For(SearchIndexQuery.FromKey(key));
                result[key] = fts is null ? new HashSet<Guid>() : await _cache.SearchIndexAsync(fts, ct: cancellationToken).ConfigureAwait(false);
            }

            return result;
        }, cancellationToken);

    /// <summary>Most matches downloaded per folder by one server search (newest first).</summary>
    public const int ServerSearchLimitPerFolder = 200;

    public Task<ServerSearchResult> SearchServerAsync(string query, MailboxScope scope, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var folderFilter = ServerSearch.FolderFilter(scope);
            if (folderFilter is null) return new ServerSearchResult(false, new HashSet<Guid>(), "Pinned and Favourites only exist on this PC, so there's nothing to search on the server.");
            var imapQuery = ServerSearch.BuildQuery(Core.Services.MessageSearch.Parse(query), _clock.Now);
            if (imapQuery is null) return new ServerSearchResult(false, new HashSet<Guid>(), "Type some words, or use from:, to:, subject: or a date, to search the server.");

            var ids = new HashSet<Guid>();
            var added = new List<Guid>();
            var failed = 0;
            var accounts = (await GetStoredAccountsAsync(cancellationToken).ConfigureAwait(false))
                .Where(a => scope.AccountId is null || a.Account.Id == scope.AccountId).ToList();
            foreach (var account in accounts)
            {
                try
                {
                    var folders = (await _cache.GetFoldersAsync(account.Account.Id, cancellationToken).ConfigureAwait(false))
                        .Where(f => f.Synchronize && folderFilter(f)).ToList();
                    foreach (var folder in folders)
                    {
                        await _imap.RunAsync(account, async client =>
                        {
                            var remote = await client.GetFolderAsync(folder.Path, cancellationToken).ConfigureAwait(false);
                            await remote.OpenAsync(MailKit.FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
                            try
                            {
                                if (remote.UidValidity != folder.UidValidity) return; // not synced yet; the next sync handles it
                                var uids = (await remote.SearchAsync(imapQuery, cancellationToken).ConfigureAwait(false))
                                    .OrderByDescending(u => u.Id).Take(ServerSearchLimitPerFolder).ToList();
                                var known = (await _cache.GetUidsAsync(folder.Id, folder.UidValidity, cancellationToken).ConfigureAwait(false)).ToHashSet();
                                var missing = uids.Where(u => !known.Contains(u.Id)).ToList();
                                if (missing.Count > 0)
                                {
                                    var summaries = await remote.FetchAsync(missing, ImapSynchronizer.SummaryRequest(), cancellationToken).ConfigureAwait(false);
                                    var stored = summaries.Select(m => ImapSynchronizer.ToStored(m, folder, folder.UidValidity)).ToList();
                                    await _cache.UpsertMessagesAsync(stored, cancellationToken).ConfigureAwait(false);
                                    added.AddRange(stored.Select(m => m.Id));
                                }

                                foreach (var uid in uids) ids.Add(MessageIdentity.ForImapMessage(account.Account.Id, folder.Path, folder.UidValidity, uid.Id));
                            }
                            finally
                            {
                                if (remote.IsOpen) await remote.CloseAsync(false, cancellationToken).ConfigureAwait(false);
                            }
                        }, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    Report(account, ex, $"Couldn't search {account.Account.DisplayName} on the server");
                }
            }

            if (added.Count > 0) Raise(MailDataChangeKind.Added, added);
            var message = ids.Count switch
            {
                0 when failed > 0 => "The server search failed.",
                0 => "No matches on the server.",
                1 => "1 match on the server.",
                _ => $"{ids.Count} matches on the server.",
            };
            if (added.Count > 0) message += $" {added.Count} older message{(added.Count == 1 ? " was" : "s were")} downloaded to show here.";
            return new ServerSearchResult(true, ids, message);
        }, cancellationToken);

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
