namespace SidePhase.Mail.Core.Organisation;

/// <summary>
/// A virtual folder: a saved search shown in the sidebar. It never moves or copies mail; it just lists the messages
/// that match <see cref="Query"/> (the same operators as the search box).
/// </summary>
public sealed record SmartFolder
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Query { get; init; }

    /// <summary>Segoe Fluent icon code point (one character).</summary>
    public string Glyph { get; init; } = "";

    public int Position { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public interface ISmartFolderStore
{
    Task<IReadOnlyList<SmartFolder>> ListAsync(CancellationToken ct = default);
    Task SaveAsync(SmartFolder folder, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Smart folder definitions, with a few useful ones created the first time.</summary>
public sealed class SmartFolderService(ISmartFolderStore store, Abstractions.ISettingsService settings, Abstractions.IClock clock)
{
    public event EventHandler? Changed;

    /// <summary>
    /// The starter set. Pinned, Favourites and Follow-ups already have their own sidebar entries, so they aren't
    /// repeated here.
    /// </summary>
    public static IReadOnlyList<(string Name, string Query, string Glyph)> Defaults { get; } =
    [
        ("Unread", "is:unread", ""),
        ("With attachments", "has:attachment", ""),
        ("Recently received", "in:inbox newer_than:3d", ""),
        ("Large messages", "larger:5mb", ""),
    ];

    public async Task<IReadOnlyList<SmartFolder>> ListAsync(CancellationToken ct = default)
    {
        if (!settings.Current.SmartFoldersSeeded)
        {
            settings.Update(s => s.SmartFoldersSeeded = true);
            var position = 0;
            foreach (var (name, query, glyph) in Defaults)
            {
                await store.SaveAsync(new SmartFolder { Id = Guid.NewGuid(), Name = name, Query = query, Glyph = glyph, Position = position++, CreatedAt = clock.Now }, ct).ConfigureAwait(false);
            }
        }

        return (await store.ListAsync(ct).ConfigureAwait(false)).OrderBy(f => f.Position).ThenBy(f => f.CreatedAt).ToList();
    }

    public async Task<SmartFolder> SaveAsync(SmartFolder folder, CancellationToken ct = default)
    {
        var name = folder.Name.Trim();
        var query = folder.Query.Trim();
        if (name.Length == 0) throw new ArgumentException("Give the smart folder a name.");
        if (query.Length == 0) throw new ArgumentException("Add at least one condition.");

        var existing = await store.ListAsync(ct).ConfigureAwait(false);
        var saved = folder with
        {
            Name = name.Length > 60 ? name[..60] : name,
            Query = query,
            CreatedAt = folder.CreatedAt == default ? clock.Now : folder.CreatedAt,
            Position = existing.Any(f => f.Id == folder.Id) ? folder.Position : existing.Select(f => f.Position).DefaultIfEmpty(-1).Max() + 1,
        };
        await store.SaveAsync(saved, ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await store.DeleteAsync(id, ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Decides which messages a smart folder shows.</summary>
public static class SmartFolderFilter
{
    /// <summary>
    /// Messages matching the folder's query. Junk and Trash are left out unless the query names a folder with
    /// <c>in:</c>, and snoozed messages stay hidden until they return, as everywhere else.
    /// </summary>
    public static Func<Models.MessageSummary, bool> Build(SmartFolder folder, Services.SearchContext context, Func<Models.MessageSummary, bool> isLocallyJunk)
    {
        var query = Services.MessageSearch.Compile(folder.Query, context);
        var namesFolder = Services.MessageSearch.Parse(folder.Query).Any(t => t.Operator == "in");
        return m =>
        {
            if (m.IsSnoozed) return false;
            if (!namesFolder && (m.FolderKind is Models.FolderKind.Junk or Models.FolderKind.Trash || isLocallyJunk(m))) return false;
            return query(m);
        };
    }

    /// <summary>The terms whose text needs the local search index (so callers can look them up first).</summary>
    public static IReadOnlyList<string> IndexTerms(SmartFolder folder) => Services.MessageSearch.TextTerms(folder.Query);
}
