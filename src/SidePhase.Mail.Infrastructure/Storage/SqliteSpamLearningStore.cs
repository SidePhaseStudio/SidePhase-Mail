using System.Text.Json;
using Microsoft.Data.Sqlite;
using SidePhase.Mail.Core.Spam;

namespace SidePhase.Mail.Infrastructure.Storage;

/// <summary>The local spam filter's word counts and training examples, in the local database only.</summary>
public sealed class SqliteSpamLearningStore(MailCache cache) : ISpamLearningStore
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

    public async Task<SpamModel> LoadAsync(CancellationToken ct = default)
    {
        var model = new SpamModel();
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT token, spam_count, ham_count FROM spam_tokens";
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) model.Tokens[r.GetString(0)] = (r.GetInt32(1), r.GetInt32(2));
        }

        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(SUM(is_spam), 0), COALESCE(SUM(1 - is_spam), 0) FROM spam_training";
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                model.SpamMessages = r.GetInt32(0);
                model.HamMessages = r.GetInt32(1);
            }
        }

        return model;
    }

    public async Task<(bool IsSpam, IReadOnlyList<string> Tokens)?> GetTrainingAsync(Guid accountId, string identityKey, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT is_spam, tokens_json FROM spam_training WHERE account_id = $a AND identity_key = $k";
        cmd.Parameters.AddWithValue("$a", accountId.ToString());
        cmd.Parameters.AddWithValue("$k", identityKey);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        return (r.GetBoolean(0), JsonSerializer.Deserialize<List<string>>(r.GetString(1)) ?? []);
    }

    public async Task ApplyAsync(Guid accountId, string identityKey, bool isSpam, IReadOnlyList<string> tokens, (bool IsSpam, IReadOnlyList<string> Tokens)? previous, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (previous is { } old) await CountAsync(c, tx, old.Tokens, old.IsSpam, -1, ct).ConfigureAwait(false);
        await CountAsync(c, tx, tokens, isSpam, +1, ct).ConfigureAwait(false);

        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO spam_training (account_id, identity_key, is_spam, tokens_json, trained_at) VALUES ($a, $k, $s, $t, $at)
                ON CONFLICT (account_id, identity_key) DO UPDATE SET is_spam = $s, tokens_json = $t, trained_at = $at
                """;
            cmd.Parameters.AddWithValue("$a", accountId.ToString());
            cmd.Parameters.AddWithValue("$k", identityKey);
            cmd.Parameters.AddWithValue("$s", isSpam);
            cmd.Parameters.AddWithValue("$t", JsonSerializer.Serialize(tokens));
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task ResetAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM spam_tokens; DELETE FROM spam_training;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task CountAsync(SqliteConnection c, SqliteTransaction tx, IReadOnlyList<string> tokens, bool isSpam, int delta, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        var column = isSpam ? "spam_count" : "ham_count";
        cmd.CommandText = $"""
            INSERT INTO spam_tokens (token, {column}) VALUES ($t, MAX($d, 0))
            ON CONFLICT (token) DO UPDATE SET {column} = MAX({column} + $d, 0)
            """;
        var pt = cmd.Parameters.Add("$t", SqliteType.Text);
        cmd.Parameters.AddWithValue("$d", delta);
        foreach (var token in tokens)
        {
            pt.Value = token;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var prune = c.CreateCommand();
        prune.Transaction = tx;
        prune.CommandText = "DELETE FROM spam_tokens WHERE spam_count = 0 AND ham_count = 0";
        await prune.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
