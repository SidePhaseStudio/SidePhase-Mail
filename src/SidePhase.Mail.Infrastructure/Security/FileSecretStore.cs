using System.Security.Cryptography;
using System.Text;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Infrastructure.Security;

/// <summary>Encrypts and decrypts secret bytes for the current user.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>
/// Windows Data Protection API (DPAPI), scoped to the current Windows user. Only that user, on this PC, can decrypt
/// the data; copying the files to another account or machine makes them useless.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    // Additional entropy ties the blobs to SidePhase Mail so other DPAPI consumers can't trivially reuse them.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SidePhase Mail · secrets · v1");

    public byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Secure credential storage requires Windows.");
        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Secure credential storage requires Windows.");
        return ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
    }
}

/// <summary>
/// Stores each secret as an encrypted file under %LOCALAPPDATA%\SidePhase Mail\Secrets. File names are hashes of the
/// key (the key itself is stored, unencrypted, on the first line of the file; it holds only an account id and a kind).
/// </summary>
public sealed class FileSecretStore(AppDataPaths paths, ISecretProtector protector) : ISecretStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task SetAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(secret);
        var blob = protector.Protect(Encoding.UTF8.GetBytes(secret));
        var payload = Encoding.UTF8.GetBytes(key + "\n" + Convert.ToBase64String(blob));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(paths.SecretsFolder);
            var file = FileFor(key);
            var temp = file + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true); // a power cut must not leave an empty secret behind
            }

            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var file = FileFor(key);
        if (!File.Exists(file)) return null;

        string text;
        try
        {
            text = Encoding.UTF8.GetString(await ReadSharedAsync(file, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SecretStoreUnavailableException("Saved sign-in details couldn't be read: " + ex.Message, ex);
        }

        var newline = text.IndexOf('\n');
        if (newline < 0 || text[..newline] != key) return null;

        try
        {
            return Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(text[(newline + 1)..])));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Encrypted for a different Windows user or machine, or damaged: treat as missing so the user is asked to sign in.
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = FileFor(key);
            if (File.Exists(file)) File.Delete(file);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.SecretsFolder)) return;
        foreach (var file in Directory.EnumerateFiles(paths.SecretsFolder, "*.secret"))
        {
            string key;
            try
            {
                var text = Encoding.UTF8.GetString(await ReadSharedAsync(file, cancellationToken).ConfigureAwait(false));
                var newline = text.IndexOf('\n');
                key = newline < 0 ? string.Empty : text[..newline];
            }
            catch (IOException)
            {
                continue;
            }

            if (key.StartsWith(prefix, StringComparison.Ordinal)) await DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }

    private string FileFor(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
        return Path.Combine(paths.SecretsFolder, hash + ".secret");
    }

    /// <summary>Reads without blocking a concurrent replace or delete of the same file.</summary>
    private static async Task<byte[]> ReadSharedAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        var buffer = new byte[stream.Length];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }
}

/// <summary>Well-known secret keys.</summary>
public static class SecretKeys
{
    public static string AccountPrefix(Guid accountId) => $"account/{accountId:N}/";
    public static string Password(Guid accountId) => AccountPrefix(accountId) + "password";
    public static string OAuthTokens(Guid accountId) => AccountPrefix(accountId) + "oauth";

}

/// <summary>The secret store exists but can't be read right now (locked or no access). Not the same as "signed out".</summary>
public sealed class SecretStoreUnavailableException(string message, Exception inner) : IOException(message, inner);
