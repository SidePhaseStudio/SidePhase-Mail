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
