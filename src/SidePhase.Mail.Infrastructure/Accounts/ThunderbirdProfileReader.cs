using System.Text.RegularExpressions;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Infrastructure.Accounts;

/// <summary>An IMAP account's settings found in a Thunderbird profile (never a password or token).</summary>
public sealed record ThunderbirdAccount(
    string EmailAddress,
    string? DisplayName,
    ServerEndpoint Imap,
    ServerEndpoint? Smtp,
    string Username,
    bool UsesOAuth);

/// <summary>A Thunderbird profile folder listed in profiles.ini.</summary>
public sealed record ThunderbirdProfile(string Name, string Path, bool IsDefault);

/// <summary>
/// Reads account <b>server settings only</b> from a Thunderbird profile's <c>prefs.js</c>, a documented plain-text
/// preferences file. It is used only after the user agrees and picks the profile folder, and it:
/// <list type="bullet">
/// <item>never opens <c>logins.json</c>, <c>key4.db</c>, <c>cert9.db</c> or any other file holding passwords, keys or
/// tokens: saved passwords are never extracted; the user signs in again in SidePhase Mail;</item>
/// <item>never reads, copies or changes mail, address books or anything else in the profile (read-only);</item>
/// <item>skips POP3, local folders, news and RSS accounts, and accounts without encryption.</item>
/// </list>
/// </summary>
public static partial class ThunderbirdProfileReader
{
    /// <summary>Largest prefs.js read (real ones are well under 1 MB).</summary>
    private const long MaxPrefsBytes = 8 * 1024 * 1024;

    /// <summary>Thunderbird's usual profile location on Windows (%APPDATA%\Thunderbird).</summary>
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Thunderbird");

    /// <summary>Profiles listed in profiles.ini under <paramref name="thunderbirdRoot"/> (empty if none).</summary>
    public static IReadOnlyList<ThunderbirdProfile> FindProfiles(string thunderbirdRoot)
    {
        var ini = Path.Combine(thunderbirdRoot, "profiles.ini");
        if (!File.Exists(ini)) return [];
        var profiles = new List<ThunderbirdProfile>();
        string? name = null, path = null;
        var relative = true;
        var isDefault = false;

        void Flush()
        {
            if (path is null) return;
            var full = relative ? System.IO.Path.Combine(thunderbirdRoot, path.Replace('/', System.IO.Path.DirectorySeparatorChar)) : path;
            if (Directory.Exists(full)) profiles.Add(new ThunderbirdProfile(name ?? System.IO.Path.GetFileName(full), full, isDefault));
        }

        foreach (var raw in File.ReadLines(ini))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                Flush();
                name = path = null;
                relative = true;
                isDefault = false;
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "Name": name = value; break;
                case "Path": path = value; break;
                case "IsRelative": relative = value != "0"; break;
                case "Default": isDefault = value == "1"; break;
            }
        }

        Flush();
        return profiles;
    }

    /// <summary>Reads the IMAP accounts from <c>prefs.js</c> in <paramref name="profileFolder"/>.</summary>
    public static IReadOnlyList<ThunderbirdAccount> ReadAccounts(string profileFolder)
    {
        var prefsFile = Path.Combine(profileFolder, "prefs.js");
        if (!File.Exists(prefsFile)) throw new FileNotFoundException("This folder isn't a Thunderbird profile (prefs.js wasn't found).");
        if (new FileInfo(prefsFile).Length > MaxPrefsBytes) throw new InvalidDataException("prefs.js is unexpectedly large, so it wasn't read.");

        string text;
        using (var stream = new FileStream(prefsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            text = reader.ReadToEnd();
        }

        return ParseAccounts(text);
    }

    /// <summary>Parses prefs.js text (exposed for tests).</summary>
    public static IReadOnlyList<ThunderbirdAccount> ParseAccounts(string prefsJs)
    {
        var prefs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in PrefRegex().Matches(prefsJs))
        {
            var value = match.Groups["str"].Success ? Unescape(match.Groups["str"].Value) : match.Groups["raw"].Value;
            prefs[match.Groups["key"].Value] = value;
        }

        string? Get(string key) => prefs.TryGetValue(key, out var v) ? v : null;
        int? GetInt(string key) => int.TryParse(Get(key), out var n) ? n : null;

        var accounts = new List<ThunderbirdAccount>();
        var accountIds = (Get("mail.accountmanager.accounts") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var accountId in accountIds)
        {
            var server = Get($"mail.account.{accountId}.server");
            if (server is null || Get($"mail.server.{server}.type") != "imap") continue;

            var identity = (Get($"mail.account.{accountId}.identities") ?? string.Empty).Split(',', StringSplitOptions.TrimEntries).FirstOrDefault();
            var email = identity is null ? null : Get($"mail.identity.{identity}.useremail");
            var host = Get($"mail.server.{server}.hostname") ?? Get($"mail.server.{server}.realhostname");
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(host) || !AccountDiscovery.IsValidAddress(email)) continue;

            var imapSecurity = Security(GetInt($"mail.server.{server}.socketType"));
            if (imapSecurity is null) continue; // unencrypted: SidePhase Mail won't connect
            var imap = new ServerEndpoint(host.Trim(), GetInt($"mail.server.{server}.port") ?? (imapSecurity == ConnectionSecurity.SslOnConnect ? 993 : 143), imapSecurity.Value);

            var smtpId = (identity is null ? null : Get($"mail.identity.{identity}.smtpServer")) ?? Get("mail.smtp.defaultserver");
            ServerEndpoint? smtp = null;
            if (smtpId is not null && Get($"mail.smtpserver.{smtpId}.hostname") is { Length: > 0 } smtpHost
                && Security(GetInt($"mail.smtpserver.{smtpId}.try_ssl")) is { } smtpSecurity)
            {
                smtp = new ServerEndpoint(smtpHost.Trim(), GetInt($"mail.smtpserver.{smtpId}.port") ?? (smtpSecurity == ConnectionSecurity.SslOnConnect ? 465 : 587), smtpSecurity);
            }

            accounts.Add(new ThunderbirdAccount(
                email.Trim(),
                identity is null ? null : Get($"mail.identity.{identity}.fullName"),
                imap,
                smtp,
                Get($"mail.server.{server}.userName") ?? email.Trim(),
                UsesOAuth: GetInt($"mail.server.{server}.authMethod") == 10));
        }

        return accounts;
    }

    // Thunderbird: socketType / try_ssl 3 = SSL/TLS, 2 = STARTTLS, 0/1 = none or "if available" (not accepted).
    private static ConnectionSecurity? Security(int? value) => value switch
    {
        3 => ConnectionSecurity.SslOnConnect,
        2 => ConnectionSecurity.StartTls,
        _ => null,
    };

    private static string Unescape(string value) =>
        value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    [GeneratedRegex(@"user_pref\(\s*""(?<key>[^""]+)""\s*,\s*(?:""(?<str>(?:[^""\\]|\\.)*)""|(?<raw>[^)\s]+))\s*\)\s*;")]
    private static partial Regex PrefRegex();
}
