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
            account_id   TEXT NOT NULL,
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
        BackupsFolder = paths.BackupsFolder;
    }

    public string DatabaseFile { get; }

    public static int LatestVersion => Migrations.Length;

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private readonly Lock _migrationGate = new();
    private Task<int>? _migration;

    /// <summary>Where copies of the database are kept before schema upgrades (null: no copies, e.g. in tests).</summary>
    public string? BackupsFolder { get; init; }

    /// <summary>How many pre-upgrade copies are kept.</summary>
    public const int BackupsKept = 3;

    /// <summary>
    /// Creates the database if needed and applies pending migrations. Returns the resulting version. Safe to call from
    /// many places at once: the work runs once per process (and is retried if it failed), and each migration runs in
    /// an exclusive transaction that re-checks the version, so two processes can't apply the same step twice.
    /// </summary>
    /// <exception cref="MailCacheException">The file is damaged or was created by a newer version of the app.</exception>
    public Task<int> MigrateAsync(CancellationToken cancellationToken = default)
    {
        lock (_migrationGate)
        {
            if (_migration is null || _migration.IsFaulted || _migration.IsCanceled)
                _migration = Task.Run(() => MigrateCoreAsync(CancellationToken.None), CancellationToken.None);
            return _migration.WaitAsync(cancellationToken);
        }
    }

    private async Task<int> MigrateCoreAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(DatabaseFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);

            var version = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false));
            if (version > Migrations.Length)
            {
                throw new MailCacheException(MailCacheProblem.NewerVersion,
                    $"The mail cache was created by a newer version of SidePhase Mail (schema {version}; this version understands {Migrations.Length}). Install the newer version to keep using it. Nothing was changed.");
            }

            if (version > 0 && version < Migrations.Length) BackUp(connection, version);

            for (var next = version; next < Migrations.Length; next++)
            {
                // BEGIN IMMEDIATE takes the write lock first, then the version is re-read: if another process has
                // already applied this step, skip it.
                await using var transaction = connection.BeginTransaction(deferred: false);
                var current = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken, transaction).ConfigureAwait(false));
                if (current > next)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    next = current - 1;
                    continue;
                }

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
        catch (SqliteException ex) when (ex.SqliteErrorCode is SQLITE_CORRUPT or SQLITE_NOTADB)
        {
            throw new MailCacheException(MailCacheProblem.Damaged,
                "The local mail cache file is damaged and can't be opened. Nothing was changed.", ex);
        }
    }

    private const int SQLITE_CORRUPT = 11;
    private const int SQLITE_NOTADB = 26;

    /// <summary>Copies the database (consistently, with SQLite's backup API) before it's upgraded.</summary>
    private void BackUp(SqliteConnection connection, int version)
    {
        if (BackupsFolder is null) return;
        try
        {
            Directory.CreateDirectory(BackupsFolder);
            var file = Path.Combine(BackupsFolder, $"mail-cache-schema{version}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
            {
                destination.Open();
                connection.BackupDatabase(destination);
            }

            foreach (var old in Directory.GetFiles(BackupsFolder, "mail-cache-schema*.db")
                         .OrderByDescending(f => File.GetCreationTimeUtc(f)).ThenByDescending(f => f, StringComparer.Ordinal).Skip(BackupsKept))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            // The upgrade itself is transactional, so a missing copy doesn't put data at risk; carry on.
            LastBackupError = ex.Message;
        }
    }

    /// <summary>Why the last pre-upgrade copy couldn't be made, if it failed.</summary>
    public string? LastBackupError { get; private set; }

    /// <summary>Runs SQLite's quick integrity check. Returns null if the database is fine, or the problems found.</summary>
    public async Task<string?> CheckIntegrityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check(20);";
        var problems = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) problems.Add(reader.GetString(0));
        return problems is ["ok"] ? null : string.Join("; ", problems);
    }

    /// <summary>
    /// Moves a damaged database (and its WAL files) into the Backups folder so a new one can be created. Only called
    /// after the user agrees. Returns where it was moved.
    /// </summary>
    public string SetAside()
    {
        SqliteConnection.ClearAllPools();
        var folder = BackupsFolder ?? Path.GetDirectoryName(DatabaseFile)!;
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"mail-cache-damaged-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
        File.Move(DatabaseFile, target);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(DatabaseFile + suffix)) File.Move(DatabaseFile + suffix, target + suffix);
        lock (_migrationGate) _migration = null;
        return target;
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
}

public enum MailCacheProblem
{
    Damaged,
    NewerVersion,
}

/// <summary>The mail cache can't be used as it is. The message is safe to show; the file has not been changed.</summary>
public sealed class MailCacheException(MailCacheProblem problem, string message, Exception? inner = null) : Exception(message, inner)
{
    public MailCacheProblem Problem { get; } = problem;
}
