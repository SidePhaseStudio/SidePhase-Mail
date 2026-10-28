using MailKit.Search;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Services;
using SidePhase.Mail.Infrastructure.Storage;
using SearchTerm = SidePhase.Mail.Core.Services.SearchTerm;

namespace SidePhase.Mail.Infrastructure.Live;

/// <summary>Translates SidePhase search syntax into IMAP SEARCH and picks the folders a view covers.</summary>
public static class ServerSearch
{
    /// <summary>Returns null when nothing in the query can be searched on the server.</summary>
    public static SearchQuery? BuildQuery(IReadOnlyList<SearchTerm> terms, DateTimeOffset now)
    {
        SearchQuery? query = null;
        foreach (var term in terms)
        {
            var v = term.Value;
            SearchQuery? part = term.Operator switch
            {
                null => SearchQuery.FromContains(v).Or(SearchQuery.ToContains(v)).Or(SearchQuery.SubjectContains(v)).Or(SearchQuery.BodyContains(v)),
                "from" => SearchQuery.FromContains(v),
                "to" => SearchQuery.ToContains(v).Or(SearchQuery.CcContains(v)),
                "subject" => SearchQuery.SubjectContains(v),
                "body" => SearchQuery.BodyContains(v),
                "is" when v.Equals("unread", StringComparison.OrdinalIgnoreCase) => SearchQuery.NotSeen,
                "is" when v.Equals("read", StringComparison.OrdinalIgnoreCase) => SearchQuery.Seen,
                "is" when v.ToLowerInvariant() is "starred" or "favourite" or "favorite" => SearchQuery.Flagged,
                "after" when MessageSearch.TryParseDate(v, out var d) => SearchQuery.SentSince(d.AddDays(1)),
                "before" when MessageSearch.TryParseDate(v, out var d) => SearchQuery.SentBefore(d),
                "on" when MessageSearch.TryParseDate(v, out var d) => SearchQuery.SentOn(d),
                "newer_than" when MessageSearch.TryParseAge(v, out var age) => SearchQuery.SentSince((now - age).Date),
                "older_than" when MessageSearch.TryParseAge(v, out var age) => SearchQuery.SentBefore((now - age).Date),
                _ => null, // local-only conditions (is:pinned, has:attachment) are applied to the results afterwards
            };

            if (part is not null) query = query is null ? part : query.And(part);
        }

        return query is null ? null : query.And(SearchQuery.NotDeleted);
    }

    /// <summary>The folders a view shows, or null if the view is local-only (Pinned, Favourites).</summary>
    public static Func<StoredFolder, bool>? FolderFilter(MailboxScope scope) => scope.Kind switch
    {
        MailboxScopeKind.UnifiedInbox => f => f.Kind == FolderKind.Inbox,
        MailboxScopeKind.AllAccounts => f => f.Kind is not (FolderKind.Junk or FolderKind.Trash),
        MailboxScopeKind.UnifiedFolder => f => f.Kind == scope.FolderKind,
        MailboxScopeKind.AccountFolder => f => f.Id == scope.FolderId,
        _ => null,
    };
}
