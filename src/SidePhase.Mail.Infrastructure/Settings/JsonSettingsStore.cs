using System.Text.Json;
using System.Text.Json.Serialization;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Settings;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Infrastructure.Settings;

/// <summary>
/// Stores preferences as JSON. Never stores passwords or tokens (those are in the DPAPI secret store).
/// <list type="bullet">
/// <item>Writes are atomic and durable: a temp file is written and flushed to disk, then swapped in, keeping the
/// previous file as <c>settings.json.bak</c>.</item>
/// <item>A damaged file is never deleted: it's kept as <c>settings.json.corrupt-&lt;time&gt;</c> and the last good
/// copy is used instead.</item>
/// <item>If the file can't be read at all (locked by another program, no access), loading fails with
/// <see cref="SettingsUnavailableException"/> rather than pretending there are no settings.</item>
/// </list>
/// </summary>
public sealed class JsonSettingsStore(AppDataPaths paths) : IRecoveringSettingsStore
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private const int ReadAttempts = 4;

    public string BackupFile => paths.SettingsFile + ".bak";

    public async Task<AppSettings?> LoadAsync(CancellationToken cancellationToken = default) =>
        (await LoadWithRecoveryAsync(cancellationToken).ConfigureAwait(false)).Settings;

    public async Task<SettingsLoadResult> LoadWithRecoveryAsync(CancellationToken cancellationToken = default)
    {
        var file = paths.SettingsFile;
        if (!File.Exists(file))
        {
            // A crash between the two steps of a save can leave only the backup.
            return File.Exists(BackupFile) && await TryReadAsync(BackupFile, cancellationToken).ConfigureAwait(false) is { } fromBackup
                ? new SettingsLoadResult(fromBackup, RestoredFromBackup: true)
                : new SettingsLoadResult(null, false);
        }

        var bytes = await ReadWithRetriesAsync(file, cancellationToken).ConfigureAwait(false);
        if (Deserialize(bytes) is { } settings) return new SettingsLoadResult(settings, false);

        // Damaged: keep it for diagnosis (never overwriting an earlier copy) and use the last good copy if any.
        File.Copy(file, $"{file}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false);
        return File.Exists(BackupFile) && await TryReadAsync(BackupFile, cancellationToken).ConfigureAwait(false) is { } backup
            ? new SettingsLoadResult(backup, RestoredFromBackup: true)
            : new SettingsLoadResult(null, false);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var file = paths.SettingsFile;
        var temp = $"{file}.{Environment.ProcessId}.tmp";

        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(file))
        {
            // Keep the previous file as the backup only if it's valid, so a damaged file never replaces a good backup.
            var previousIsValid = Deserialize(await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false)) is not null;
            if (previousIsValid) File.Replace(temp, file, BackupFile, ignoreMetadataErrors: true);
            else File.Move(temp, file, overwrite: true);
        }
        else
        {
            File.Move(temp, file);
        }
    }

    private static AppSettings? Deserialize(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(bytes, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<AppSettings?> TryReadAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            return Deserialize(await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadWithRetriesAsync(string file, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Antivirus, backup or sync tools can hold the file briefly.
                if (attempt >= ReadAttempts)
                    throw new SettingsUnavailableException($"The settings file couldn't be read ({ex.Message}).", ex);
                await Task.Delay(150 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
