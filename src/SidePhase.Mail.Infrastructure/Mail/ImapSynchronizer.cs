using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Rendering;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Infrastructure.Mail;

public sealed record FolderSyncResult(IReadOnlyList<Guid> NewMessageIds, int FlagChanges, int Removed, bool WasReset);

/// <summary>Brings the SQLite cache in line with an IMAP account. Read-only towards the server.</summary>
public sealed class ImapSynchronizer(MailCache cache)
{
    private const int FetchBatchSize = 100;
    private const int FlagBatchSize = 500;

    /// <summary>How many of the most recent messages per folder are downloaded on first sync.</summary>
    public int InitialMessageLimit { get; set; } = 500;

    // Received headers are fetched only to check where the spam headers came from (see SpamHeaderTrust).
    private static readonly string[] HeaderFields = ["X-Spam-Status", "X-Spam-Flag", "X-Spam-Score", "X-Spam-Level", "X-Spam-Report", "Received"];

    internal static FetchRequest SummaryRequest()
    {
        var headers = new HeaderSet();
        foreach (var field in HeaderFields) headers.Add(field);
        return new FetchRequest(
            MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
            MessageSummaryItems.InternalDate | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure |
            MessageSummaryItems.PreviewText | MessageSummaryItems.References)
        {
            Headers = headers,
        };
    }

    /// <summary>Discovers the account's folders, classifies them and updates the cache. Returns the cached folders.</summary>
    public async Task<IReadOnlyList<StoredFolder>> SyncFolderListAsync(ImapClient client, Guid accountId, CancellationToken ct = default)
    {
        var personal = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', string.Empty);
        var remote = (await client.GetFoldersAsync(personal, StatusItems.None, subscribedOnly: false, ct).ConfigureAwait(false)).ToList();
        if (!remote.Any(f => f.FullName.Equals(client.Inbox.FullName, StringComparison.OrdinalIgnoreCase))) remote.Insert(0, client.Inbox);

        const FolderAttributes specialUse = FolderAttributes.Sent | FolderAttributes.Drafts | FolderAttributes.Trash |
                                            FolderAttributes.Junk | FolderAttributes.Archive | FolderAttributes.All;
        var hasArchive = remote.Any(f => f.Attributes.HasFlag(FolderAttributes.Archive));

        var candidates = remote
            .Select(f => (Folder: f, Class: FolderClassifier.Classify(f.Attributes, f.FullName, f.Name, ReferenceEquals(f, client.Inbox) || f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)), Special: (f.Attributes & specialUse) != 0))
            .OrderByDescending(c => c.Class.Kind == FolderKind.Inbox)
            .ThenByDescending(c => c.Special)
            .ThenBy(c => c.Folder.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var taken = new HashSet<FolderKind>();
        var result = new List<StoredFolder>();
        foreach (var (folder, classification, _) in candidates)
        {
            var kind = classification.Kind;
            var synchronize = classification.Synchronize;

            // Gmail exposes both [Gmail]/All Mail (\All) and sometimes a real \Archive: prefer the real one.
            if (folder.Attributes.HasFlag(FolderAttributes.All) && hasArchive) continue;

            if (kind != FolderKind.Custom && !taken.Add(kind))
            {
                kind = FolderKind.Custom; // a second "Sent"-like folder becomes an ordinary folder
            }

            if (kind == FolderKind.Custom && !synchronize) continue; // virtual or non-selectable

            result.Add(new StoredFolder
            {
                Id = MessageIdentity.ForFolder(accountId, folder.FullName),
                AccountId = accountId,
                Path = folder.FullName,
                Name = DisplayName(folder, personal, client.Inbox),
                Kind = kind,
                SortOrder = kind == FolderKind.Custom ? 10 : classification.SortOrder,
                Synchronize = synchronize,
            });
        }

        var existing = await cache.GetFoldersAsync(accountId, ct).ConfigureAwait(false);
        foreach (var folder in result) await cache.UpsertFolderAsync(folder, ct).ConfigureAwait(false);
        foreach (var gone in existing.Where(e => result.All(r => r.Path != e.Path)))
        {
            await cache.DeleteFolderAsync(gone.Id, ct).ConfigureAwait(false);
        }

        return await cache.GetFoldersAsync(accountId, ct).ConfigureAwait(false);
    }

