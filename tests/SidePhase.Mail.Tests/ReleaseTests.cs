using Microsoft.Data.Sqlite;
using SidePhase.Mail.Core.Composition;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Tests;

public sealed class MailtoLinkTests
{
    [Fact]
    public void Parses_recipients_subject_and_body()
    {
        var link = MailtoLink.Parse("mailto:alice@example.test,bob@example.test?cc=carol@example.test&bcc=dan@example.test&subject=Hello%20there&body=Line%201%0D%0ALine%202");

        Assert.NotNull(link);
        Assert.Equal(["alice@example.test", "bob@example.test"], link.To.Select(a => a.Address));
        Assert.Equal("carol@example.test", Assert.Single(link.Cc).Address);
        Assert.Equal("dan@example.test", Assert.Single(link.Bcc).Address);
        Assert.Equal("Hello there", link.Subject);
        Assert.Equal("Line 1\nLine 2", link.Body);
    }

    [Theory]
    [InlineData("mailto:a@example.test?attach=C:\\Users\\me\\secret.docx")]
    [InlineData("mailto:a@example.test?attachment=file:///etc/passwd")]
    public void Attachment_parameters_are_ignored(string uri)
    {
        var link = MailtoLink.Parse(uri)!;
        Assert.Equal("a@example.test", Assert.Single(link.To).Address);
        Assert.Empty(link.Body);
    }

