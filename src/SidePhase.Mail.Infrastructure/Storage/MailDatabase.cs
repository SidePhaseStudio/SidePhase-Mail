using Microsoft.Data.Sqlite;

namespace SidePhase.Mail.Infrastructure.Storage;

/// <summary>
/// Opens the SQLite cache and applies schema migrations. The schema version lives in <c>PRAGMA user_version</c>;
/// each migration runs once, inside a transaction, in order.
/// </summary>
public sealed class MailDatabase
{
    /// <summary>Ordered migrations. Never edit a shipped migration; append a new one instead.</summary>
    internal static readonly string[] Migrations =
    [
        // 1 — initial schema
        """
        CREATE TABLE accounts (
            id            TEXT PRIMARY KEY,
            display_name  TEXT NOT NULL,
            email         TEXT NOT NULL,
            provider      INTEGER NOT NULL,
            color         TEXT,
            auth_method   INTEGER NOT NULL,
            username      TEXT NOT NULL,
            imap_host     TEXT NOT NULL,
            imap_port     INTEGER NOT NULL,
            imap_security INTEGER NOT NULL,
            smtp_host     TEXT NOT NULL,
            smtp_port     INTEGER NOT NULL,
            smtp_security INTEGER NOT NULL,
            sort_order    INTEGER NOT NULL DEFAULT 0,
            created_at    INTEGER NOT NULL
        );

        CREATE TABLE folders (
            id             TEXT PRIMARY KEY,
            account_id     TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            path           TEXT NOT NULL,
            name           TEXT NOT NULL,
            kind           INTEGER NOT NULL,
            uid_validity   INTEGER NOT NULL DEFAULT 0,
            highest_uid    INTEGER NOT NULL DEFAULT 0,
            highest_modseq INTEGER NOT NULL DEFAULT 0,
            last_sync      INTEGER,
            sort_order     INTEGER NOT NULL DEFAULT 0,
            sync           INTEGER NOT NULL DEFAULT 1,
            UNIQUE (account_id, path)
        );

        CREATE TABLE messages (
            id                TEXT PRIMARY KEY,
            account_id        TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            folder_id         TEXT NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
            uid               INTEGER NOT NULL,
            uid_validity      INTEGER NOT NULL,
            message_id_header TEXT,
            identity_key      TEXT NOT NULL,
            from_name         TEXT,
            from_address      TEXT NOT NULL,
            to_json           TEXT NOT NULL,
            cc_json           TEXT NOT NULL,
            subject           TEXT NOT NULL,
            preview           TEXT NOT NULL,
            date              INTEGER NOT NULL,
            is_read           INTEGER NOT NULL,
            is_starred        INTEGER NOT NULL,
            has_attachments   INTEGER NOT NULL,
            spam_json         TEXT,
            size              INTEGER NOT NULL DEFAULT 0,
            in_reply_to       TEXT,
            references_json   TEXT,
            UNIQUE (folder_id, uid_validity, uid)
        );
        CREATE INDEX ix_messages_folder_date  ON messages (folder_id, date DESC);
        CREATE INDEX ix_messages_account_date ON messages (account_id, date DESC);
        CREATE INDEX ix_messages_identity     ON messages (account_id, identity_key);
        CREATE INDEX ix_messages_date         ON messages (date DESC);

        CREATE TABLE bodies (
            message_id       TEXT PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
            html             TEXT,
            text             TEXT,
            attachments_json TEXT NOT NULL,
            bcc_json         TEXT,
            cached_at        INTEGER NOT NULL
        );

        -- Pins and local stars are keyed by the message's stable identity (Message-ID), not by folder/UID,
        -- so they survive moves between folders, UIDVALIDITY resets and cache rebuilds.
        CREATE TABLE pins (
            account_id   TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            identity_key TEXT NOT NULL,
            pinned_at    INTEGER NOT NULL,
            PRIMARY KEY (account_id, identity_key)
        );

        CREATE TABLE local_stars (
            account_id   TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            identity_key TEXT NOT NULL,
            PRIMARY KEY (account_id, identity_key)
        );

        CREATE TABLE drafts (
            id         TEXT PRIMARY KEY,
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            json       TEXT NOT NULL,
            updated_at INTEGER NOT NULL
        );
        """,

        // 2 — Milestone 4: snooze, follow-up reminders, outbox (Undo Send / Schedule send), contacts.
        // Only adds tables: accounts, folders, messages and saved sign-ins are untouched.
        """
        CREATE TABLE snoozes (
            account_id   TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            identity_key TEXT NOT NULL,
            until_utc    INTEGER NOT NULL,
            created_at   INTEGER NOT NULL,
            PRIMARY KEY (account_id, identity_key)
        );
        CREATE INDEX ix_snoozes_until ON snoozes (until_utc);

        CREATE TABLE reminders (
            account_id   TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            identity_key TEXT NOT NULL,
            due_utc      INTEGER NOT NULL,
            created_at   INTEGER NOT NULL,
            notified_at  INTEGER,
            PRIMARY KEY (account_id, identity_key)
        );
        CREATE INDEX ix_reminders_due ON reminders (due_utc);

        -- No foreign key to accounts: demonstration-mailbox items live here too (is_demo = 1).
        CREATE TABLE outbox (
            id                TEXT PRIMARY KEY,
            account_id        TEXT NOT NULL,
            is_demo           INTEGER NOT NULL,
            kind              INTEGER NOT NULL,
            state             INTEGER NOT NULL,
            send_at_utc       INTEGER NOT NULL,
            draft_json        TEXT NOT NULL,
            attempts          INTEGER NOT NULL DEFAULT 0,
            last_error        TEXT,
            created_at        INTEGER NOT NULL,
            updated_at        INTEGER NOT NULL,
            accepted_at       INTEGER
        );
        CREATE INDEX ix_outbox_state ON outbox (state, send_at_utc);

        CREATE TABLE outbox_attempts (
            outbox_id TEXT NOT NULL REFERENCES outbox(id) ON DELETE CASCADE,
            at_utc    INTEGER NOT NULL,
            outcome   INTEGER NOT NULL,
            detail    TEXT
        );
        CREATE INDEX ix_outbox_attempts ON outbox_attempts (outbox_id);

        CREATE TABLE contacts (
            id           TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            notes        TEXT,
            source       TEXT NOT NULL DEFAULT 'local',
            created_at   INTEGER NOT NULL,
            updated_at   INTEGER NOT NULL
        );

        CREATE TABLE contact_emails (
            contact_id TEXT NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,
            email      TEXT NOT NULL COLLATE NOCASE,
            label      TEXT,
            position   INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (contact_id, email)
        );
        CREATE INDEX ix_contact_emails_email ON contact_emails (email);

        CREATE TABLE recent_recipients (
            email        TEXT PRIMARY KEY COLLATE NOCASE,
            display_name TEXT,
            last_used    INTEGER NOT NULL,
            use_count    INTEGER NOT NULL
        );
        """,

        // 3 — Milestone 5: full-text search, attachment details, smart folders, rules, categories, local spam learning.
        // Only adds tables, indexes and triggers; existing data is untouched (the search index is filled from it).
        """
        CREATE TABLE message_attachments (
            message_id   TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
            position     INTEGER NOT NULL,
            file_name    TEXT NOT NULL,
            content_type TEXT NOT NULL,
            size         INTEGER NOT NULL,
            PRIMARY KEY (message_id, position)
        );
        CREATE INDEX ix_message_attachments_name ON message_attachments (file_name COLLATE NOCASE);

        -- Search index. fts_map gives every message a stable integer key (implicit rowids can change on VACUUM).
        CREATE TABLE fts_map (
            fts_rowid  INTEGER PRIMARY KEY,
            message_id TEXT NOT NULL UNIQUE
        );
        CREATE VIRTUAL TABLE message_fts USING fts5(
            subject, sender, recipients, body, attachments,
            tokenize = 'unicode61 remove_diacritics 2', prefix = '2 3');

        CREATE TRIGGER trg_messages_fts_insert AFTER INSERT ON messages BEGIN
            INSERT OR IGNORE INTO fts_map (message_id) VALUES (new.id);
            INSERT INTO message_fts (rowid, subject, sender, recipients, body, attachments)
            SELECT fts_rowid, new.subject, COALESCE(new.from_name, '') || ' ' || new.from_address,
                   (SELECT COALESCE(group_concat(COALESCE(json_extract(value, '$.name'), '') || ' ' || COALESCE(json_extract(value, '$.address'), ''), ' '), '')
                      FROM json_each(new.to_json)) || ' ' ||
                   (SELECT COALESCE(group_concat(COALESCE(json_extract(value, '$.name'), '') || ' ' || COALESCE(json_extract(value, '$.address'), ''), ' '), '')
                      FROM json_each(new.cc_json)),
                   new.preview, ''
            FROM fts_map WHERE message_id = new.id;
        END;

        CREATE TRIGGER trg_messages_fts_delete AFTER DELETE ON messages BEGIN
            DELETE FROM message_fts WHERE rowid = (SELECT fts_rowid FROM fts_map WHERE message_id = old.id);
            DELETE FROM fts_map WHERE message_id = old.id;
        END;

        CREATE TRIGGER trg_bodies_fts AFTER INSERT ON bodies BEGIN
            UPDATE message_fts SET body = COALESCE(new.text, body)
            WHERE rowid = (SELECT fts_rowid FROM fts_map WHERE message_id = new.message_id);
        END;

        CREATE TRIGGER trg_bodies_fts_update AFTER UPDATE OF text ON bodies BEGIN
            UPDATE message_fts SET body = COALESCE(new.text, body)
            WHERE rowid = (SELECT fts_rowid FROM fts_map WHERE message_id = new.message_id);
        END;

        CREATE TRIGGER trg_attachments_fts AFTER INSERT ON message_attachments BEGIN
            UPDATE message_fts SET attachments = attachments || ' ' || new.file_name
            WHERE rowid = (SELECT fts_rowid FROM fts_map WHERE message_id = new.message_id);
        END;

        INSERT INTO fts_map (message_id) SELECT id FROM messages;
        INSERT INTO message_fts (rowid, subject, sender, recipients, body, attachments)
        SELECT fm.fts_rowid, m.subject, COALESCE(m.from_name, '') || ' ' || m.from_address,
               (SELECT COALESCE(group_concat(COALESCE(json_extract(value, '$.name'), '') || ' ' || COALESCE(json_extract(value, '$.address'), ''), ' '), '')
                  FROM json_each(m.to_json)) || ' ' ||
               (SELECT COALESCE(group_concat(COALESCE(json_extract(value, '$.name'), '') || ' ' || COALESCE(json_extract(value, '$.address'), ''), ' '), '')
                  FROM json_each(m.cc_json)),
               COALESCE(b.text, m.preview), ''
        FROM messages m JOIN fts_map fm ON fm.message_id = m.id LEFT JOIN bodies b ON b.message_id = m.id;

        CREATE TABLE smart_folders (
            id         TEXT PRIMARY KEY,
            name       TEXT NOT NULL,
            query      TEXT NOT NULL,
            glyph      TEXT,
            position   INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL
        );

        CREATE TABLE mail_rules (
            id              TEXT PRIMARY KEY,
            name            TEXT NOT NULL,
            enabled         INTEGER NOT NULL,
            position        INTEGER NOT NULL DEFAULT 0,
            definition_json TEXT NOT NULL,
            created_at      INTEGER NOT NULL,
            updated_at      INTEGER NOT NULL
        );

        -- Which rule has already acted on which message (by identity, so moving a message doesn't re-run rules).
        CREATE TABLE rule_applications (
            rule_id      TEXT NOT NULL,
            account_id   TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            identity_key TEXT NOT NULL,
            applied_at   INTEGER NOT NULL,
            PRIMARY KEY (rule_id, account_id, identity_key)
        );

        CREATE TABLE message_categories (
            account_id   TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            identity_key TEXT NOT NULL,
            category     TEXT NOT NULL COLLATE NOCASE,
            PRIMARY KEY (account_id, identity_key, category)
        );

        -- Local spam learning: word counts from messages you marked Junk / Not junk. Never leaves this PC.
        CREATE TABLE spam_tokens (
            token      TEXT PRIMARY KEY,
            spam_count INTEGER NOT NULL DEFAULT 0,
            ham_count  INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE spam_training (
            account_id   TEXT NOT NULL,
            identity_key TEXT NOT NULL,
            is_spam      INTEGER NOT NULL,
            tokens_json  TEXT NOT NULL,
            trained_at   INTEGER NOT NULL,
            PRIMARY KEY (account_id, identity_key)
        );
        """,
    ];

    private readonly string _connectionString;

    public MailDatabase(string databaseFile)
    {
        DatabaseFile = databaseFile;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            ForeignKeys = true,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public MailDatabase(AppDataPaths paths)
        : this(paths.DatabaseFile)
    {
    }

    public string DatabaseFile { get; }

    public static int LatestVersion => Migrations.Length;

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Creates the database if needed and applies pending migrations. Returns the resulting version.</summary>
    public async Task<int> MigrateAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(DatabaseFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);

        var version = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false));
        if (version > Migrations.Length)
        {
            throw new InvalidOperationException(
                $"The mail cache was created by a newer version of SidePhase Mail (schema {version}). Update the app or clear local data.");
        }

        for (var next = version; next < Migrations.Length; next++)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = Migrations[next] + $"\nPRAGMA user_version = {next + 1};";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return Migrations.Length;
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
}
