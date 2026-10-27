using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;

namespace SidePhase.Mail.Infrastructure.Mail;

public sealed record AppendResult(uint? Uid, uint UidValidity);

public sealed record MoveResult(uint? NewUid, uint DestinationUidValidity);

/// <summary>Server-side changes. Each one is the direct result of an explicit user action.</summary>
public static class ImapOperations
{
    public static async Task SetFlagsAsync(ImapClient client, string folderPath, uint uid, MessageFlags flags, bool add, CancellationToken ct)
    {
        var folder = await OpenAsync(client, folderPath, FolderAccess.ReadWrite, ct).ConfigureAwait(false);
        var request = new StoreFlagsRequest(add ? StoreAction.Add : StoreAction.Remove, flags) { Silent = true };
        await folder.StoreAsync(new UniqueId(folder.UidValidity, uid), request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Tags a message with the standard <c>$Junk</c> / <c>$NotJunk</c> keywords (RFC 5788 registry) so server-side
    /// filters (e.g. Dovecot antispam, Fastmail) can learn from the user's choice. Skipped silently when the folder
    /// doesn't accept custom keywords.
    /// </summary>
    public static async Task SetJunkKeywordAsync(ImapClient client, string folderPath, uint uid, bool isJunk, CancellationToken ct)
    {
        var folder = await OpenAsync(client, folderPath, FolderAccess.ReadWrite, ct).ConfigureAwait(false);
        if (!folder.PermanentFlags.HasFlag(MessageFlags.UserDefined)) return;
        var id = new UniqueId(folder.UidValidity, uid);
        await folder.StoreAsync(id, new StoreFlagsRequest(StoreAction.Add, [isJunk ? "$Junk" : "$NotJunk"]) { Silent = true }, ct).ConfigureAwait(false);
        await folder.StoreAsync(id, new StoreFlagsRequest(StoreAction.Remove, [isJunk ? "$NotJunk" : "$Junk"]) { Silent = true }, ct).ConfigureAwait(false);
    }

    /// <summary>True if the folder lets clients set \Flagged permanently (otherwise stars are kept locally).</summary>
    public static async Task<bool> SupportsFlaggedAsync(ImapClient client, string folderPath, CancellationToken ct)
    {
        var folder = await OpenAsync(client, folderPath, FolderAccess.ReadWrite, ct).ConfigureAwait(false);
        return folder.PermanentFlags.HasFlag(MessageFlags.Flagged) || folder.AcceptedFlags.HasFlag(MessageFlags.Flagged);
    }

    public static async Task<MoveResult> MoveAsync(ImapClient client, string sourcePath, uint uid, string destinationPath, CancellationToken ct)
    {
        var destination = await client.GetFolderAsync(destinationPath, ct).ConfigureAwait(false);
        var source = await OpenAsync(client, sourcePath, FolderAccess.ReadWrite, ct).ConfigureAwait(false);
        var id = new UniqueId(source.UidValidity, uid);
        UniqueId? newUid;
        if ((client.Capabilities & (ImapCapabilities.Move | ImapCapabilities.UidPlus)) != 0)
        {
            newUid = await source.MoveToAsync(id, destination, ct).ConfigureAwait(false);
        }
        else
        {
            // No MOVE and no UID EXPUNGE: copy, then only flag the original. A plain EXPUNGE could also remove
            // messages another client flagged for deletion, so it's left for the server or the user to purge.
            newUid = await source.CopyToAsync(id, destination, ct).ConfigureAwait(false);
            await source.StoreAsync(id, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct).ConfigureAwait(false);
        }

        return new MoveResult(newUid?.Id, newUid?.Validity ?? 0);
    }

    public static async Task<AppendResult> AppendAsync(ImapClient client, string folderPath, MimeMessage message, MessageFlags flags, CancellationToken ct)
    {
        var folder = await client.GetFolderAsync(folderPath, ct).ConfigureAwait(false);
        var uid = await folder.AppendAsync(new AppendRequest(message, flags), ct).ConfigureAwait(false);
        return new AppendResult(uid?.Id, uid?.Validity ?? 0);
    }

    /// <summary>
    /// Marks the message \Deleted and expunges just that UID when the server supports UIDPLUS. Without UIDPLUS a plain
    /// EXPUNGE could remove other messages flagged elsewhere, so the message is only flagged.
    /// </summary>
    public static async Task DeleteAsync(ImapClient client, string folderPath, uint uid, CancellationToken ct)
    {
        var folder = await OpenAsync(client, folderPath, FolderAccess.ReadWrite, ct).ConfigureAwait(false);
        var id = new UniqueId(folder.UidValidity, uid);
        await folder.StoreAsync(id, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct).ConfigureAwait(false);
        if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
        {
            await folder.ExpungeAsync([id], ct).ConfigureAwait(false);
        }
    }

    public static async Task<MimeMessage> FetchMessageAsync(ImapClient client, string folderPath, uint uid, CancellationToken ct)
    {
        var folder = await OpenAsync(client, folderPath, FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        // MailKit uses BODY.PEEK, so downloading a message doesn't mark it as read on the server.
        return await folder.GetMessageAsync(new UniqueId(folder.UidValidity, uid), ct).ConfigureAwait(false);
    }

    /// <summary>Finds a message's UID in a folder by its Message-ID header (after a move whose new UID wasn't reported).</summary>
    public static async Task<(uint Uid, uint UidValidity)?> FindByMessageIdAsync(ImapClient client, string folderPath, string messageId, CancellationToken ct)
    {
        var folder = await OpenAsync(client, folderPath, FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        var bare = messageId.Trim().Trim('<', '>');
        var uids = await folder.SearchAsync(SearchQuery.HeaderContains("Message-ID", bare).And(SearchQuery.NotDeleted), ct).ConfigureAwait(false);
        return uids.Count == 0 ? null : (uids[^1].Id, folder.UidValidity);
    }

    public static async Task<string> CreateFolderAsync(ImapClient client, string name, CancellationToken ct)
    {
        var personal = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', string.Empty);
        var root = await client.GetFolderAsync(personal.Path, ct).ConfigureAwait(false);
        var created = await root.CreateAsync(name, isMessageFolder: true, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The server didn't create the \"{name}\" folder.");
        await created.SubscribeAsync(ct).ConfigureAwait(false);
        return created.FullName;
    }

    private static async Task<IMailFolder> OpenAsync(ImapClient client, string path, FolderAccess access, CancellationToken ct)
    {
        var folder = await client.GetFolderAsync(path, ct).ConfigureAwait(false);
        if (!folder.IsOpen || (access == FolderAccess.ReadWrite && folder.Access != FolderAccess.ReadWrite))
        {
            await folder.OpenAsync(access, ct).ConfigureAwait(false);
        }

        return folder;
    }
}