    [Fact]
    public void Subjects_are_one_line_and_lengths_are_capped()
    {
        var link = MailtoLink.Parse("mailto:a@example.test?subject=Hi%0D%0ABcc:%20x@evil.test&body=" + new string('x', MailtoLink.MaxBodyLength + 10))!;
        Assert.DoesNotContain('\n', link.Subject);
        Assert.Empty(link.Bcc);
        Assert.Equal(MailtoLink.MaxBodyLength, link.Body.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.test")]
    [InlineData("javascript:alert(1)")]
    public void Anything_else_is_not_a_mailto_link(string? text) => Assert.Null(MailtoLink.Parse(text));

    [Fact]
    public void Invalid_addresses_are_dropped()
    {
        var link = MailtoLink.Parse("mailto:not-an-address?to=ok@example.test")!;
        Assert.Equal("ok@example.test", Assert.Single(link.To).Address);
    }
}

public sealed class PortableDataImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sp-import-" + Guid.NewGuid().ToString("N"));
    private string Portable => Path.Combine(_root, "portable");
    private string Package => Path.Combine(_root, "package");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task SeedPortableAsync()
    {
        Directory.CreateDirectory(Path.Combine(Portable, "Secrets"));
        Directory.CreateDirectory(Path.Combine(Portable, "Logs"));
        Directory.CreateDirectory(Path.Combine(Portable, "WebView2", "Default"));
        Directory.CreateDirectory(Path.Combine(Portable, "Outbox", "abc"));
        await File.WriteAllTextAsync(Path.Combine(Portable, "settings.json"), "{ \"accentColor\": \"#123456\" }");
        await File.WriteAllTextAsync(Path.Combine(Portable, "Secrets", "s1.secret"), "account/1/password\nAAAA");
        await File.WriteAllTextAsync(Path.Combine(Portable, "Logs", "app.log"), "log");
        await File.WriteAllTextAsync(Path.Combine(Portable, "WebView2", "Default", "cache"), "cache");
        await File.WriteAllTextAsync(Path.Combine(Portable, "Outbox", "abc", "000-file.txt"), "attachment");

        var db = new MailDatabase(Path.Combine(Portable, "mail-cache.db"));
        await db.MigrateAsync();
        await new MailCache(db).AddAccountAsync(Mail.MailCacheTests.Account(Guid.NewGuid(), "kept@example.test"));
    }

    [Fact]
    public async Task Copies_accounts_settings_secrets_and_outbox_but_not_logs_or_caches()
    {
        await SeedPortableAsync();
        SqliteConnection.ClearAllPools(); // the portable build has closed
        var before = Directory.GetFiles(Portable, "*", SearchOption.AllDirectories).Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList();

        var result = PortableDataImporter.ImportIfNeeded(Portable, Package);

        Assert.Equal(DataImportOutcome.Imported, result.Outcome);
        Assert.Contains("#123456", await File.ReadAllTextAsync(Path.Combine(Package, "settings.json")));
        Assert.True(File.Exists(Path.Combine(Package, "Secrets", "s1.secret")));
        Assert.True(File.Exists(Path.Combine(Package, "Outbox", "abc", "000-file.txt")));
        Assert.False(Directory.Exists(Path.Combine(Package, "Logs")));
        Assert.False(Directory.Exists(Path.Combine(Package, "WebView2")));
        Assert.False(Directory.Exists(Path.Combine(Package, ".import-staging")));
        Assert.True(File.Exists(Path.Combine(Package, PortableDataImporter.MarkerFileName)));

        var copied = new MailCache(new MailDatabase(Path.Combine(Package, "mail-cache.db")));
        Assert.Equal("kept@example.test", Assert.Single(await copied.GetAccountsAsync()).Account.EmailAddress);

        // The portable folder is untouched (nothing moved, deleted or rewritten).
        foreach (var (file, written) in before)
        {
            Assert.True(File.Exists(file), file);
            if (!file.EndsWith("-shm", StringComparison.Ordinal) && !file.EndsWith("-wal", StringComparison.Ordinal))
                Assert.Equal(written, File.GetLastWriteTimeUtc(file));
        }
    }

    [Fact]
    public async Task Never_overwrites_data_the_installed_app_already_has()
    {
        await SeedPortableAsync();
        Directory.CreateDirectory(Package);
        await File.WriteAllTextAsync(Path.Combine(Package, "settings.json"), "{ \"accentColor\": \"#ABCDEF\" }");

        Assert.Equal(DataImportOutcome.NothingToImport, PortableDataImporter.ImportIfNeeded(Portable, Package).Outcome);
        Assert.Contains("#ABCDEF", await File.ReadAllTextAsync(Path.Combine(Package, "settings.json")));
        Assert.False(File.Exists(Path.Combine(Package, "mail-cache.db")));
    }

    [Fact]
    public async Task Runs_only_once()
    {
        await SeedPortableAsync();
        Assert.Equal(DataImportOutcome.Imported, PortableDataImporter.ImportIfNeeded(Portable, Package).Outcome);

        // The user later resets the installed app's data: the import doesn't come back on its own.
        File.Delete(Path.Combine(Package, "settings.json"));
        SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(Package, "mail-cache.db"));
        Assert.Equal(DataImportOutcome.NothingToImport, PortableDataImporter.ImportIfNeeded(Portable, Package).Outcome);
    }

    [Fact]
    public void Nothing_to_import_without_portable_data()
    {
        Directory.CreateDirectory(Portable);
        Assert.Equal(DataImportOutcome.NothingToImport, PortableDataImporter.ImportIfNeeded(Portable, Package).Outcome);
        Assert.Equal(DataImportOutcome.NothingToImport, PortableDataImporter.ImportIfNeeded(Package, Package).Outcome);
    }

    [Fact]
    public async Task A_damaged_portable_database_fails_cleanly_and_changes_nothing()
    {
        Directory.CreateDirectory(Portable);
        await File.WriteAllTextAsync(Path.Combine(Portable, "settings.json"), "{}");
        await File.WriteAllBytesAsync(Path.Combine(Portable, "mail-cache.db"), Enumerable.Repeat((byte)0x33, 4096).ToArray());

        var result = PortableDataImporter.ImportIfNeeded(Portable, Package);

        Assert.Equal(DataImportOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(Path.Combine(Package, "settings.json")));
        Assert.False(File.Exists(Path.Combine(Package, PortableDataImporter.MarkerFileName)));
        Assert.False(Directory.Exists(Path.Combine(Package, ".import-staging")));
    }
}