    /// <summary>Incrementally synchronises one folder (new messages, flag changes, removals, UIDVALIDITY resets).</summary>
    public async Task<FolderSyncResult> SyncFolderAsync(ImapClient client, StoredFolder folder, CancellationToken ct = default)
    {
        var remote = await client.GetFolderAsync(folder.Path, ct).ConfigureAwait(false);
        await remote.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        try
        {
            // Messages flagged \Deleted (awaiting expunge) are treated as gone.
            var serverUids = (await remote.SearchAsync(SearchQuery.NotDeleted, ct).ConfigureAwait(false)).Select(u => u.Id).ToList();
            var validity = remote.UidValidity;
            var cachedUids = folder.UidValidity == validity
                ? await cache.GetUidsAsync(folder.Id, validity, ct).ConfigureAwait(false)
                : [];
            var plan = SyncPlanner.Plan(folder.UidValidity, folder.HighestUid, cachedUids, validity, serverUids, InitialMessageLimit);

            if (plan.ResetFolder) await cache.ResetFolderAsync(folder.Id, ct).ConfigureAwait(false);

            var removed = plan.RemoveFromCache.Count == 0
                ? 0
                : await cache.DeleteMessagesAsync(folder.Id, validity, plan.RemoveFromCache, ct).ConfigureAwait(false);

            var flagChanges = 0;
            foreach (var batch in plan.RefreshFlags.Chunk(FlagBatchSize))
            {
                var summaries = await remote.FetchAsync(batch.Select(u => new UniqueId(validity, u)).ToList(), new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags), ct).ConfigureAwait(false);
                flagChanges += await cache.UpdateFlagsAsync(folder.Id, validity, summaries.Select(s =>
                    (s.UniqueId.Id, s.Flags?.HasFlag(MessageFlags.Seen) == true, s.Flags?.HasFlag(MessageFlags.Flagged) == true)), ct).ConfigureAwait(false);
            }

            var newIds = new List<Guid>();
            var request = SummaryRequest();
            foreach (var batch in plan.FetchNew.Chunk(FetchBatchSize))
            {
                var summaries = await remote.FetchAsync(batch.Select(u => new UniqueId(validity, u)).ToList(), request, ct).ConfigureAwait(false);
                var stored = summaries.Select(s => ToStored(s, folder, validity)).ToList();
                await cache.UpsertMessagesAsync(stored, ct).ConfigureAwait(false);
                newIds.AddRange(stored.Select(s => s.Id));
            }

            var highest = serverUids.Count == 0 ? (plan.ResetFolder ? 0u : folder.HighestUid) : Math.Max(serverUids.Max(), plan.ResetFolder ? 0u : folder.HighestUid);
            await cache.UpdateFolderSyncStateAsync(folder.Id, validity, highest, remote.HighestModSeq, ct).ConfigureAwait(false);
            return new FolderSyncResult(newIds, flagChanges, removed, plan.ResetFolder);
        }
        finally
        {
            if (remote.IsOpen) await remote.CloseAsync(false, ct).ConfigureAwait(false);
        }
    }

    internal static StoredMessage ToStored(IMessageSummary s, StoredFolder folder, uint uidValidity)
    {
        var envelope = s.Envelope;
        var mailbox = envelope?.From.Mailboxes.FirstOrDefault() ?? envelope?.Sender.Mailboxes.FirstOrDefault();
        var from = mailbox is null
            ? new EmailAddress(null, "unknown")
            : new EmailAddress(string.IsNullOrWhiteSpace(mailbox.Name) ? null : mailbox.Name, mailbox.Address);
        var date = envelope?.Date ?? s.InternalDate ?? DateTimeOffset.UtcNow;
        var subject = envelope?.Subject ?? string.Empty;
        var messageId = envelope?.MessageId;
        var flags = s.Flags ?? MessageFlags.None;

        return new StoredMessage
        {
            Id = MessageIdentity.ForImapMessage(folder.AccountId, folder.Path, uidValidity, s.UniqueId.Id),
            AccountId = folder.AccountId,
            FolderId = folder.Id,
            Uid = s.UniqueId.Id,
            UidValidity = uidValidity,
            MessageIdHeader = messageId,
            IdentityKey = MessageIdentity.IdentityKey(messageId, from, date, subject),
            From = from,
            To = envelope is null ? [] : MessageBodyExtractor.ToAddresses(envelope.To),
            Cc = envelope is null ? [] : MessageBodyExtractor.ToAddresses(envelope.Cc),
            Subject = subject,
            Preview = HtmlText.Preview(s.PreviewText),
            Date = date,
            IsRead = flags.HasFlag(MessageFlags.Seen),
            IsStarred = flags.HasFlag(MessageFlags.Flagged),
            HasAttachments = s.Attachments?.Any() == true,
            Spam = s.Headers is null ? Core.Spam.SpamReport.None : MessageBodyExtractor.ReadSpamHeaders(s.Headers),
            Size = s.Size ?? 0,
            InReplyTo = envelope?.InReplyTo,
            References = s.References?.ToList() ?? [],
        };
    }

    private static string DisplayName(IMailFolder folder, FolderNamespace personal, IMailFolder inbox)
    {
        if (folder.FullName.Equals(inbox.FullName, StringComparison.OrdinalIgnoreCase)) return "Inbox";
        var name = folder.FullName;
        if (!string.IsNullOrEmpty(personal.Path) && name.StartsWith(personal.Path, StringComparison.Ordinal)) name = name[personal.Path.Length..];
        if (name.StartsWith("[Gmail]", StringComparison.Ordinal) && folder.DirectorySeparator != '\0') name = folder.Name;
        return folder.DirectorySeparator == '\0' ? name : name.Replace(folder.DirectorySeparator.ToString(), " / ", StringComparison.Ordinal);
    }
}
