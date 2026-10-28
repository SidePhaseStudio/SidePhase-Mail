using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Services;

/// <summary>Messages that belong to one conversation, oldest first.</summary>
/// <param name="Key">Stable identity: the id of the earliest message, so it survives new replies arriving.</param>
public sealed record Conversation(Guid Key, IReadOnlyList<MessageSummary> Messages)
{
    public int Count => Messages.Count;
    public MessageSummary Latest => Messages[^1];
}

/// <summary>
/// Groups messages into conversations like Gmail. Messages are linked through their Message-ID, References and
/// In-Reply-To headers, which is reliable. Replies without those headers fall back to the subject: a reply ("Re: …")
/// joins messages with the same subject that share a participant and are within 60 days, so unrelated messages that
/// merely share a subject such as "Invoice" are not merged. Conversations never span accounts.
/// </summary>
public static class ConversationGrouper
{
    private static readonly TimeSpan SubjectWindow = TimeSpan.FromDays(60);

    public static IReadOnlyList<Conversation> Group(IEnumerable<MessageSummary> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = messages.ToList();
        var parent = Enumerable.Range(0, list.Count).ToArray();

        int Find(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }

        // 1. Header links: any two messages naming the same Message-ID (their own or a referenced one) are related.
        var byId = new Dictionary<(Guid, string), int>();
        for (var i = 0; i < list.Count; i++)
        {
            var m = list[i];
            foreach (var id in Ids(m))
            {
                var key = (m.AccountId, id);
                if (byId.TryGetValue(key, out var other)) Union(i, other);
                else byId[key] = i;
            }
        }

        // 2. Subject fallback for replies.
        var bySubject = new Dictionary<(Guid, string), List<int>>();
        for (var i = 0; i < list.Count; i++)
        {
            var subject = Normalize(list[i].Subject);
            if (subject.Length == 0) continue;
            var key = (list[i].AccountId, subject);
            if (!bySubject.TryGetValue(key, out var bucket)) bySubject[key] = bucket = [];
            bucket.Add(i);
        }

        foreach (var bucket in bySubject.Values.Where(b => b.Count > 1))
        {
            // Only replies that carry no reply headers: when headers exist they are authoritative.
            foreach (var i in bucket.Where(i => list[i].References.Count == 0 && IsReply(list[i].Subject)))
            {
                foreach (var j in bucket)
                {
                    if (i == j || Find(i) == Find(j)) continue;
                    if ((list[i].Date - list[j].Date).Duration() <= SubjectWindow && ShareParticipant(list[i], list[j])) Union(i, j);
                }
            }
        }

        return Enumerable.Range(0, list.Count)
            .GroupBy(Find)
            .Select(g =>
            {
                var ordered = g.Select(i => list[i]).OrderBy(m => m.Date).ThenBy(m => m.Id).ToList();
                return new Conversation(ordered[0].Id, ordered);
            })
            .ToList();
    }

    /// <summary>The subject without reply/forward prefixes, case-folded, for comparison.</summary>
    public static string Normalize(string subject) => MessageListBuilder.StripPrefixes(subject ?? string.Empty).Trim().ToLowerInvariant();

    private static bool IsReply(string subject) => !string.Equals(MessageListBuilder.StripPrefixes(subject).Trim(), subject.Trim(), StringComparison.Ordinal);

    private static IEnumerable<string> Ids(MessageSummary m)
    {
        if (Clean(m.MessageIdHeader) is { } own) yield return own;
        foreach (var reference in m.References)
        {
            if (Clean(reference) is { } id) yield return id;
        }
    }

    private static string? Clean(string? id)
    {
        var trimmed = id?.Trim().Trim('<', '>').Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToLowerInvariant();
    }

    private static bool ShareParticipant(MessageSummary a, MessageSummary b)
    {
        var people = Participants(a).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Participants(b).Any(people.Contains);
    }

    private static IEnumerable<string> Participants(MessageSummary m) =>
        m.To.Concat(m.Cc).Append(m.From).Select(a => a.Address.Trim()).Where(a => a.Length > 0);
}
