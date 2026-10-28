using System.Text.Json;
using Microsoft.Data.Sqlite;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Organisation;

namespace SidePhase.Mail.Infrastructure.Storage;

/// <summary>Mail rules and the record of which messages each rule has handled.</summary>
public sealed class SqliteMailRuleStore(MailCache cache) : IMailRuleStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private bool _ready;

    private sealed record Definition(bool MatchAll, Guid? AccountId, bool StopProcessing, IReadOnlyList<RuleCondition> Conditions, IReadOnlyList<RuleAction> Actions);

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        if (!_ready)
        {
            await cache.InitializeAsync(ct).ConfigureAwait(false);
            _ready = true;
        }

        return await cache.Database.OpenAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MailRule>> ListAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, enabled, position, definition_json, created_at, updated_at FROM mail_rules ORDER BY position, created_at";
        var result = new List<MailRule>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            Definition? definition;
            try
            {
                definition = JsonSerializer.Deserialize<Definition>(r.GetString(4), Json);
            }
            catch (JsonException)
            {
                definition = null; // a damaged rule is skipped rather than breaking every rule
            }

            if (definition is null) continue;
            result.Add(new MailRule
            {
                Id = Guid.Parse(r.GetString(0)),
                Name = r.GetString(1),
                Enabled = r.GetBoolean(2),
                Position = r.GetInt32(3),
                MatchAll = definition.MatchAll,
                AccountId = definition.AccountId,
                StopProcessing = definition.StopProcessing,
                Conditions = definition.Conditions ?? [],
                Actions = definition.Actions ?? [],
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)),
                UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)),
            });
        }

        return result;
    }

    public async Task SaveAsync(MailRule rule, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mail_rules (id, name, enabled, position, definition_json, created_at, updated_at)
            VALUES ($id, $name, $enabled, $pos, $def, $created, $updated)
            ON CONFLICT (id) DO UPDATE SET name = $name, enabled = $enabled, position = $pos, definition_json = $def, updated_at = $updated
            """;
        cmd.Parameters.AddWithValue("$id", rule.Id.ToString());
        cmd.Parameters.AddWithValue("$name", rule.Name);
        cmd.Parameters.AddWithValue("$enabled", rule.Enabled);
        cmd.Parameters.AddWithValue("$pos", rule.Position);
        cmd.Parameters.AddWithValue("$def", JsonSerializer.Serialize(new Definition(rule.MatchAll, rule.AccountId, rule.StopProcessing, rule.Conditions, rule.Actions), Json));
        cmd.Parameters.AddWithValue("$created", rule.CreatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$updated", rule.UpdatedAt.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM mail_rules WHERE id = $id; DELETE FROM rule_applications WHERE rule_id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MessageSummary>> ClaimAsync(Guid ruleId, IReadOnlyList<(MessageSummary Message, string IdentityKey)> messages, DateTimeOffset now, CancellationToken ct = default)
    {
        var fresh = new List<MessageSummary>();
        await using var c = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO rule_applications (rule_id, account_id, identity_key, applied_at) VALUES ($r, $a, $k, $t)";
        cmd.Parameters.AddWithValue("$r", ruleId.ToString());
        var pa = cmd.Parameters.Add("$a", SqliteType.Text);
        var pk = cmd.Parameters.Add("$k", SqliteType.Text);
        cmd.Parameters.AddWithValue("$t", now.ToUnixTimeMilliseconds());
        foreach (var (message, key) in messages)
        {
            pa.Value = message.AccountId.ToString();
            pk.Value = key;
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1) fresh.Add(message);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return fresh;
    }
}
