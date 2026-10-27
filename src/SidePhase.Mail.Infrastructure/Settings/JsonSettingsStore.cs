using System.Text.Json;
using System.Text.Json.Serialization;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Settings;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Infrastructure.Settings;

/// <summary>
/// Stores preferences as JSON. Writes are atomic (temp file + replace) so a crash can't leave a half-written file.
/// Never stores passwords or tokens; those belong in the Windows credential store (Milestone 2).
/// </summary>
public sealed class JsonSettingsStore(AppDataPaths paths) : ISettingsStore
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public async Task<AppSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var file = paths.SettingsFile;
        if (!File.Exists(file)) return null;

        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        try
        {
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            stream.Close();
            // Keep the unreadable file for diagnosis and start with defaults.
            File.Copy(file, file + ".corrupt", overwrite: true);
            return null;
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var file = paths.SettingsFile;
        var temp = file + ".tmp";

        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, file, overwrite: true);
    }
}
