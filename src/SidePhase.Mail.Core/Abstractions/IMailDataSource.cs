using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Abstractions;

public enum MailDataChangeKind
{
    /// <summary>Flags such as read, starred or pinned changed.</summary>
    FlagsChanged,
    Moved,
    Added,
    Removed,

    /// <summary>Accounts, folders or the whole store changed; reload everything.</summary>
    Reset,
}

public sealed class MailDataChangedEventArgs(MailDataChangeKind kind, IReadOnlyCollection<Guid> messageIds) : EventArgs
{
    public MailDataChangeKind Kind { get; } = kind;
    public IReadOnlyCollection<Guid> MessageIds { get; } = messageIds;
}

/// <summary>Where a message was before a move, so the move can be undone.</summary>
public sealed record MoveRecord(Guid MessageId, Guid FromFolderId);

public sealed record SendResult(bool Succeeded, string Message, Guid? SentMessageId = null)
{
    /// <summary>How sure we are about the result (used by the outbox to decide whether a retry is safe).</summary>
    public Scheduling.SendOutcome Outcome { get; init; } = Succeeded ? Scheduling.SendOutcome.Accepted : Scheduling.SendOutcome.Rejected;
}

public sealed record SyncResult(bool Succeeded, int NewMessages, string Message);

public sealed record ServerSearchResult(bool Supported, IReadOnlySet<Guid> MessageIds, string Message);

/// <summary>A problem with one account or operation, phrased for the user. Never contains secrets.</summary>
public sealed class MailErrorEventArgs(Guid? accountId, string message, bool requiresSignIn = false) : EventArgs
{
    public Guid? AccountId { get; } = accountId;
    public string Message { get; } = message;

    /// <summary>The account's credentials were rejected or have expired.</summary>
    public bool RequiresSignIn { get; } = requiresSignIn;
}

/// <summary>
/// The application's single entry point to mail data. The UI only talks to this interface, so the demonstration
/// store used in Milestone 1 can be replaced by the SQLite cache + IMAP/SMTP engine without touching view models.
/// </summary>
/// <remarks>Implementations may raise <see cref="DataChanged"/> on any thread.</remarks>
public interface IMailDataSource
{
    /// <summary>True when the data is sample content that never touches a mail server.</summary>
    bool IsDemo { get; }

    event EventHandler<MailDataChangedEventArgs>? DataChanged;

    /// <summary>Raised when a background operation fails (sync, flag change, move). May be raised on any thread.</summary>
    event EventHandler<MailErrorEventArgs>? ErrorOccurred;

    Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailFolder>> GetFoldersAsync(Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>Returns message summaries (never bodies) for every message in the store.</summary>
    Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(CancellationToken cancellationToken = default);

    Task<MessageSummary?> GetMessageAsync(Guid messageId, CancellationToken cancellationToken = default);

    Task<MessageBody?> GetMessageBodyAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>Downloads the content of one attachment (index into <see cref="MessageBody.Attachments"/>).</summary>
    Task<byte[]> GetAttachmentContentAsync(Guid messageId, int attachmentIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one attachment's decoded content to <paramref name="destination"/> without holding the whole message or
    /// attachment in memory. Progress runs from 0 to 1.
    /// </summary>
    Task SaveAttachmentAsync(Guid messageId, int attachmentIndex, Stream destination, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    Task SetReadAsync(IReadOnlyCollection<Guid> messageIds, bool isRead, CancellationToken cancellationToken = default);

    Task SetStarredAsync(IReadOnlyCollection<Guid> messageIds, bool isStarred, CancellationToken cancellationToken = default);

    Task SetPinnedAsync(IReadOnlyCollection<Guid> messageIds, bool isPinned, CancellationToken cancellationToken = default);

    /// <summary>Hides messages until <paramref name="until"/> (null = unsnooze now). Local to this PC; nothing moves on the server.</summary>
    Task SetSnoozeAsync(IReadOnlyCollection<Guid> messageIds, DateTimeOffset? until, CancellationToken cancellationToken = default);

    /// <summary>Ends snoozes that have expired. Returns the messages that are back.</summary>
    Task<IReadOnlyList<MessageSummary>> ReleaseExpiredSnoozesAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Sets a follow-up reminder (null = done/removed). Local to this PC; nothing is sent to anyone.</summary>
    Task SetFollowUpAsync(IReadOnlyCollection<Guid> messageIds, DateTimeOffset? due, CancellationToken cancellationToken = default);

    /// <summary>Reminders that are due and haven't been announced yet; each is returned only once.</summary>
    Task<IReadOnlyList<MessageSummary>> TakeDueFollowUpsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Moves messages to the folder of the given kind in each message's own account.</summary>
    Task<IReadOnlyList<MoveRecord>> MoveToAsync(IReadOnlyCollection<Guid> messageIds, FolderKind target, CancellationToken cancellationToken = default);

    /// <summary>Moves messages back to where they were (undo).</summary>
    Task RestoreAsync(IReadOnlyCollection<MoveRecord> moves, CancellationToken cancellationToken = default);

    /// <summary>Moves messages to a specific folder of their own account, in batches. Progress runs from 0 to 1.</summary>
    Task<IReadOnlyList<MoveRecord>> MoveToFolderAsync(IReadOnlyCollection<Guid> messageIds, Guid folderId, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Adds or removes a local category on messages (stored on this PC by message identity).</summary>
    Task SetCategoryAsync(IReadOnlyCollection<Guid> messageIds, string category, bool add, CancellationToken cancellationToken = default);

    /// <summary>Permanently deletes messages that are already in Trash.</summary>
    Task DeletePermanentlyAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken = default);

    Task<Guid> SaveDraftAsync(DraftMessage draft, CancellationToken cancellationToken = default);

    Task<DraftMessage?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default);

    Task<SendResult> SendAsync(DraftMessage draft, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// For each term, the ids of messages whose downloaded text contains it. Only messages already stored on this PC
    /// are searched.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlySet<Guid>>> SearchDownloadedTextAsync(IReadOnlyCollection<string> terms, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the mail servers to search (including mail older than what's stored locally). Matches that weren't stored
    /// yet are downloaded as headers so they can be shown.
    /// </summary>
    Task<ServerSearchResult> SearchServerAsync(string query, MailboxScope scope, CancellationToken cancellationToken = default);

    /// <summary>Clears locally cached data. Never deletes anything on a mail server.</summary>
    Task ClearLocalDataAsync(CancellationToken cancellationToken = default);
}
