using System.Security.Cryptography;
using System.Text;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Infrastructure.Mail;

/// <summary>
/// Stable identifiers. A cached message's id is derived from (account, folder path, UIDVALIDITY, UID), so it is the
/// same after every restart and re-sync and never depends on IMAP sequence numbers. The <em>identity key</em> follows
/// the message itself (its Message-ID header) and is what pins and local favourites attach to, so they survive moves.
/// </summary>
public static class MessageIdentity
{
    public static Guid ForImapMessage(Guid accountId, string folderPath, uint uidValidity, uint uid) =>
        DeterministicGuid($"msg|{accountId:N}|{folderPath}|{uidValidity}|{uid}");

    public static Guid ForFolder(Guid accountId, string folderPath) =>
        DeterministicGuid($"folder|{accountId:N}|{folderPath}");

    public static string IdentityKey(string? messageIdHeader, EmailAddress from, DateTimeOffset date, string? subject)
    {
        var id = messageIdHeader?.Trim().Trim('<', '>').Trim();
        if (!string.IsNullOrEmpty(id)) return "mid:" + id;

        // No Message-ID (rare, usually spam or very old software): fall back to a content fingerprint.
        var fingerprint = $"{from.NormalizedAddress}|{date.ToUnixTimeSeconds()}|{subject}";
        return "h:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..32].ToLowerInvariant();
    }

    /// <summary>A name-based (RFC 4122 version 5 style) GUID from SHA-256.</summary>
    internal static Guid DeterministicGuid(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50); // version 5 (in Guid's little-endian layout)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes);
    }
}
