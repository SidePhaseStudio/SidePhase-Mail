using MailKit;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Infrastructure.Mail;

public sealed record FolderClassification(FolderKind Kind, bool Synchronize, int SortOrder);

/// <summary>
/// Maps IMAP folders to SidePhase folder kinds using RFC 6154 special-use attributes first, then common names
/// (English and the usual localised variants).
/// </summary>
public static class FolderClassifier
{
    private static readonly string[] SentNames = ["sent", "sent items", "sent mail", "sent messages", "gesendet", "gesendete elemente", "envoyés", "elementos enviados", "inviata", "posta inviata"];
    private static readonly string[] DraftNames = ["drafts", "draft", "entwürfe", "brouillons", "borradores", "bozze"];
    private static readonly string[] TrashNames = ["trash", "deleted items", "deleted messages", "deleted", "bin", "papierkorb", "gelöschte elemente", "corbeille", "papelera", "cestino"];
    private static readonly string[] JunkNames = ["junk", "junk e-mail", "junk email", "spam", "bulk mail", "spamverdacht", "courrier indésirable"];
    private static readonly string[] ArchiveNames = ["archive", "archives", "archiv", "archivio", "archivo"];

    public static FolderClassification Classify(FolderAttributes attributes, string fullName, string name, bool isInbox)
    {
        if ((attributes & (FolderAttributes.NoSelect | FolderAttributes.NonExistent)) != 0)
        {
            return new FolderClassification(FolderKind.Custom, Synchronize: false, 900);
        }

        if (isInbox || fullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)) return new(FolderKind.Inbox, true, 0);
        if (attributes.HasFlag(FolderAttributes.Drafts)) return new(FolderKind.Drafts, true, 1);
        if (attributes.HasFlag(FolderAttributes.Sent)) return new(FolderKind.Sent, true, 2);
        if (attributes.HasFlag(FolderAttributes.Archive)) return new(FolderKind.Archive, true, 3);
        if (attributes.HasFlag(FolderAttributes.Junk)) return new(FolderKind.Junk, true, 4);
        if (attributes.HasFlag(FolderAttributes.Trash)) return new(FolderKind.Trash, true, 5);

        // Gmail's "All Mail" is where archived mail lives. It repeats every message, so it's used as the archive
        // target but not downloaded.
        if (attributes.HasFlag(FolderAttributes.All)) return new FolderClassification(FolderKind.Archive, Synchronize: false, 3);

        // Other virtual folders only repeat messages stored elsewhere (Gmail "Starred", "Important").
        if ((attributes & (FolderAttributes.Flagged | FolderAttributes.Important)) != 0)
        {
            return new FolderClassification(FolderKind.Custom, Synchronize: false, 800);
        }

        var lower = name.Trim().ToLowerInvariant();
        if (DraftNames.Contains(lower)) return new(FolderKind.Drafts, true, 1);
        if (SentNames.Contains(lower)) return new(FolderKind.Sent, true, 2);
        if (ArchiveNames.Contains(lower)) return new(FolderKind.Archive, true, 3);
        if (JunkNames.Contains(lower)) return new(FolderKind.Junk, true, 4);
        if (TrashNames.Contains(lower)) return new(FolderKind.Trash, true, 5);
        return new FolderClassification(FolderKind.Custom, true, 10);
    }

    /// <summary>When a server has several candidates (e.g. "Sent" and "Sent Items"), keep only the best one per kind.</summary>
    public static bool IsPreferredKind(FolderKind kind) => kind is not FolderKind.Custom;
}
