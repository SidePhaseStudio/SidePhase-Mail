using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Rendering;
using SidePhase.Mail.Core.Services;

namespace SidePhase.Mail.Infrastructure.Demo;

/// <summary>
/// In-memory mail store filled with clearly labelled demonstration content. It never opens a network connection,
/// never sends mail and is reset by "Clear local data".
/// </summary>
public sealed class DemoMailDataSource : IMailDataSource
{
    private readonly IClock _clock;
    private readonly Lock _gate = new();
    private readonly TimeSpan _latency;

    private List<MailAccount> _accounts = [];
    private List<MailFolder> _folders = [];
    private Dictionary<Guid, MessageSummary> _messages = [];
    private Dictionary<Guid, MessageBody> _bodies = [];
    private Dictionary<Guid, DraftMessage> _drafts = [];

    public DemoMailDataSource(IClock clock)
        : this(clock, TimeSpan.FromMilliseconds(450))
    {
    }

    internal DemoMailDataSource(IClock clock, TimeSpan simulatedLatency)
    {
        _clock = clock;
        _latency = simulatedLatency;
        Seed();
    }

    public bool IsDemo => true;

    public event EventHandler<MailDataChangedEventArgs>? DataChanged;

    // The demo store has no background work that can fail.
    public event EventHandler<MailErrorEventArgs>? ErrorOccurred
    {
        add { }
        remove { }
    }