/// <summary>Checks on the packaging and release files, so versions and identities can't drift apart.</summary>
public sealed class PackagingTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SidePhase.Mail.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static System.Xml.Linq.XDocument Manifest() =>
        System.Xml.Linq.XDocument.Load(Path.Combine(RepoRoot(), "src", "SidePhase.Mail.App", "Package.appxmanifest"));

    private static readonly System.Xml.Linq.XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

    [Fact]
    public void Package_version_matches_the_app_version()
    {
        var props = System.Xml.Linq.XDocument.Load(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var prefix = props.Descendants("VersionPrefix").Single().Value;
        var identity = Manifest().Root!.Element(Foundation + "Identity")!;

        Assert.Equal(prefix + ".0", identity.Attribute("Version")!.Value);
        Assert.Matches(@"^\d+\.\d+\.\d+$", prefix);
    }

    [Fact]
    public void Package_identity_is_marked_as_a_development_placeholder()
    {
        var identity = Manifest().Root!.Element(Foundation + "Identity")!;
        // A real publisher certificate subject must never be invented here: Partner Center supplies it.
        Assert.Contains("(development)", identity.Attribute("Publisher")!.Value);
        Assert.Equal("SidePhase Studio", Manifest().Root!.Element(Foundation + "Properties")!.Element(Foundation + "PublisherDisplayName")!.Value);
    }

    [Fact]
    public void Every_image_the_manifest_names_exists()
    {
        var appFolder = Path.Combine(RepoRoot(), "src", "SidePhase.Mail.App");
        var images = Manifest().Descendants()
            .SelectMany(e => e.Attributes().Where(a => a.Value.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).Select(a => a.Value)
                .Concat(e.HasElements || !e.Value.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? [] : [e.Value]))
            .Distinct()
            .ToList();

        Assert.NotEmpty(images);
        foreach (var image in images)
        {
            var path = Path.Combine(appFolder, image.Replace('\\', Path.DirectorySeparatorChar));
            var folder = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            Assert.True(Directory.GetFiles(folder, name + ".scale-*.png").Length > 0 || File.Exists(path), $"Missing image for {image}");
        }
    }

    [Fact]
    public void Only_the_capabilities_the_app_needs_are_declared()
    {
        var capabilities = Manifest().Descendants().Where(e => e.Name.LocalName == "Capability").Select(e => e.Attribute("Name")!.Value).Order();
        Assert.Equal(["internetClient", "runFullTrust"], capabilities);
    }
}

public sealed class SecurityHardeningTests
{
    [Theory]
    [InlineData("<lin<link>k rel=preconnect href=https://t.example/x>")]
    [InlineData("<scr<script>ipt>alert(1)</script>")]
    [InlineData("<me<meta>ta http-equiv=refresh content='0;url=https://t.example'>")]
    [InlineData("<svg><svg>x</svg></svg>")]
    public void Nested_tags_cannot_reassemble_after_sanitising(string html)
    {
        var clean = Core.Rendering.HtmlEmailDocumentBuilder.Sanitize(html);
        Assert.DoesNotMatch(@"<\s*(link|script|meta|svg)\b", clean.ToLowerInvariant());
    }

