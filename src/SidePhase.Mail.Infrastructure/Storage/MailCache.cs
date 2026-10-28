using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Spam;

namespace SidePhase.Mail.Infrastructure.Storage;

/// <summary>Typed access to the SQLite cache. Holds no secrets: passwords and tokens live in the secret store.</summary>
public sealed class MailCache(MailDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public MailDatabase Database => database;

    public Task<int> InitializeAsync(CancellationToken cancellationToken = default) => database.MigrateAsync(cancellationToken);

    // ------------------------------------------------------------------ accounts

    public async Task<IReadOnlyList<StoredAccount>> GetAccountsAsync(CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, display_name, email, provider, color, auth_method, username, imap_host, imap_port, imap_security, smtp_host, smtp_port, smtp_security FROM accounts ORDER BY sort_order, created_at";
        var result = new List<StoredAccount>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var account = new MailAccount
            {
                Id = Guid.Parse(r.GetString(0)),
                DisplayName = r.GetString(1),
                EmailAddress = r.GetString(2),
                Provider = (MailProviderKind)r.GetInt32(3),
                ColorHex = r.IsDBNull(4) ? null : r.GetString(4),
            };
            var settings = new AccountConnectionSettings
            {
                AuthMethod = (AuthMethod)r.GetInt32(5),
                Username = r.GetString(6),
                Imap = new ServerEndpoint(r.GetString(7), r.GetInt32(8), (ConnectionSecurity)r.GetInt32(9)),
                Smtp = new ServerEndpoint(r.GetString(10), r.GetInt32(11), (ConnectionSecurity)r.GetInt32(12)),
            };
            result.Add(new StoredAccount(account, settings));
        }

        return result;
    }

    public async Task AddAccountAsync(StoredAccount stored, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO accounts (id, display_name, email, provider, color, auth_method, username,
                imap_host, imap_port, imap_security, smtp_host, smtp_port, smtp_security, sort_order, created_at)
            VALUES ($id, $name, $email, $provider, $color, $auth, $user, $ih, $ip, $is, $sh, $sp, $ss,
                (SELECT COALESCE(MAX(sort_order) + 1, 0) FROM accounts), $created)
            """;
        var a = stored.Account;
        var s = stored.Settings;
        cmd.Parameters.AddWithValue("$id", a.Id.ToString());
        cmd.Parameters.AddWithValue("$name", a.DisplayName);
        cmd.Parameters.AddWithValue("$email", a.EmailAddress);
        cmd.Parameters.AddWithValue("$provider", (int)a.Provider);
        cmd.Parameters.AddWithValue("$color", (object?)a.ColorHex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$auth", (int)s.AuthMethod);
        cmd.Parameters.AddWithValue("$user", s.Username);
        cmd.Parameters.AddWithValue("$ih", s.Imap.Host);
        cmd.Parameters.AddWithValue("$ip", s.Imap.Port);
        cmd.Parameters.AddWithValue("$is", (int)s.Imap.Security);
        cmd.Parameters.AddWithValue("$sh", s.Smtp.Host);
        cmd.Parameters.AddWithValue("$sp", s.Smtp.Port);
        cmd.Parameters.AddWithValue("$ss", (int)s.Smtp.Security);
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Renames or recolours an account. Only these two columns change: connection settings and saved sign-ins are untouched.</summary>
    public async Task UpdateAccountAppearanceAsync(Guid accountId, string displayName, string? colorHex, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE accounts SET display_name = $name, color = $color WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", accountId.ToString());
        cmd.Parameters.AddWithValue("$name", displayName);
        cmd.Parameters.AddWithValue("$color", (object?)colorHex ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Deletes the account and, through cascades, its folders, messages, bodies, pins and drafts.</summary>
    /// <summary>
    /// Removes the account and everything cached for it (folders, messages, bodies, pins, snoozes, categories and other
    /// cascaded rows), plus the rule history and local spam-training records that aren't linked by a foreign key.
    /// Outbox items are kept, so a message queued for this account isn't lost: it shows as failed and can be copied.
    /// </summary>
    public async Task DeleteAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM rule_applications WHERE account_id = $id;
                DELETE FROM spam_training WHERE account_id = $id;
                DELETE FROM accounts WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", accountId.ToString());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ folders

    public async Task<IReadOnlyList<StoredFolder>> GetFoldersAsync(Guid? accountId = null, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, account_id, path, name, kind, uid_validity, highest_uid, highest_modseq, last_sync, sort_order, sync FROM folders"
                          + (accountId is null ? string.Empty : " WHERE account_id = $a")
                          + " ORDER BY sort_order, name";
        if (accountId is { } id) cmd.Parameters.AddWithValue("$a", id.ToString());
        var result = new List<StoredFolder>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new StoredFolder
            {
                Id = Guid.Parse(r.GetString(0)),
                AccountId = Guid.Parse(r.GetString(1)),
                Path = r.GetString(2),
                Name = r.GetString(3),
                Kind = (FolderKind)r.GetInt32(4),
                UidValidity = (uint)r.GetInt64(5),
                HighestUid = (uint)r.GetInt64(6),
                HighestModSeq = (ulong)r.GetInt64(7),
                LastSync = r.IsDBNull(8) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(8)),
                SortOrder = r.GetInt32(9),
                Synchronize = r.GetBoolean(10),
            });
        }

        return result;
    }

    /// <summary>Inserts a folder or updates its name, kind and order, keeping its id and sync state.</summary>
    public async Task UpsertFolderAsync(StoredFolder folder, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO folders (id, account_id, path, name, kind, sort_order, sync)
            VALUES ($id, $a, $path, $name, $kind, $order, $sync)
            ON CONFLICT (account_id, path) DO UPDATE SET name = excluded.name, kind = excluded.kind, sort_order = excluded.sort_order, sync = excluded.sync
            """;
        cmd.Parameters.AddWithValue("$id", folder.Id.ToString());
        cmd.Parameters.AddWithValue("$a", folder.AccountId.ToString());
        cmd.Parameters.AddWithValue("$path", folder.Path);
        cmd.Parameters.AddWithValue("$name", folder.Name);
        cmd.Parameters.AddWithValue("$kind", (int)folder.Kind);
        cmd.Parameters.AddWithValue("$order", folder.SortOrder);
        cmd.Parameters.AddWithValue("$sync", folder.Synchronize);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteFolderAsync(Guid folderId, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM folders WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", folderId.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateFolderSyncStateAsync(Guid folderId, uint uidValidity, uint highestUid, ulong highestModSeq, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE folders SET uid_validity = $v, highest_uid = $u, highest_modseq = $m, last_sync = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$v", (long)uidValidity);
        cmd.Parameters.AddWithValue("$u", (long)highestUid);
        cmd.Parameters.AddWithValue("$m", (long)highestModSeq);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$id", folderId.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The server renumbered the folder (UIDVALIDITY changed): drop its cached messages and sync state.
    /// Pins and local stars are keyed by message identity, so they reattach when the messages are downloaded again.
    /// </summary>
    public async Task ResetFolderAsync(Guid folderId, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM messages WHERE folder_id = $id; UPDATE folders SET uid_validity = 0, highest_uid = 0, highest_modseq = 0 WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", folderId.ToString());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ messages

    private const string SummarySelect = """
        SELECT m.id, m.account_id, m.folder_id, f.kind, m.message_id_header, m.from_name, m.from_address, m.to_json, m.cc_json,
               m.subject, m.preview, m.date, m.is_read, m.is_starred, m.has_attachments, m.spam_json,
               p.pinned_at, CASE WHEN ls.identity_key IS NULL THEN 0 ELSE 1 END,
               sz.until_utc, rm.due_utc, m.in_reply_to, m.references_json, m.size,
               (SELECT group_concat(mc.category, char(31)) FROM message_categories mc WHERE mc.account_id = m.account_id AND mc.identity_key = m.identity_key)
        FROM messages m
        JOIN folders f ON f.id = m.folder_id
        LEFT JOIN pins p ON p.account_id = m.account_id AND p.identity_key = m.identity_key
        LEFT JOIN local_stars ls ON ls.account_id = m.account_id AND ls.identity_key = m.identity_key
        LEFT JOIN snoozes sz ON sz.account_id = m.account_id AND sz.identity_key = m.identity_key
        LEFT JOIN reminders rm ON rm.account_id = m.account_id AND rm.identity_key = m.identity_key
        """;

    public async Task<IReadOnlyList<MessageSummary>> GetSummariesAsync(CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        var attachments = await ReadAttachmentDetailsAsync(c, null, ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = SummarySelect + " ORDER BY m.date DESC";
        return await ReadSummariesAsync(cmd, attachments, ct).ConfigureAwait(false);
    }

    public async Task<MessageSummary?> GetSummaryAsync(Guid messageId, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        var attachments = await ReadAttachmentDetailsAsync(c, messageId, ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = SummarySelect + " WHERE m.id = $id";
        cmd.Parameters.AddWithValue("$id", messageId.ToString());
        return (await ReadSummariesAsync(cmd, attachments, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    private static async Task<Dictionary<string, List<AttachmentInfo>>> ReadAttachmentDetailsAsync(SqliteConnection c, Guid? messageId, CancellationToken ct)
    {
        var result = new Dictionary<string, List<AttachmentInfo>>();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT message_id, file_name, content_type, size FROM message_attachments"
            + (messageId is null ? string.Empty : " WHERE message_id = $id") + " ORDER BY message_id, position";
        if (messageId is { } id) cmd.Parameters.AddWithValue("$id", id.ToString());
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = r.GetString(0);
            if (!result.TryGetValue(key, out var list)) result[key] = list = [];
            list.Add(new AttachmentInfo(r.GetString(1), r.GetString(2), r.GetInt64(3)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<MessageSummary>> ReadSummariesAsync(SqliteCommand cmd, Dictionary<string, List<AttachmentInfo>> attachments, CancellationToken ct)
    {
        var result = new List<MessageSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var pinnedAt = r.IsDBNull(16) ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(16));
            result.Add(new MessageSummary
            {
                Id = Guid.Parse(r.GetString(0)),
                AccountId = Guid.Parse(r.GetString(1)),
                FolderId = Guid.Parse(r.GetString(2)),
                FolderKind = (FolderKind)r.GetInt32(3),
                MessageIdHeader = r.IsDBNull(4) ? null : r.GetString(4),
                From = new EmailAddress(r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6)),
                To = ReadAddresses(r.GetString(7)),
                Cc = ReadAddresses(r.GetString(8)),
                Subject = r.GetString(9),
                Preview = r.GetString(10),
                Date = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(11)),
                IsRead = r.GetBoolean(12),
                IsStarred = r.GetBoolean(13) || r.GetInt32(17) == 1,
                HasAttachments = r.GetBoolean(14),
                Spam = r.IsDBNull(15) ? SpamReport.None : JsonSerializer.Deserialize<SpamReport>(r.GetString(15), Json) ?? SpamReport.None,
                IsPinned = pinnedAt is not null,
                PinnedAt = pinnedAt,
                SnoozedUntil = r.IsDBNull(18) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(18)),
                FollowUpAt = r.IsDBNull(19) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(19)),
                References = ReadReferences(r.IsDBNull(20) ? null : r.GetString(20), r.IsDBNull(21) ? null : r.GetString(21)),
                Size = r.GetInt64(22),
                Attachments = attachments.TryGetValue(r.GetString(0), out var list) ? list : [],
                Categories = r.IsDBNull(23) ? [] : r.GetString(23).Split('\u001f', StringSplitOptions.RemoveEmptyEntries),
            });
        }

        return result;
    }

    private static IReadOnlyList<string> ReadReferences(string? inReplyTo, string? referencesJson)
    {
        var refs = referencesJson is null ? [] : JsonSerializer.Deserialize<List<string>>(referencesJson, Json) ?? [];
        if (!string.IsNullOrWhiteSpace(inReplyTo) && !refs.Contains(inReplyTo)) refs.Add(inReplyTo);
        return refs;
    }

    public async Task<StoredMessageRef?> GetMessageRefAsync(Guid messageId, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.account_id, m.folder_id, f.path, f.kind, m.uid, m.uid_validity, m.identity_key, m.message_id_header, m.is_read, m.is_starred
            FROM messages m JOIN folders f ON f.id = m.folder_id WHERE m.id = $id
            """;
        cmd.Parameters.AddWithValue("$id", messageId.ToString());
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new StoredMessageRef(
            Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), r.GetString(3),
            (FolderKind)r.GetInt32(4), (uint)r.GetInt64(5), (uint)r.GetInt64(6), r.GetString(7),
            r.IsDBNull(8) ? null : r.GetString(8), r.GetBoolean(9), r.GetBoolean(10));
    }

    /// <summary>All cached copies of a message (any folder) by identity.</summary>
    public async Task<IReadOnlyList<Guid>> FindAllByIdentityAsync(Guid accountId, string identityKey, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM messages WHERE account_id = $a AND identity_key = $k";
        cmd.Parameters.AddWithValue("$a", accountId.ToString());
        cmd.Parameters.AddWithValue("$k", identityKey);
        var result = new List<Guid>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) result.Add(Guid.Parse(r.GetString(0)));
        return result;
    }

    /// <summary>Finds a message in a folder by its identity (used after moves when the server didn't report the new UID).</summary>
    public async Task<Guid?> FindByIdentityAsync(Guid folderId, string identityKey, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM messages WHERE folder_id = $f AND identity_key = $k ORDER BY uid DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$f", folderId.ToString());
        cmd.Parameters.AddWithValue("$k", identityKey);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is string s ? Guid.Parse(s) : null;
    }

    public async Task<IReadOnlyList<uint>> GetUidsAsync(Guid folderId, uint uidValidity, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT uid FROM messages WHERE folder_id = $f AND uid_validity = $v ORDER BY uid";
        cmd.Parameters.AddWithValue("$f", folderId.ToString());
        cmd.Parameters.AddWithValue("$v", (long)uidValidity);
        var result = new List<uint>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) result.Add((uint)r.GetInt64(0));
        return result;
    }

    /// <summary>Inserts new messages or refreshes flags of known ones. Message ids are deterministic, so re-syncing never duplicates.</summary>
    public async Task UpsertMessagesAsync(IEnumerable<StoredMessage> messages, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO messages (id, account_id, folder_id, uid, uid_validity, message_id_header, identity_key, from_name, from_address,
                to_json, cc_json, subject, preview, date, is_read, is_starred, has_attachments, spam_json, size, in_reply_to, references_json)
            VALUES ($id, $a, $f, $uid, $uv, $mid, $key, $fn, $fa, $to, $cc, $subject, $preview, $date, $read, $star, $att, $spam, $size, $irt, $refs)
            ON CONFLICT (id) DO UPDATE SET is_read = excluded.is_read, is_starred = excluded.is_starred,
                preview = CASE WHEN excluded.preview <> '' THEN excluded.preview ELSE messages.preview END
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var name in new[] { "$id", "$a", "$f", "$uid", "$uv", "$mid", "$key", "$fn", "$fa", "$to", "$cc", "$subject", "$preview", "$date", "$read", "$star", "$att", "$spam", "$size", "$irt", "$refs" })
        {
            p[name] = cmd.Parameters.Add(name, SqliteType.Text);
        }

        foreach (var m in messages)
        {
            p["$id"].Value = m.Id.ToString();
            p["$a"].Value = m.AccountId.ToString();
            p["$f"].Value = m.FolderId.ToString();
            p["$uid"].Value = (long)m.Uid;
            p["$uv"].Value = (long)m.UidValidity;
            p["$mid"].Value = (object?)m.MessageIdHeader ?? DBNull.Value;
            p["$key"].Value = m.IdentityKey;
            p["$fn"].Value = (object?)m.From.Name ?? DBNull.Value;
            p["$fa"].Value = m.From.Address;
            p["$to"].Value = JsonSerializer.Serialize(m.To, Json);
            p["$cc"].Value = JsonSerializer.Serialize(m.Cc, Json);
            p["$subject"].Value = m.Subject;
            p["$preview"].Value = m.Preview;
            p["$date"].Value = m.Date.ToUnixTimeMilliseconds();
            p["$read"].Value = m.IsRead;
            p["$star"].Value = m.IsStarred;
            p["$att"].Value = m.HasAttachments;
            p["$spam"].Value = m.Spam.HasHeaders ? JsonSerializer.Serialize(m.Spam, Json) : DBNull.Value;
            p["$size"].Value = m.Size;
            p["$irt"].Value = (object?)m.InReplyTo ?? DBNull.Value;
            p["$refs"].Value = m.References.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(m.References, Json);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (m.Attachments.Count > 0) await InsertAttachmentsAsync(c, tx, m.Id, m.Attachments, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Records attachment names/types/sizes once per message (later calls for the same message are ignored).</summary>
    private static async Task InsertAttachmentsAsync(SqliteConnection c, SqliteTransaction? tx, Guid messageId, IReadOnlyList<AttachmentInfo> attachments, CancellationToken ct)
    {
        await using (var check = c.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT 1 FROM message_attachments WHERE message_id = $id LIMIT 1";
            check.Parameters.AddWithValue("$id", messageId.ToString());
            if (await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null) return;
        }

        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO message_attachments (message_id, position, file_name, content_type, size) VALUES ($id, $pos, $name, $type, $size)";
        var pid = cmd.Parameters.Add("$id", SqliteType.Text);
        var ppos = cmd.Parameters.Add("$pos", SqliteType.Integer);
        var pname = cmd.Parameters.Add("$name", SqliteType.Text);
        var ptype = cmd.Parameters.Add("$type", SqliteType.Text);
        var psize = cmd.Parameters.Add("$size", SqliteType.Integer);
        for (var i = 0; i < attachments.Count; i++)
        {
            pid.Value = messageId.ToString();
            ppos.Value = i;
            pname.Value = attachments[i].FileName;
            ptype.Value = attachments[i].ContentType;
            psize.Value = attachments[i].SizeBytes;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Stores attachment details fetched later for already-cached messages.</summary>
    public async Task SaveAttachmentDetailsAsync(Guid messageId, IReadOnlyList<AttachmentInfo> attachments, CancellationToken ct = default)
    {
        if (attachments.Count == 0) return;
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await InsertAttachmentsAsync(c, null, messageId, attachments, ct).ConfigureAwait(false);
    }

    /// <summary>UIDs in a folder whose messages have attachments but no recorded details yet (older cached mail).</summary>
    public async Task<IReadOnlyList<(Guid Id, uint Uid)>> GetMessagesMissingAttachmentDetailsAsync(Guid folderId, uint uidValidity, int limit, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.uid FROM messages m
            WHERE m.folder_id = $f AND m.uid_validity = $v AND m.has_attachments = 1
              AND NOT EXISTS (SELECT 1 FROM message_attachments a WHERE a.message_id = m.id)
            ORDER BY m.uid DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$f", folderId.ToString());
        cmd.Parameters.AddWithValue("$v", (long)uidValidity);
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<(Guid, uint)>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) result.Add((Guid.Parse(r.GetString(0)), (uint)r.GetInt64(1)));
        return result;
    }

    /// <summary>
    /// Runs an FTS5 query over the local search index (subject, sender, recipients, downloaded text, attachment
    /// names). Only messages cached on this PC are indexed.
    /// </summary>
    public async Task<IReadOnlySet<Guid>> SearchIndexAsync(string ftsQuery, int limit = 20000, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT fm.message_id FROM message_fts f JOIN fts_map fm ON fm.fts_rowid = f.rowid
            WHERE message_fts MATCH $q LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$q", ftsQuery);
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = new HashSet<Guid>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) result.Add(Guid.Parse(r.GetString(0)));
        return result;
    }

    /// <summary>Updates read/starred flags reported by the server for already-cached UIDs.</summary>
    public async Task<int> UpdateFlagsAsync(Guid folderId, uint uidValidity, IEnumerable<(uint Uid, bool IsRead, bool IsStarred)> flags, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE messages SET is_read = $r, is_starred = $s WHERE folder_id = $f AND uid_validity = $v AND uid = $u AND (is_read <> $r OR is_starred <> $s)";
        var pr = cmd.Parameters.Add("$r", SqliteType.Integer);
        var ps = cmd.Parameters.Add("$s", SqliteType.Integer);
        cmd.Parameters.AddWithValue("$f", folderId.ToString());
        cmd.Parameters.AddWithValue("$v", (long)uidValidity);
        var pu = cmd.Parameters.Add("$u", SqliteType.Integer);
        var changed = 0;
        foreach (var (uid, read, starred) in flags)
        {
            pr.Value = read;
            ps.Value = starred;
            pu.Value = (long)uid;
            changed += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return changed;
    }

    public async Task<int> DeleteMessagesAsync(Guid folderId, uint uidValidity, IEnumerable<uint> uids, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM messages WHERE folder_id = $f AND uid_validity = $v AND uid = $u";
        cmd.Parameters.AddWithValue("$f", folderId.ToString());
        cmd.Parameters.AddWithValue("$v", (long)uidValidity);
        var pu = cmd.Parameters.Add("$u", SqliteType.Integer);
        var removed = 0;
        foreach (var uid in uids)
        {
            pu.Value = (long)uid;
            removed += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return removed;
    }

    /// <summary>
    /// Re-homes a cached message after a server-side move: the row (and its cached body) gets the id derived from the
    /// destination folder and new UID, so the list updates immediately instead of waiting for the next sync.
    /// </summary>
    public async Task MoveCachedMessageAsync(Guid oldId, Guid newId, Guid folderId, uint uidValidity, uint uid, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO messages (id, account_id, folder_id, uid, uid_validity, message_id_header, identity_key, from_name, from_address,
                    to_json, cc_json, subject, preview, date, is_read, is_starred, has_attachments, spam_json, size, in_reply_to, references_json)
                SELECT $new, account_id, $f, $uid, $uv, message_id_header, identity_key, from_name, from_address,
                    to_json, cc_json, subject, preview, date, is_read, is_starred, has_attachments, spam_json, size, in_reply_to, references_json
                FROM messages WHERE id = $old;
                INSERT OR IGNORE INTO bodies (message_id, html, text, attachments_json, bcc_json, cached_at)
                SELECT $new, html, text, attachments_json, bcc_json, cached_at FROM bodies WHERE message_id = $old;
                INSERT OR IGNORE INTO message_attachments (message_id, position, file_name, content_type, size)
                SELECT $new, position, file_name, content_type, size FROM message_attachments WHERE message_id = $old;
                DELETE FROM messages WHERE id = $old;
                """;
            cmd.Parameters.AddWithValue("$new", newId.ToString());
            cmd.Parameters.AddWithValue("$old", oldId.ToString());
            cmd.Parameters.AddWithValue("$f", folderId.ToString());
            cmd.Parameters.AddWithValue("$uid", (long)uid);
            cmd.Parameters.AddWithValue("$uv", (long)uidValidity);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Message-ID and References of a cached message, for threading replies.</summary>
    public async Task<(string? MessageId, IReadOnlyList<string> References)> GetThreadHeadersAsync(Guid messageId, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT message_id_header, references_json FROM messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", messageId.ToString());
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return (null, []);
        var refs = r.IsDBNull(1) ? [] : JsonSerializer.Deserialize<List<string>>(r.GetString(1), Json) ?? [];
        return (r.IsDBNull(0) ? null : r.GetString(0), refs);
    }

    public async Task DeleteMessageAsync(Guid messageId, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", messageId.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Replaces a message's spam assessment (e.g. once the full headers have been downloaded).</summary>
    public async Task UpdateSpamAsync(Guid messageId, SpamReport spam, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE messages SET spam_json = $s WHERE id = $id";
        cmd.Parameters.AddWithValue("$s", spam.HasHeaders ? JsonSerializer.Serialize(spam, Json) : DBNull.Value);
        cmd.Parameters.AddWithValue("$id", messageId.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SetFlagsAsync(Guid messageId, bool? isRead, bool? isStarred, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE messages SET is_read = COALESCE($r, is_read), is_starred = COALESCE($s, is_starred) WHERE id = $id";
        cmd.Parameters.AddWithValue("$r", isRead is null ? DBNull.Value : isRead.Value);
        cmd.Parameters.AddWithValue("$s", isStarred is null ? DBNull.Value : isStarred.Value);
        cmd.Parameters.AddWithValue("$id", messageId.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ bodies

    public async Task<MessageBody?> GetBodyAsync(Guid messageId, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT html, text, attachments_json, bcc_json FROM bodies WHERE message_id = $id";
        cmd.Parameters.AddWithValue("$id", messageId.ToString());
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new MessageBody
        {
            MessageId = messageId,
            HtmlBody = r.IsDBNull(0) ? null : r.GetString(0),
            TextBody = r.IsDBNull(1) ? null : r.GetString(1),
            Attachments = JsonSerializer.Deserialize<List<AttachmentInfo>>(r.GetString(2), Json) ?? [],
            Bcc = r.IsDBNull(3) ? [] : ReadAddresses(r.GetString(3)),
        };
    }

    public async Task SaveBodyAsync(MessageBody body, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO bodies (message_id, html, text, attachments_json, bcc_json, cached_at) VALUES ($id, $h, $t, $a, $b, $c)
            ON CONFLICT (message_id) DO UPDATE SET html = excluded.html, text = excluded.text, attachments_json = excluded.attachments_json,
                bcc_json = excluded.bcc_json, cached_at = excluded.cached_at
            """;
        cmd.Parameters.AddWithValue("$id", body.MessageId.ToString());
        cmd.Parameters.AddWithValue("$h", (object?)body.HtmlBody ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", (object?)body.TextBody ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", JsonSerializer.Serialize(body.Attachments, Json));
        cmd.Parameters.AddWithValue("$b", body.Bcc.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(body.Bcc, Json));
        cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (body.Attachments.Count > 0) await InsertAttachmentsAsync(c, null, body.MessageId, body.Attachments, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ categories

    public async Task SetCategoryAsync(Guid accountId, string identityKey, string category, bool add, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = add
            ? "INSERT OR IGNORE INTO message_categories (account_id, identity_key, category) VALUES ($a, $k, $c)"
            : "DELETE FROM message_categories WHERE account_id = $a AND identity_key = $k AND category = $c";
        cmd.Parameters.AddWithValue("$a", accountId.ToString());
        cmd.Parameters.AddWithValue("$k", identityKey);
        cmd.Parameters.AddWithValue("$c", category);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ pins & local stars

    public async Task SetPinnedAsync(Guid accountId, string identityKey, DateTimeOffset? pinnedAt, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        if (pinnedAt is { } at)
        {
            cmd.CommandText = "INSERT INTO pins (account_id, identity_key, pinned_at) VALUES ($a, $k, $t) ON CONFLICT DO UPDATE SET pinned_at = excluded.pinned_at";
            cmd.Parameters.AddWithValue("$t", at.ToUnixTimeMilliseconds());
        }
        else
        {
            cmd.CommandText = "DELETE FROM pins WHERE account_id = $a AND identity_key = $k";
        }

        cmd.Parameters.AddWithValue("$a", accountId.ToString());
        cmd.Parameters.AddWithValue("$k", identityKey);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SetLocalStarAsync(Guid accountId, string identityKey, bool starred, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = starred
            ? "INSERT OR IGNORE INTO local_stars (account_id, identity_key) VALUES ($a, $k)"
            : "DELETE FROM local_stars WHERE account_id = $a AND identity_key = $k";
        cmd.Parameters.AddWithValue("$a", accountId.ToString());
        cmd.Parameters.AddWithValue("$k", identityKey);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ snooze & follow-up (keyed by identity, like pins)

    public async Task SetSnoozeAsync(Guid accountId, string identityKey, DateTimeOffset? until, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        if (until is { } at)
        {
            cmd.CommandText = "INSERT INTO snoozes (account_id, identity_key, until_utc, created_at) VALUES ($a, $k, $u, $n) ON CONFLICT DO UPDATE SET until_utc = excluded.until_utc";
            cmd.Parameters.AddWithValue("$u", at.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$n", now.ToUnixTimeMilliseconds());
        }
        else
        {
            cmd.CommandText = "DELETE FROM snoozes WHERE account_id = $a AND identity_key = $k";
        }

        cmd.Parameters.AddWithValue("$a", accountId.ToString());
        cmd.Parameters.AddWithValue("$k", identityKey);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Ends snoozes that have expired and returns the (account, identity) pairs that came back.</summary>
    public async Task<IReadOnlyList<(Guid AccountId, string IdentityKey)>> ReleaseExpiredSnoozesAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var released = new List<(Guid, string)>();
        await using (var select = c.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = "SELECT account_id, identity_key FROM snoozes WHERE until_utc <= $now";
            select.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            await using var r = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) released.Add((Guid.Parse(r.GetString(0)), r.GetString(1)));
        }

        await using (var delete = c.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM snoozes WHERE until_utc <= $now";
            delete.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return released;
    }

    public async Task SetFollowUpAsync(Guid accountId, string identityKey, DateTimeOffset? due, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        if (due is { } at)
        {
            // A new time also re-arms the notification.
            cmd.CommandText = "INSERT INTO reminders (account_id, identity_key, due_utc, created_at) VALUES ($a, $k, $d, $n) ON CONFLICT DO UPDATE SET due_utc = excluded.due_utc, notified_at = NULL";
            cmd.Parameters.AddWithValue("$d", at.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$n", now.ToUnixTimeMilliseconds());
        }
        else
        {
            cmd.CommandText = "DELETE FROM reminders WHERE account_id = $a AND identity_key = $k";
        }

        cmd.Parameters.AddWithValue("$a", accountId.ToString());
        cmd.Parameters.AddWithValue("$k", identityKey);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Due reminders that haven't been announced yet, marked as announced in the same transaction so a reminder is
    /// never notified twice (even across restarts). Returns one cached message id per reminder where available.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> TakeDueRemindersAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var ids = new List<Guid>();
        await using (var select = c.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = """
                SELECT (SELECT m.id FROM messages m WHERE m.account_id = r.account_id AND m.identity_key = r.identity_key ORDER BY m.date DESC LIMIT 1)
                FROM reminders r WHERE r.due_utc <= $now AND r.notified_at IS NULL
                """;
            select.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            await using var r = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!r.IsDBNull(0)) ids.Add(Guid.Parse(r.GetString(0)));
            }
        }

        await using (var update = c.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE reminders SET notified_at = $now WHERE due_utc <= $now AND notified_at IS NULL";
            update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return ids;
    }

    // ------------------------------------------------------------------ drafts

    public async Task SaveDraftAsync(Guid draftId, DraftMessage draft, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO drafts (id, account_id, json, updated_at) VALUES ($id, $a, $j, $t)
            ON CONFLICT (id) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$id", draftId.ToString());
        cmd.Parameters.AddWithValue("$a", draft.AccountId.ToString());
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(draft with { DraftId = draftId }, Json));
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<DraftMessage?> GetDraftAsync(Guid draftId, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM drafts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", draftId.ToString());
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string json
            ? JsonSerializer.Deserialize<DraftMessage>(json, Json)
            : null;
    }

    public async Task DeleteDraftAsync(Guid draftId, CancellationToken ct = default)
    {
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM drafts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", draftId.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes every cached account, folder and message (the server is never touched).</summary>
    public async Task ClearMessagesAsync(CancellationToken ct = default)
    {
        // One transaction: an interruption must never leave empty folders that still look fully synced (sync would then
        // only fetch newer mail, and older messages would never come back).
        await using var c = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM bodies; DELETE FROM messages; UPDATE folders SET uid_validity = 0, highest_uid = 0, highest_modseq = 0, last_sync = NULL;";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    internal static IReadOnlyList<EmailAddress> ReadAddresses(string json) =>
        JsonSerializer.Deserialize<List<EmailAddress>>(json, Json) ?? [];

    internal static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