    public Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<MailAccount>>(_accounts.ToList());
    }

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<MailFolder>>(
                _folders.Where(f => f.AccountId == accountId).OrderBy(f => f.SortOrder).ToList());
        }
    }

    public Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<MessageSummary>>(_messages.Values.ToList());
    }

    public Task<MessageSummary?> GetMessageAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        lock (_gate) return Task.FromResult(_messages.GetValueOrDefault(messageId));
    }

    public async Task<MessageBody?> GetMessageBodyAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        // A short, cancellable delay keeps the UI honest about bodies being loaded on demand.
        await Task.Delay(_latency / 6, cancellationToken).ConfigureAwait(false);
        lock (_gate) return _bodies.GetValueOrDefault(messageId);
    }

    public Task<byte[]> GetAttachmentContentAsync(Guid messageId, int attachmentIndex, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Demonstration attachments have no file content.");

    public Task SaveAttachmentAsync(Guid messageId, int attachmentIndex, Stream destination, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Demonstration attachments have no file content.");

    public Task SetReadAsync(IReadOnlyCollection<Guid> messageIds, bool isRead, CancellationToken cancellationToken = default) =>
        UpdateFlags(messageIds, m => m with { IsRead = isRead });

    public Task SetStarredAsync(IReadOnlyCollection<Guid> messageIds, bool isStarred, CancellationToken cancellationToken = default) =>
        UpdateFlags(messageIds, m => m with { IsStarred = isStarred });

    public Task SetPinnedAsync(IReadOnlyCollection<Guid> messageIds, bool isPinned, CancellationToken cancellationToken = default)
    {
        var now = _clock.Now;
        return UpdateFlags(messageIds, m => m with { IsPinned = isPinned, PinnedAt = isPinned ? now : null });
    }

    public Task SetSnoozeAsync(IReadOnlyCollection<Guid> messageIds, DateTimeOffset? until, CancellationToken cancellationToken = default) =>
        UpdateFlags(messageIds, m => m with { SnoozedUntil = until });

    public Task<IReadOnlyList<MessageSummary>> ReleaseExpiredSnoozesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        List<MessageSummary> released;
        lock (_gate)
        {
            released = _messages.Values.Where(m => m.SnoozedUntil is { } until && until <= now).Select(m => m with { SnoozedUntil = null }).ToList();
            foreach (var m in released) _messages[m.Id] = m;
        }

        Raise(MailDataChangeKind.FlagsChanged, released.Select(m => m.Id).ToList());
        return Task.FromResult<IReadOnlyList<MessageSummary>>(released);
    }

    public Task SetFollowUpAsync(IReadOnlyCollection<Guid> messageIds, DateTimeOffset? due, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var id in messageIds) _announcedFollowUps.Remove(id);
        }

        return UpdateFlags(messageIds, m => m with { FollowUpAt = due });
    }

    public Task<IReadOnlyList<MessageSummary>> TakeDueFollowUpsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var due = _messages.Values.Where(m => m.FollowUpAt is { } at && at <= now && _announcedFollowUps.Add(m.Id)).ToList();
            return Task.FromResult<IReadOnlyList<MessageSummary>>(due);
        }
    }

    private readonly HashSet<Guid> _announcedFollowUps = [];

    public Task<IReadOnlyList<MoveRecord>> MoveToAsync(IReadOnlyCollection<Guid> messageIds, FolderKind target, CancellationToken cancellationToken = default)
    {
        var moves = new List<MoveRecord>();
        lock (_gate)
        {
            foreach (var id in messageIds)
            {
                if (!_messages.TryGetValue(id, out var m)) continue;
                var folder = _folders.FirstOrDefault(f => f.AccountId == m.AccountId && f.Kind == target);
                if (folder is null || folder.Id == m.FolderId) continue;
                moves.Add(new MoveRecord(id, m.FolderId));
                _messages[id] = m with { FolderId = folder.Id, FolderKind = folder.Kind };
            }
        }

        Raise(MailDataChangeKind.Moved, moves.Select(m => m.MessageId).ToArray());
        return Task.FromResult<IReadOnlyList<MoveRecord>>(moves);
    }

    public Task<IReadOnlyList<MoveRecord>> MoveToFolderAsync(IReadOnlyCollection<Guid> messageIds, Guid folderId, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var moves = new List<MoveRecord>();
        lock (_gate)
        {
            var folder = _folders.FirstOrDefault(f => f.Id == folderId);
            foreach (var id in messageIds)
            {
                if (folder is null || !_messages.TryGetValue(id, out var m) || m.AccountId != folder.AccountId || m.FolderId == folder.Id) continue;
                moves.Add(new MoveRecord(id, m.FolderId));
                _messages[id] = m with { FolderId = folder.Id, FolderKind = folder.Kind };
            }
        }

        progress?.Report(1);
        Raise(MailDataChangeKind.Moved, moves.Select(m => m.MessageId).ToArray());
        return Task.FromResult<IReadOnlyList<MoveRecord>>(moves);
    }

    public Task SetCategoryAsync(IReadOnlyCollection<Guid> messageIds, string category, bool add, CancellationToken cancellationToken = default) =>
        UpdateFlags(messageIds, m => m with
        {
            Categories = add
                ? m.Categories.Contains(category, StringComparer.CurrentCultureIgnoreCase) ? m.Categories : [.. m.Categories, category]
                : m.Categories.Where(c => !c.Equals(category, StringComparison.CurrentCultureIgnoreCase)).ToList(),
        });

    public Task RestoreAsync(IReadOnlyCollection<MoveRecord> moves, CancellationToken cancellationToken = default)
    {
        var restored = new List<Guid>();
        lock (_gate)
        {
            foreach (var move in moves)
            {
                if (!_messages.TryGetValue(move.MessageId, out var m)) continue;
                var folder = _folders.FirstOrDefault(f => f.Id == move.FromFolderId);
                if (folder is null) continue;
                _messages[move.MessageId] = m with { FolderId = folder.Id, FolderKind = folder.Kind };
                restored.Add(move.MessageId);
            }
        }

        Raise(MailDataChangeKind.Moved, restored);
        return Task.CompletedTask;
    }

    public Task DeletePermanentlyAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken = default)
    {
        var removed = new List<Guid>();
        lock (_gate)
        {
            foreach (var id in messageIds)
            {
                if (_messages.TryGetValue(id, out var m) && m.FolderKind == FolderKind.Trash)
                {
                    _messages.Remove(id);
                    _bodies.Remove(id);
                    _drafts.Remove(id);
                    removed.Add(id);
                }
            }
        }

        Raise(MailDataChangeKind.Removed, removed);
        return Task.CompletedTask;
    }

    public Task<Guid> SaveDraftAsync(DraftMessage draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Guid id;
        bool isNew;
        lock (_gate)
        {
            var account = _accounts.First(a => a.Id == draft.AccountId);
            var drafts = _folders.First(f => f.AccountId == account.Id && f.Kind == FolderKind.Drafts);
            id = draft.DraftId ?? Guid.NewGuid();
            isNew = !_messages.ContainsKey(id);
            var stored = draft with { DraftId = id };
            _drafts[id] = stored;
            _messages[id] = new MessageSummary
            {
                Id = id,
                AccountId = account.Id,
                FolderId = drafts.Id,
                FolderKind = FolderKind.Drafts,
                From = account.Address,
                To = draft.To,
                Cc = draft.Cc,
                Subject = draft.Subject,
                Preview = HtmlText.Preview(draft.TextBody),
                Date = _clock.Now,
                IsRead = true,
                HasAttachments = draft.Attachments.Count > 0,
            };
            _bodies[id] = new MessageBody
            {
                MessageId = id,
                HtmlBody = draft.IsPlainText ? null : ComposeHtml(draft),
                TextBody = ComposeText(draft),
                Attachments = draft.Attachments.Select(ToAttachmentInfo).ToList(),
                Bcc = draft.Bcc,
            };
        }

        Raise(isNew ? MailDataChangeKind.Added : MailDataChangeKind.FlagsChanged, [id]);
        return Task.FromResult(id);
    }

    public Task<DraftMessage?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        lock (_gate) return Task.FromResult(_drafts.GetValueOrDefault(draftId));
    }

    public async Task<SendResult> SendAsync(DraftMessage draft, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!draft.HasAnyRecipient)
        {
            return new SendResult(false, "Add at least one recipient before sending.");
        }

        // Simulated progress so the compose window's feedback can be exercised. Nothing leaves this computer.
        for (var step = 1; step <= 5; step++)
        {
            await Task.Delay(_latency / 3, cancellationToken).ConfigureAwait(false);
            progress?.Report(step / 5.0);
        }

        var id = Guid.NewGuid();
        lock (_gate)
        {
            var account = _accounts.First(a => a.Id == draft.AccountId);
            var sent = _folders.First(f => f.AccountId == account.Id && f.Kind == FolderKind.Sent);
            _messages[id] = new MessageSummary
            {
                Id = id,
                AccountId = account.Id,
                FolderId = sent.Id,
                FolderKind = FolderKind.Sent,
                MessageIdHeader = $"<{id:N}@sidephase-demo.example>",
                From = account.Address,
                To = draft.To,
                Cc = draft.Cc,
                Subject = draft.Subject,
                Preview = HtmlText.Preview(draft.TextBody),
                Date = _clock.Now,
                IsRead = true,
                HasAttachments = draft.Attachments.Count > 0,
            };
            _bodies[id] = new MessageBody
            {
                MessageId = id,
                HtmlBody = draft.IsPlainText ? null : ComposeHtml(draft),
                TextBody = ComposeText(draft),
                Attachments = draft.Attachments.Select(ToAttachmentInfo).ToList(),
                Bcc = draft.Bcc,
            };

            if (draft.DraftId is { } draftId)
            {
                _messages.Remove(draftId);
                _bodies.Remove(draftId);
                _drafts.Remove(draftId);
            }
        }

        Raise(MailDataChangeKind.Added, draft.DraftId is { } d ? [id, d] : [id]);
        return new SendResult(true, "Demo mode: the message was saved to Sent but was not delivered to anyone.", id);
    }

    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(_latency * 2, cancellationToken).ConfigureAwait(false);
        return new SyncResult(true, 0, "Demonstration data is up to date. Real synchronisation arrives with Milestone 2.");
    }

    public Task<IReadOnlyDictionary<string, IReadOnlySet<Guid>>> SearchDownloadedTextAsync(IReadOnlyCollection<string> terms, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyDictionary<string, IReadOnlySet<Guid>> result = terms.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(
                t => t,
                t =>
                {
                    var term = SearchIndexQuery.FromKey(t);
                    return (IReadOnlySet<Guid>)_bodies.Values
                        .Where(b => term.Operator == "filename"
                            ? b.Attachments.Any(a => a.FileName.Contains(term.Value, StringComparison.CurrentCultureIgnoreCase))
                            : b.TextBody?.Contains(term.Value, StringComparison.CurrentCultureIgnoreCase) == true)
                        .Select(b => b.MessageId)
                        .ToHashSet();
                },
                StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(result);
        }
    }

    public Task<ServerSearchResult> SearchServerAsync(string query, MailboxScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ServerSearchResult(false, new HashSet<Guid>(), "The demonstration mailbox has no server to search."));

    public Task ClearLocalDataAsync(CancellationToken cancellationToken = default)
    {
        Seed();
        Raise(MailDataChangeKind.Reset, []);
        return Task.CompletedTask;
    }

    private void Seed()
    {
        var data = DemoDataSeeder.Create(_clock.Now);
        lock (_gate)
        {
            _accounts = data.Accounts.ToList();
            _folders = data.Folders.ToList();
            _messages = data.Messages.ToDictionary(m => m.Id);
            _bodies = data.Bodies.ToDictionary(b => b.MessageId);
            _drafts = data.Drafts.ToDictionary(d => d.DraftId!.Value);
        }
    }

    private Task UpdateFlags(IReadOnlyCollection<Guid> ids, Func<MessageSummary, MessageSummary> update)
    {
        var changed = new List<Guid>();
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (!_messages.TryGetValue(id, out var m)) continue;
                var next = update(m);
                if (next == m) continue;
                _messages[id] = next;
                changed.Add(id);
            }
        }

        Raise(MailDataChangeKind.FlagsChanged, changed);
        return Task.CompletedTask;
    }

    private void Raise(MailDataChangeKind kind, IReadOnlyCollection<Guid> ids)
    {
        if (kind != MailDataChangeKind.Reset && ids.Count == 0) return;
        DataChanged?.Invoke(this, new MailDataChangedEventArgs(kind, ids));
    }

    private static string ComposeHtml(DraftMessage draft) =>
        (draft.HtmlBody ?? PlainTextHtml.Convert(draft.TextBody)) + (draft.QuotedHtml ?? string.Empty);

    private static string ComposeText(DraftMessage draft) =>
        draft.TextBody + (draft.QuotedText is { Length: > 0 } q ? "\n\n" + q : string.Empty);

    private static AttachmentInfo ToAttachmentInfo(DraftAttachment a) => new(a.FileName, a.ContentType, a.SizeBytes);
}