    [Fact]
    public void Event_handlers_after_a_slash_are_removed()
    {
        var clean = Core.Rendering.HtmlEmailDocumentBuilder.Sanitize("<img/onerror=alert(1) src=x>");
        Assert.DoesNotContain("onerror", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("invoice.exe\u3000")]
    [InlineData("invoice.exe . .")]
    [InlineData("report\u202Efdp.exe")]
    [InlineData("readme.html")]
    [InlineData("quarter.one")]
    [InlineData("remote.rdp")]
    public void Disguised_and_risky_names_are_flagged(string name)
    {
        Assert.True(Core.Formatting.AttachmentSafety.IsDangerous(name), name);
    }

    [Theory]
    [InlineData(0x2028)]
    [InlineData(0x0085)]
    [InlineData(0x00A0)]
    public void Trailing_unicode_whitespace_cannot_hide_an_extension(int whitespace)
    {
        Assert.True(Core.Formatting.AttachmentSafety.IsDangerous("invoice.exe" + (char)whitespace));
    }

    [Fact]
    public void Direction_tricks_count_as_misleading()
    {
        Assert.True(Core.Formatting.AttachmentSafety.HasMisleadingName("report\u202Efdp.exe"));
        Assert.DoesNotContain('\u202E', Core.Formatting.AttachmentSafety.SafeFileName("report\u202Efdp.exe"));
    }

    [Theory]
    [InlineData("nul.tar.gz")]
    [InlineData("aux.foo.txt")]
    [InlineData("CON .txt")]
    [InlineData("com0.log")]
    [InlineData("CONOUT$.txt")]
    public void Reserved_device_names_are_renamed_whatever_follows(string name)
    {
        Assert.StartsWith("_", Core.Formatting.AttachmentSafety.SafeFileName(name));
    }

    [Theory]
    [InlineData("ceo@corp.example", "ceo@corp.example", null, true)]
    [InlineData("ceo@corp.example", "CEO@Corp.Example", null, true)]
    [InlineData("ceo@corp.example", "ceo@corp.example.evil.example", null, false)]
    [InlineData("ceo@corp.example", "x@evil.example", "ceo@corp.example", false)]
    [InlineData("@corp.example", "anyone@mail.corp.example", null, true)]
    [InlineData("@corp.example", "anyone@corp.example.evil.example", null, false)]
    [InlineData("newsletter", "newsletter@shop.example", null, true)]
    public void Sender_rules_need_the_real_address(string value, string address, string? displayName, bool expected)
    {
        var message = Samples.Message(from: displayName is null ? address : $"\"{displayName}\" <{address}>");
        var condition = new Core.Organisation.RuleCondition(Core.Organisation.RuleField.Sender, value);
        Assert.Equal(expected, Core.Organisation.MailRuleConditions.Matches(condition, message));
    }
}

public sealed class RemoteContentPolicyTests
{
    [Theory]
    [InlineData("https://images.example.com/logo.png", true)]
    [InlineData("http://cdn.example.org/a.gif", true)]
    [InlineData("https://8.8.8.8/x.png", true)]
    [InlineData("http://localhost/x.png", false)]
    [InlineData("http://127.0.0.1:8080/x.png", false)]
    [InlineData("http://[::1]/x.png", false)]
    [InlineData("http://192.168.1.1/cgi-bin/reboot", false)]
    [InlineData("http://10.0.0.5/x.png", false)]
    [InlineData("http://172.20.1.1/x.png", false)]
    [InlineData("http://169.254.169.254/latest/meta-data/", false)]
    [InlineData("http://[fe80::1]/x.png", false)]
    [InlineData("http://[fd00::1]/x.png", false)]
    [InlineData("http://[::ffff:192.168.0.1]/x.png", false)]
    [InlineData("http://printer.local/x.png", false)]
    [InlineData("http://router/x.png", false)]
    [InlineData("http://nas.home.arpa/x.png", false)]
    [InlineData("https://user:pw@example.com/x.png", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("ftp://example.com/x.png", false)]
    public void Only_public_web_images_are_allowed(string url, bool allowed) =>
        Assert.Equal(allowed, Core.Rendering.RemoteContentPolicy.IsAllowedImageUrl(url));
}

public sealed class ProviderChoiceTests
{
    private static Core.Abstractions.AccountDiscoveryResult Discovered(string email, Core.Models.MailProviderKind provider, Core.Models.AuthMethod auth) => new()
    {
        EmailAddress = email,
        Provider = provider,
        Source = Core.Abstractions.DiscoverySource.Guess,
        Settings = new Core.Models.AccountConnectionSettings
        {
            Imap = new("imap.company.example", 993, Core.Models.ConnectionSecurity.SslOnConnect),
            Smtp = new("smtp.company.example", 465, Core.Models.ConnectionSecurity.SslOnConnect),
            Username = email,
            AuthMethod = auth,
        },
    };

