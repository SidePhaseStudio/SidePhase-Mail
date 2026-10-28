using System.Security.Cryptography;
using System.Text;
using MimeKit;

namespace SidePhase.Mail.Infrastructure.Storage;

/// <summary>A sent message whose copy hasn't been filed in the account's Sent folder yet.</summary>
public sealed record PendingSentCopy(Guid AccountId, string MessageId, string File);

/// <summary>
/// Sent messages waiting for their Sent-folder copy (Outbox\SentCopies). The copy is written here as soon as the
/// server accepts a message and removed once it's in Sent, so closing or crashing the app between the two never loses
/// it: the next sync files it (checking by Message-ID first, so it never appears twice).
/// </summary>
public sealed class PendingSentCopies(AppDataPaths paths)
{
    /// <summary>Copies older than this are given up on (the user can still find the message in their recipients' replies).</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(14);

    private string Folder => Path.Combine(paths.Root, "Outbox", "SentCopies");

    public async Task<string> SaveAsync(Guid accountId, MimeMessage message, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, $"{accountId:N}-{Hash(message.MessageId ?? Guid.NewGuid().ToString())}.eml");
        var temp = file + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await message.WriteToAsync(stream, ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, file, overwrite: true);
        return file;
    }

    public IReadOnlyList<PendingSentCopy> List()
    {
        if (!Directory.Exists(Folder)) return [];
        var result = new List<PendingSentCopy>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.eml"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var dash = name.IndexOf('-');
            if (dash != 32 || !Guid.TryParseExact(name[..dash], "N", out var accountId)) continue;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > GiveUpAfter)
            {
                TryDelete(file);
                continue;
            }

            result.Add(new PendingSentCopy(accountId, name[(dash + 1)..], file));
        }

        return result;
    }

    public static async Task<MimeMessage> LoadAsync(PendingSentCopy copy, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(copy.File);
        return await MimeMessage.LoadAsync(stream, ct).ConfigureAwait(false);
    }

    public static void Remove(string file) => TryDelete(file);

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again next time.
        }
    }

    private static string Hash(string messageId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageId)))[..24].ToLowerInvariant();
}
