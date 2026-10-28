using System.Text.Json;
using Microsoft.Data.Sqlite;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Scheduling;

namespace SidePhase.Mail.Infrastructure.Storage;

/// <summary>The outbox in the local SQLite database. State changes are atomic compare-and-set updates.</summary>
public sealed class SqliteOutboxStore(MailCache cache) : IOutboxStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
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

    public async Task AddAsync(OutboxItem item, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO outbox (id, account_id, is_demo, kind, state, send_at_utc, draft_json, attempts, last_error, created_at, updated_at, accepted_at)
            VALUES ($id, $a, $demo, $kind, $state, $at, $draft, $attempts, $err, $created, $updated, $accepted)
            """;
        Bind(cmd, item);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<OutboxItem?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = Select + " WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        return (await ReadAsync(cmd, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<OutboxItem>> ListAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = Select + " ORDER BY send_at_utc";
        return await ReadAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<bool> TryTransitionAsync(Guid id, OutboxState expected, OutboxItem updated, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE outbox SET account_id = $a, is_demo = $demo, kind = $kind, state = $state, send_at_utc = $at, draft_json = $draft,
                attempts = $attempts, last_error = $err, updated_at = $updated, accepted_at = $accepted
            WHERE id = $id AND state = $expected
            """;
        Bind(cmd, updated);
        cmd.Parameters.AddWithValue("$expected", (int)expected);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task AddAttemptAsync(OutboxAttempt attempt, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO outbox_attempts (outbox_id, at_utc, outcome, detail) VALUES ($id, $at, $o, $d)";
        cmd.Parameters.AddWithValue("$id", attempt.OutboxId.ToString());
        cmd.Parameters.AddWithValue("$at", attempt.At.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$o", (int)attempt.Outcome);
        cmd.Parameters.AddWithValue("$d", attempt.Detail);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutboxAttempt>> GetAttemptsAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT at_utc, outcome, detail FROM outbox_attempts WHERE outbox_id = $id ORDER BY at_utc";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        var result = new List<OutboxAttempt>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new OutboxAttempt(id, DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)), (SendOutcome)r.GetInt32(1), r.IsDBNull(2) ? string.Empty : r.GetString(2)));
        }

        return result;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM outbox WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string Select = "SELECT id, account_id, is_demo, kind, state, send_at_utc, draft_json, attempts, last_error, created_at, updated_at, accepted_at FROM outbox";

    private static void Bind(SqliteCommand cmd, OutboxItem item)
    {
        cmd.Parameters.AddWithValue("$id", item.Id.ToString());
        cmd.Parameters.AddWithValue("$a", item.AccountId.ToString());
        cmd.Parameters.AddWithValue("$demo", item.IsDemo);
        cmd.Parameters.AddWithValue("$kind", (int)item.Kind);
        cmd.Parameters.AddWithValue("$state", (int)item.State);
        cmd.Parameters.AddWithValue("$at", item.SendAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$draft", JsonSerializer.Serialize(item.Draft, Json));
        cmd.Parameters.AddWithValue("$attempts", item.Attempts);
        cmd.Parameters.AddWithValue("$err", (object?)item.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$updated", item.UpdatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$accepted", item.AcceptedAt is { } a ? a.ToUnixTimeMilliseconds() : DBNull.Value);
    }

    private static async Task<IReadOnlyList<OutboxItem>> ReadAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var result = new List<OutboxItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new OutboxItem
            {
                Id = Guid.Parse(r.GetString(0)),
                AccountId = Guid.Parse(r.GetString(1)),
                IsDemo = r.GetBoolean(2),
                Kind = (OutboxKind)r.GetInt32(3),
                State = (OutboxState)r.GetInt32(4),
                SendAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)),
                Draft = JsonSerializer.Deserialize<DraftMessage>(r.GetString(6), Json) ?? new DraftMessage(),
                Attempts = r.GetInt32(7),
                LastError = r.IsDBNull(8) ? null : r.GetString(8),
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)),
                UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(10)),
                AcceptedAt = r.IsDBNull(11) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(11)),
            });
        }

        return result;
    }
}