    [Fact]
    public void Choosing_google_for_a_company_domain_signs_in_with_google()
    {
        var result = Infrastructure.Accounts.AccountDiscovery.ForChoice(Infrastructure.Accounts.ProviderChoice.Google,
            Discovered("ana@company.example", Core.Models.MailProviderKind.Imap, Core.Models.AuthMethod.Password));

        Assert.Equal(Core.Models.AuthMethod.OAuthGoogle, result.Settings!.AuthMethod);
        Assert.Equal("imap.gmail.com", result.Settings.Imap.Host);
        Assert.Equal(Core.Models.MailProviderKind.Gmail, result.Provider);
        Assert.Equal("ana@company.example", result.Settings.Username);
    }

    [Fact]
    public void Choosing_microsoft_for_a_company_domain_signs_in_with_microsoft_365()
    {
        var result = Infrastructure.Accounts.AccountDiscovery.ForChoice(Infrastructure.Accounts.ProviderChoice.Microsoft,
            Discovered("ana@company.example", Core.Models.MailProviderKind.Imap, Core.Models.AuthMethod.Password));

        Assert.Equal(Core.Models.AuthMethod.OAuthMicrosoft, result.Settings!.AuthMethod);
        Assert.Equal("outlook.office365.com", result.Settings.Imap.Host);
        Assert.Equal(Core.Models.MailProviderKind.Microsoft, result.Provider);
    }

    [Fact]
    public void Recognised_providers_and_other_keep_what_discovery_found()
    {
        var gmail = Discovered("ana@gmail.com", Core.Models.MailProviderKind.Gmail, Core.Models.AuthMethod.OAuthGoogle);
        Assert.Same(gmail, Infrastructure.Accounts.AccountDiscovery.ForChoice(Infrastructure.Accounts.ProviderChoice.Google, gmail));

        var other = Discovered("ana@company.example", Core.Models.MailProviderKind.Imap, Core.Models.AuthMethod.Password);
        Assert.Same(other, Infrastructure.Accounts.AccountDiscovery.ForChoice(Infrastructure.Accounts.ProviderChoice.Other, other));
    }
}

public sealed class ThunderbirdImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sp-tb-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string Prefs = """
        // Mozilla User Preferences
        user_pref("mail.accountmanager.accounts", "account1,account2,account3,account4");
        user_pref("mail.account.account1.identities", "id1");
        user_pref("mail.account.account1.server", "server1");
        user_pref("mail.server.server1.type", "imap");
        user_pref("mail.server.server1.hostname", "imap.example.test");
        user_pref("mail.server.server1.port", 993);
        user_pref("mail.server.server1.socketType", 3);
        user_pref("mail.server.server1.userName", "ana");
        user_pref("mail.identity.id1.useremail", "ana@example.test");
        user_pref("mail.identity.id1.fullName", "Ana \"Annie\" Lopes");
        user_pref("mail.identity.id1.smtpServer", "smtp1");
        user_pref("mail.smtpserver.smtp1.hostname", "smtp.example.test");
        user_pref("mail.smtpserver.smtp1.port", 587);
        user_pref("mail.smtpserver.smtp1.try_ssl", 2);
        user_pref("mail.account.account2.identities", "id2");
        user_pref("mail.account.account2.server", "server2");
        user_pref("mail.server.server2.type", "pop3");
        user_pref("mail.server.server2.hostname", "pop.example.test");
        user_pref("mail.identity.id2.useremail", "pop@example.test");
        user_pref("mail.account.account3.server", "server3");
        user_pref("mail.server.server3.type", "none");
        user_pref("mail.account.account4.identities", "id4");
        user_pref("mail.account.account4.server", "server4");
        user_pref("mail.server.server4.type", "imap");
        user_pref("mail.server.server4.hostname", "imap.plain.test");
        user_pref("mail.server.server4.socketType", 0);
        user_pref("mail.identity.id4.useremail", "plain@plain.test");
        user_pref("signon.rememberSignons", true);
        """;

