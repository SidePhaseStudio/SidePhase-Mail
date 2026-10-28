using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SidePhase.Mail.Infrastructure.Storage;

/// <summary>The outcome of <see cref="PortableDataImporter.ImportIfNeeded"/>.</summary>
public sealed record DataImportResult(DataImportOutcome Outcome, string? Detail = null)
{
    public static readonly DataImportResult NothingToDo = new(DataImportOutcome.NothingToImport);
}

public enum DataImportOutcome
{
    /// <summary>No earlier data, or the installed app already has its own data (nothing is overwritten).</summary>
    NothingToImport,

    /// <summary>Accounts, settings and the mail cache were copied; the original folder was left untouched.</summary>
    Imported,

    /// <summary>The copy failed; nothing was changed and the app starts with empty data. The detail says why.</summary>
    Failed,
}

/// <summary>
/// The first time the installed (MSIX) app starts, copies the data of the portable/unpackaged build
/// (%LOCALAPPDATA%\SidePhase Mail) into the package's own data folder, so accounts, saved passwords and tokens
/// (DPAPI, same Windows user), settings, pins, snoozes, the outbox and the mail cache carry over.
/// <list type="bullet">
/// <item>It is a copy: the original folder is never changed or deleted, so the portable build keeps working.</item>
/// <item>It runs once and never overwrites: if the installed app already has data, nothing is copied.</item>
/// <item>It is all-or-nothing: files are staged first and only moved into place when everything copied.</item>
/// <item>SQLite databases are copied with SQLite's backup API, so a copy taken while the portable build is running is
/// still consistent.</item>
/// <item>Logs, the WebView2 cache and old backups are not copied.</item>
/// </list>
/// </summary>
public static class PortableDataImporter
{
    public const string MarkerFileName = "imported-from-portable.json";
    private const string StagingFolderName = ".import-staging";

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Logs", "WebView2", "Backups", StagingFolderName,
    };

    /// <summary>Copies <paramref name="sourceRoot"/> into <paramref name="targetRoot"/> if the target has no data yet.</summary>
    public static DataImportResult ImportIfNeeded(string sourceRoot, string targetRoot)
    {
        var source = Path.GetFullPath(sourceRoot);
        var target = Path.GetFullPath(targetRoot);
        if (string.Equals(source.TrimEnd(Path.DirectorySeparatorChar), target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return DataImportResult.NothingToDo;
        if (!HasData(source)) return DataImportResult.NothingToDo;
        if (HasData(target) || File.Exists(Path.Combine(target, MarkerFileName))) return DataImportResult.NothingToDo;

        var staging = Path.Combine(target, StagingFolderName);
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); // our own leftover from an interrupted copy
            Directory.CreateDirectory(staging);
            CopyFolder(source, staging, isRoot: true);

            // Everything copied: move it into place (the target has no data files, checked above).
            foreach (var entry in Directory.EnumerateFileSystemEntries(staging))
            {
                var destination = Path.Combine(target, Path.GetFileName(entry));
                if (Directory.Exists(entry)) MergeFolder(entry, destination);
                else File.Move(entry, destination, overwrite: false);
            }

            Directory.Delete(staging, recursive: true);
            File.WriteAllText(Path.Combine(target, MarkerFileName), JsonSerializer.Serialize(new
            {
                importedAt = DateTimeOffset.UtcNow,
                from = "portable build data folder",
            }));
            return new DataImportResult(DataImportOutcome.Imported, source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (Exception)
            {
                // Leave the staging folder; it's ignored and replaced next time.
            }

            return new DataImportResult(DataImportOutcome.Failed, ex.Message);
        }
    }

    /// <summary>True if the folder holds settings or a mail cache.</summary>
    public static bool HasData(string root) =>
        File.Exists(Path.Combine(root, "settings.json")) || File.Exists(Path.Combine(root, "mail-cache.db"));

    private static void CopyFolder(string from, string to, bool isRoot)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var name = Path.GetFileName(file);
            if (isRoot && name.Equals(MarkerFileName, StringComparison.OrdinalIgnoreCase)) continue;

            // SQLite side files are folded into the database copy by the backup API.
            if (name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-journal", StringComparison.OrdinalIgnoreCase)) continue;

            var destination = Path.Combine(to, name);
            if (name.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) CopyDatabase(file, destination);
            else File.Copy(file, destination, overwrite: false);
        }

        foreach (var folder in Directory.EnumerateDirectories(from))
        {
            var name = Path.GetFileName(folder);
            if (isRoot && SkippedFolders.Contains(name)) continue;
            CopyFolder(folder, Path.Combine(to, name), isRoot: false);
        }
    }

    private static void CopyDatabase(string from, string to)
    {
        // Read-only, so the original file is never written (not even a WAL checkpoint). If SQLite can't open it that
        // way (a WAL database whose -shm file is missing needs to create one), fall back to a normal open: still
        // nothing is changed except what the portable build itself would do on its next start.
        try
        {
            Backup(from, to, SqliteOpenMode.ReadOnly);
        }
        catch (SqliteException)
        {
            if (File.Exists(to)) File.Delete(to);
            Backup(from, to, SqliteOpenMode.ReadWrite);
        }

        // Check the copy before trusting it.
        using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = to, Pooling = false }.ToString());
        check.Open();
        using var command = check.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = command.ExecuteScalar() as string;
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"The copied database failed its integrity check ({result}).");
    }

    private static void Backup(string from, string to, SqliteOpenMode mode)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = from, Mode = mode, Pooling = false }.ToString());
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = to, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
        }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static void MergeFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Move(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        foreach (var folder in Directory.EnumerateDirectories(from))
            MergeFolder(folder, Path.Combine(to, Path.GetFileName(folder)));
    }
}
