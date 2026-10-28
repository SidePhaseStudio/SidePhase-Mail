using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Services;

/// <summary>One attachment found by the attachment finder, with the message it belongs to.</summary>
public sealed record AttachmentHit(MessageSummary Message, AttachmentInfo Attachment);

/// <summary>Filters for the attachment finder. Empty values mean "any".</summary>
public sealed record AttachmentQuery
{
    public string FileName { get; init; } = string.Empty;

    /// <summary>A group from <see cref="AttachmentTypes.Groups"/> or an extension; empty for any.</summary>
    public string Type { get; init; } = string.Empty;

    public long MinimumSize { get; init; }
    public string Sender { get; init; } = string.Empty;
    public DateTimeOffset? After { get; init; }
}

/// <summary>
/// Finds attachments across all cached mail using only their recorded names, types and sizes, so nothing has to be
/// downloaded to search. Junk and Trash are left out; newest first.
/// </summary>
public static class AttachmentFinder
{
    public static IReadOnlyList<AttachmentHit> Find(IEnumerable<MessageSummary> messages, AttachmentQuery query, int limit = 2000)
    {
        var name = query.FileName.Trim();
        var sender = query.Sender.Trim();
        return messages
            .Where(m => m.Attachments.Count > 0 && m.FolderKind is not (FolderKind.Junk or FolderKind.Trash))
            .Where(m => query.After is not { } after || m.Date >= after)
            .Where(m => sender.Length == 0 || Contains(m.From.Address, sender) || Contains(m.From.Name, sender))
            .OrderByDescending(m => m.Date)
            .SelectMany(m => m.Attachments.Select(a => new AttachmentHit(m, a)))
            .Where(h => name.Length == 0 || Contains(h.Attachment.FileName, name))
            .Where(h => query.Type.Length == 0 || AttachmentTypes.Matches(h.Attachment, query.Type))
            .Where(h => h.Attachment.SizeBytes >= query.MinimumSize)
            .Take(limit)
            .ToList();
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
}
