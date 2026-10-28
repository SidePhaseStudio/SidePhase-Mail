using Microsoft.Data.Sqlite;
using SidePhase.Mail.Core.Contacts;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Infrastructure.Storage;

/// <summary>The address book and recent recipients in the local SQLite database. Never uploaded anywhere.</summary>
public sealed class SqliteContactStore(MailCache cache) : IContactStore
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

    public async Task<IReadOnlyList<Contact>> ListAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        return await ReadContactsAsync(c, null, ct).ConfigureAwait(false);
    }

    public async Task<Contact?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        return (await ReadContactsAsync(c, id, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task SaveAsync(Contact contact, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO contacts (id, display_name, notes, source, created_at, updated_at)
                VALUES ($id, $name, $notes, $source, $created, $updated)
                ON CONFLICT(id) DO UPDATE SET display_name = $name, notes = $notes, source = $source, updated_at = $updated
                """;
            cmd.Parameters.AddWithValue("$id", contact.Id.ToString());
            cmd.Parameters.AddWithValue("$name", contact.DisplayName);
            cmd.Parameters.AddWithValue("$notes", (object?)contact.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source", contact.Source);
            cmd.Parameters.AddWithValue("$created", contact.CreatedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$updated", contact.UpdatedAt.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM contact_emails WHERE contact_id = $id";
            cmd.Parameters.AddWithValue("$id", contact.Id.ToString());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var position = 0;
        foreach (var email in contact.Emails)
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO contact_emails (contact_id, email, label, position) VALUES ($id, $email, $label, $pos)";
            cmd.Parameters.AddWithValue("$id", contact.Id.ToString());
            cmd.Parameters.AddWithValue("$email", email.Address);
            cmd.Parameters.AddWithValue("$label", (object?)email.Label ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pos", position++);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var sql in new[] { "DELETE FROM contact_emails WHERE contact_id = $id", "DELETE FROM contacts WHERE id = $id" })
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecentRecipient>> GetRecentAsync(int limit, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT email, display_name, last_used, use_count FROM recent_recipients ORDER BY last_used DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<RecentRecipient>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new RecentRecipient(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                reader.GetInt32(3)));
        }

        return result;
    }

    public async Task RecordRecipientsAsync(IEnumerable<EmailAddress> recipients, DateTimeOffset when, CancellationToken ct = default)
    {
        var list = recipients.Where(r => !string.IsNullOrWhiteSpace(r.Address)).DistinctBy(r => r.Address.Trim(), StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return;

        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var r in list)
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO recent_recipients (email, display_name, last_used, use_count) VALUES ($email, $name, $at, 1)
                ON CONFLICT(email) DO UPDATE SET
                    display_name = COALESCE($name, display_name),
                    last_used = MAX(last_used, $at),
                    use_count = use_count + 1
                """;
            cmd.Parameters.AddWithValue("$email", r.Address.Trim());
            cmd.Parameters.AddWithValue("$name", string.IsNullOrWhiteSpace(r.Name) ? DBNull.Value : r.Name.Trim());
            cmd.Parameters.AddWithValue("$at", when.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Keep the list bounded: the 2,000 most recent are plenty for autocomplete.
        await using (var trim = c.CreateCommand())
        {
            trim.Transaction = tx;
            trim.CommandText = "DELETE FROM recent_recipients WHERE email NOT IN (SELECT email FROM recent_recipients ORDER BY last_used DESC LIMIT 2000)";
            await trim.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task ForgetRecentAsync(string email, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM recent_recipients WHERE email = $email";
        cmd.Parameters.AddWithValue("$email", email.Trim());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Contact>> ReadContactsAsync(SqliteConnection c, Guid? id, CancellationToken ct)
    {
        var emails = new Dictionary<string, List<ContactEmail>>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT contact_id, email, label FROM contact_emails" + (id is null ? string.Empty : " WHERE contact_id = $id") + " ORDER BY contact_id, position";
            if (id is not null) cmd.Parameters.AddWithValue("$id", id.Value.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var key = reader.GetString(0);
                if (!emails.TryGetValue(key, out var list)) emails[key] = list = [];
                list.Add(new ContactEmail(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        var result = new List<Contact>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, display_name, notes, source, created_at, updated_at FROM contacts" + (id is null ? string.Empty : " WHERE id = $id");
            if (id is not null) cmd.Parameters.AddWithValue("$id", id.Value.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var key = reader.GetString(0);
                result.Add(new Contact
                {
                    Id = Guid.Parse(key),
                    DisplayName = reader.GetString(1),
                    Notes = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Source = reader.GetString(3),
                    CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                    UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                    Emails = emails.TryGetValue(key, out var list) ? list : [],
                });
            }
        }

        return result;
    }
}
