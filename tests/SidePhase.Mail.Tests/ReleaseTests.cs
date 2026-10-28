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
