using SidePhase.Mail.Core.Models;
using SidePhase.Mail.Core.Scheduling;

namespace SidePhase.Mail.Infrastructure.Storage;

/// <summary>Copies queued messages' attachments to %LOCALAPPDATA%\SidePhase Mail\Outbox\{id}\ until they're sent.</summary>
public sealed class FileOutboxAttachmentStore(AppDataPaths paths) : IOutboxAttachmentStore
{
    private string FolderFor(Guid itemId) => Path.Combine(paths.Root, "Outbox", itemId.ToString("N"));

    public async Task<DraftMessage> KeepAsync(Guid itemId, DraftMessage draft, CancellationToken ct = default)
    {
        if (draft.Attachments.Count == 0) return draft;
        var folder = FolderFor(itemId);
        Directory.CreateDirectory(folder);
        var kept = new List<DraftAttachment>();
        var index = 0;
        foreach (var attachment in draft.Attachments)
        {
            if (attachment.SourcePath is null || !File.Exists(attachment.SourcePath))
            {
                throw new FileNotFoundException($"The attachment \"{attachment.FileName}\" can't be found. Attach it again before sending.");
            }

            // Already one of ours (e.g. the queued message was edited): keep using it.
            if (Path.GetDirectoryName(Path.GetFullPath(attachment.SourcePath)) == Path.GetFullPath(folder))
            {
                kept.Add(attachment);
                continue;
            }

            var target = Path.Combine(folder, $"{index++:D3}-{Path.GetFileName(attachment.FileName)}");
            await using (var source = File.OpenRead(attachment.SourcePath))
            await using (var destination = File.Create(target))
            {
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
            }

            kept.Add(attachment with { SourcePath = target });
        }

        return draft with { Attachments = kept };
    }

    public Task ReleaseAsync(Guid itemId, CancellationToken ct = default)
    {
        try
        {
            var folder = FolderFor(itemId);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }
}
