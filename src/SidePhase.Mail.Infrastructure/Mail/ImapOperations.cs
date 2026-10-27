using MailKit;
using MailKit.Net.Imap;
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
        var newUid = await source.MoveToAsync(new UniqueId(source.UidValidity, uid), destination, ct).ConfigureAwait(false);
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

    public static async Task<string> CreateFolderAsync(ImapClient client, string name, CancellationToken ct)
    {
        var personal = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', string.Empty);
        var root = await client.GetFolderAsync(personal.Path, ct).ConfigureAwait(false);
        var created = await root.CreateAsync(name, isMessageFolder: true, ct).ConfigureAwait(false);
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
