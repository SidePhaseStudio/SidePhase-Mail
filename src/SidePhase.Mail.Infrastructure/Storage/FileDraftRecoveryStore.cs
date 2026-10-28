using System.Text.Json;
using SidePhase.Mail.Core.Composition;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Infrastructure.Storage;

/// <summary>Recovery copies in %LOCALAPPDATA%\SidePhase Mail\Recovery, one JSON file per open compose window.</summary>
public sealed class FileDraftRecoveryStore(AppDataPaths paths) : IDraftRecoveryStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string Folder => Path.Combine(paths.Root, "Recovery");

    private string FileFor(Guid id) => Path.Combine(Folder, id.ToString("N") + ".json");

    public async Task SaveAsync(Guid sessionId, DraftMessage draft, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Folder);
            var target = FileFor(sessionId);
            var temp = target + ".tmp";

            // Write then rename, so a crash mid-write never leaves a half-written copy.
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new Envelope(draft, DateTimeOffset.UtcNow), Json), ct).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid sessionId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            File.Delete(FileFor(sessionId));
            File.Delete(FileFor(sessionId) + ".tmp");
        }
        catch (IOException)
        {
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<RecoveredDraft>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(Folder)) return [];
        var result = new List<RecoveredDraft>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id)) continue;
            try
            {
                var envelope = JsonSerializer.Deserialize<Envelope>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false), Json);
                if (envelope?.Draft is not null) result.Add(new RecoveredDraft(id, envelope.Draft, envelope.SavedAt));
            }
            catch (JsonException)
            {
                // A damaged copy can't be recovered; leave it for the user to find rather than crash.
            }
            catch (IOException)
            {
            }
        }

        return result.OrderBy(r => r.SavedAt).ToList();
    }

    private sealed record Envelope(DraftMessage Draft, DateTimeOffset SavedAt);
}
