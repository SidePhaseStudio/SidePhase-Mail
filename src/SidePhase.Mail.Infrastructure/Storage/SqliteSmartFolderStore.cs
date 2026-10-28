using Microsoft.Data.Sqlite;
using SidePhase.Mail.Core.Organisation;

namespace SidePhase.Mail.Infrastructure.Storage;

/// <summary>Smart folder definitions in the local database.</summary>
public sealed class SqliteSmartFolderStore(MailCache cache) : ISmartFolderStore
{
    private bool _ready;

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        if (!_ready)
        {
            await cache.InitializeAsync(ct).ConfigureAwait(false);
            _ready = true;
        }

        return await cache.Database.OpenAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SmartFolder>> ListAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, query, glyph, position, created_at FROM smart_folders ORDER BY position, created_at";
        var result = new List<SmartFolder>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new SmartFolder
            {
                Id = Guid.Parse(r.GetString(0)),
                Name = r.GetString(1),
                Query = r.GetString(2),
                Glyph = r.IsDBNull(3) ? "" : r.GetString(3),
                Position = r.GetInt32(4),
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)),
            });
        }

        return result;
    }

    public async Task SaveAsync(SmartFolder folder, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO smart_folders (id, name, query, glyph, position, created_at) VALUES ($id, $name, $query, $glyph, $pos, $created)
            ON CONFLICT (id) DO UPDATE SET name = $name, query = $query, glyph = $glyph, position = $pos
            """;
        cmd.Parameters.AddWithValue("$id", folder.Id.ToString());
        cmd.Parameters.AddWithValue("$name", folder.Name);
        cmd.Parameters.AddWithValue("$query", folder.Query);
        cmd.Parameters.AddWithValue("$glyph", folder.Glyph);
        cmd.Parameters.AddWithValue("$pos", folder.Position);
        cmd.Parameters.AddWithValue("$created", folder.CreatedAt.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM smart_folders WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
