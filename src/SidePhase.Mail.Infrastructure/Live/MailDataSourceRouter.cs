using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Settings;
using SidePhase.Mail.Infrastructure.Demo;

namespace SidePhase.Mail.Infrastructure.Live;

/// <summary>
/// The app's <see cref="IMailDataSource"/>: either the demonstration mailbox or real accounts, never both. Events from
/// the inactive source are dropped, so demonstration messages can't appear in a real inbox (or the reverse).
/// </summary>
public sealed class MailDataSourceRouter : IMailDataSource
{
    private readonly DemoMailDataSource _demo;
    private readonly LiveMailDataSource _live;
    private readonly ISettingsService _settings;
    private DataMode _mode;

    public MailDataSourceRouter(DemoMailDataSource demo, LiveMailDataSource live, ISettingsService settings)
    {
        _demo = demo;
        _live = live;
        _settings = settings;
        _mode = settings.Current.DataMode;

        _demo.DataChanged += (_, e) => { if (_mode == DataMode.Demo) DataChanged?.Invoke(this, e); };
        _live.DataChanged += (_, e) => { if (_mode == DataMode.Live) DataChanged?.Invoke(this, e); };
        _live.ErrorOccurred += (_, e) => { if (_mode == DataMode.Live) ErrorOccurred?.Invoke(this, e); };
        settings.SettingsChanged += (_, e) =>
        {
            if (e.Settings.DataMode == _mode) return;
            _mode = e.Settings.DataMode;
            ModeChanged?.Invoke(this, EventArgs.Empty);
            DataChanged?.Invoke(this, new MailDataChangedEventArgs(MailDataChangeKind.Reset, []));
            if (_mode == DataMode.Live) _ = _live.SyncAsync();
        };
    }

    private IMailDataSource Active => _mode == DataMode.Live ? _live : _demo;

    public DataMode Mode => _mode;

    public bool IsDemo => Active.IsDemo;

    public event EventHandler<MailDataChangedEventArgs>? DataChanged;

    public event EventHandler<MailErrorEventArgs>? ErrorOccurred;

    /// <summary>Raised (before the Reset data change) when switching between demo and real accounts.</summary>
    public event EventHandler? ModeChanged;

    public Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken cancellationToken = default) => Active.GetAccountsAsync(cancellationToken);

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid accountId, CancellationToken cancellationToken = default) => Active.GetFoldersAsync(accountId, cancellationToken);

    public Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(CancellationToken cancellationToken = default) => Active.GetMessagesAsync(cancellationToken);

    public Task<MessageSummary?> GetMessageAsync(Guid messageId, CancellationToken cancellationToken = default) => Active.GetMessageAsync(messageId, cancellationToken);

    public Task<MessageBody?> GetMessageBodyAsync(Guid messageId, CancellationToken cancellationToken = default) => Active.GetMessageBodyAsync(messageId, cancellationToken);

    public Task<byte[]> GetAttachmentContentAsync(Guid messageId, int attachmentIndex, CancellationToken cancellationToken = default) => Active.GetAttachmentContentAsync(messageId, attachmentIndex, cancellationToken);

    public Task SaveAttachmentAsync(Guid messageId, int attachmentIndex, Stream destination, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Active.SaveAttachmentAsync(messageId, attachmentIndex, destination, progress, cancellationToken);

    public Task SetReadAsync(IReadOnlyCollection<Guid> messageIds, bool isRead, CancellationToken cancellationToken = default) => Active.SetReadAsync(messageIds, isRead, cancellationToken);

    public Task SetStarredAsync(IReadOnlyCollection<Guid> messageIds, bool isStarred, CancellationToken cancellationToken = default) => Active.SetStarredAsync(messageIds, isStarred, cancellationToken);

    public Task SetPinnedAsync(IReadOnlyCollection<Guid> messageIds, bool isPinned, CancellationToken cancellationToken = default) => Active.SetPinnedAsync(messageIds, isPinned, cancellationToken);

    public Task SetSnoozeAsync(IReadOnlyCollection<Guid> messageIds, DateTimeOffset? until, CancellationToken cancellationToken = default) => Active.SetSnoozeAsync(messageIds, until, cancellationToken);

    public Task<IReadOnlyList<MessageSummary>> ReleaseExpiredSnoozesAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => Active.ReleaseExpiredSnoozesAsync(now, cancellationToken);

    public Task SetFollowUpAsync(IReadOnlyCollection<Guid> messageIds, DateTimeOffset? due, CancellationToken cancellationToken = default) => Active.SetFollowUpAsync(messageIds, due, cancellationToken);

    public Task<IReadOnlyList<MessageSummary>> TakeDueFollowUpsAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => Active.TakeDueFollowUpsAsync(now, cancellationToken);

    public Task<IReadOnlyList<MoveRecord>> MoveToAsync(IReadOnlyCollection<Guid> messageIds, FolderKind target, CancellationToken cancellationToken = default) => Active.MoveToAsync(messageIds, target, cancellationToken);

    public Task<IReadOnlyList<MoveRecord>> MoveToFolderAsync(IReadOnlyCollection<Guid> messageIds, Guid folderId, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Active.MoveToFolderAsync(messageIds, folderId, progress, cancellationToken);

    public Task SetCategoryAsync(IReadOnlyCollection<Guid> messageIds, string category, bool add, CancellationToken cancellationToken = default) =>
        Active.SetCategoryAsync(messageIds, category, add, cancellationToken);

    public Task RestoreAsync(IReadOnlyCollection<MoveRecord> moves, CancellationToken cancellationToken = default) => Active.RestoreAsync(moves, cancellationToken);

    public Task<int> DeletePermanentlyAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken = default) => Active.DeletePermanentlyAsync(messageIds, cancellationToken);

    public Task<Guid> SaveDraftAsync(DraftMessage draft, CancellationToken cancellationToken = default) => Active.SaveDraftAsync(draft, cancellationToken);

    public Task<DraftMessage?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default) => Active.GetDraftAsync(draftId, cancellationToken);

    public Task<SendResult> SendAsync(DraftMessage draft, IProgress<double>? progress = null, CancellationToken cancellationToken = default) => Active.SendAsync(draft, progress, cancellationToken);

    public Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default) => Active.SyncAsync(cancellationToken);

    public Task ClearLocalDataAsync(CancellationToken cancellationToken = default) => Active.ClearLocalDataAsync(cancellationToken);

    public Task<IReadOnlyDictionary<string, IReadOnlySet<Guid>>> SearchDownloadedTextAsync(IReadOnlyCollection<string> terms, CancellationToken cancellationToken = default) =>
        Active.SearchDownloadedTextAsync(terms, cancellationToken);

    public Task<ServerSearchResult> SearchServerAsync(string query, MailboxScope scope, CancellationToken cancellationToken = default) =>
        Active.SearchServerAsync(query, scope, cancellationToken);
}
