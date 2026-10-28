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
