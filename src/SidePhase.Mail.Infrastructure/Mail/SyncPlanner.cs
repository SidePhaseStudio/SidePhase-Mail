namespace SidePhase.Mail.Infrastructure.Mail;

/// <summary>What to do to bring one cached folder in line with the server.</summary>
public sealed record SyncPlan(
    bool ResetFolder,
    IReadOnlyList<uint> FetchNew,
    IReadOnlyList<uint> RefreshFlags,
    IReadOnlyList<uint> RemoveFromCache);

/// <summary>
/// Pure incremental-sync logic based on IMAP UIDs and UIDVALIDITY (RFC 3501 §2.3.1.1).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>If UIDVALIDITY changed, every cached UID is meaningless: reset the folder and start again.</item>
/// <item>Only UIDs above the highest one already seen are downloaded, so older mail beyond the initial window is never
/// back-filled on every sync.</item>
/// <item>On first sync only the most recent <c>initialLimit</c> messages are downloaded.</item>
/// <item>Cached UIDs the server no longer has are removed from the cache (they were moved or expunged elsewhere);
/// nothing is ever deleted on the server.</item>
/// </list>
/// </remarks>
public static class SyncPlanner
{
    public static SyncPlan Plan(
        uint cachedUidValidity,
        uint cachedHighestUid,
        IReadOnlyCollection<uint> cachedUids,
        uint serverUidValidity,
        IReadOnlyList<uint> serverUids,
        int initialLimit)
    {
        var reset = cachedUidValidity != 0 && cachedUidValidity != serverUidValidity;
        var cached = reset ? new HashSet<uint>() : cachedUids.ToHashSet();
        var highest = reset ? 0u : Math.Max(cachedHighestUid, cached.Count == 0 ? 0u : cached.Max());
        var server = serverUids.ToHashSet();

        IReadOnlyList<uint> fetch;
        if (highest == 0 && cached.Count == 0)
        {
            fetch = serverUids.OrderBy(u => u).TakeLast(Math.Max(0, initialLimit)).ToList();
        }
        else
        {
            fetch = serverUids.Where(u => u > highest && !cached.Contains(u)).OrderBy(u => u).ToList();
        }

        var refresh = cached.Where(server.Contains).OrderBy(u => u).ToList();
        var remove = cached.Where(u => !server.Contains(u)).OrderBy(u => u).ToList();
        return new SyncPlan(reset, fetch, refresh, remove);
    }
}