    [Fact]
    public void Reads_encrypted_imap_accounts_with_their_outgoing_server()
    {
        var account = Assert.Single(Infrastructure.Accounts.ThunderbirdProfileReader.ParseAccounts(Prefs));

        Assert.Equal("ana@example.test", account.EmailAddress);
        Assert.Equal("Ana \"Annie\" Lopes", account.DisplayName);
        Assert.Equal(new Core.Models.ServerEndpoint("imap.example.test", 993, Core.Models.ConnectionSecurity.SslOnConnect), account.Imap);
        Assert.Equal(new Core.Models.ServerEndpoint("smtp.example.test", 587, Core.Models.ConnectionSecurity.StartTls), account.Smtp);
        Assert.Equal("ana", account.Username);
        Assert.False(account.UsesOAuth);
    }

    [Fact]
    public void Never_touches_password_or_key_files()
    {
        var profile = Path.Combine(_root, "Profiles", "abcd.default-release");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "prefs.js"), Prefs);
        foreach (var secret in new[] { "logins.json", "key4.db", "cert9.db" }) File.WriteAllText(Path.Combine(profile, secret), "secret");
        var before = Directory.GetFiles(profile).ToDictionary(f => f, File.GetLastAccessTimeUtc);

        var accounts = Infrastructure.Accounts.ThunderbirdProfileReader.ReadAccounts(profile);

        Assert.Single(accounts);
        // The reader opens only prefs.js (checked by reading the source of truth: the file list is unchanged and the
        // secret files still hold exactly what they held).
        foreach (var secret in new[] { "logins.json", "key4.db", "cert9.db" }) Assert.Equal("secret", File.ReadAllText(Path.Combine(profile, secret)));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(profile).Order());
    }

    [Fact]
    public void Finds_profiles_from_profiles_ini()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Profiles", "abcd.default-release"));
        File.WriteAllText(Path.Combine(_root, "profiles.ini"), """
            [Profile0]
            Name=default-release
            IsRelative=1
            Path=Profiles/abcd.default-release
            Default=1

            [Profile1]
            Name=missing
            IsRelative=1
            Path=Profiles/gone
            """);

        var profile = Assert.Single(Infrastructure.Accounts.ThunderbirdProfileReader.FindProfiles(_root));
        Assert.Equal("default-release", profile.Name);
        Assert.True(profile.IsDefault);
    }

    [Fact]
    public void A_folder_that_isnt_a_profile_is_reported() =>
        Assert.Throws<FileNotFoundException>(() => Infrastructure.Accounts.ThunderbirdProfileReader.ReadAccounts(Path.GetTempPath()));
}

public sealed class LogRedactionAdditionsTests
{
    [Theory]
    [InlineData("C: A1 AUTHENTICATE PLAIN AGFsaWNlAHNlY3JldA==")]
    [InlineData("AUTH LOGIN c2VjcmV0cGFzc3dvcmQ=")]
    public void Sasl_payloads_are_redacted(string line)
    {
        var redacted = Core.Formatting.LogRedactor.Redact(line);
        Assert.Contains("[redacted]", redacted);
        Assert.DoesNotContain("AGFsaWNlAHNlY3JldA", redacted);
        Assert.DoesNotContain("c2VjcmV0cGFzc3dvcmQ", redacted);
    }
}
