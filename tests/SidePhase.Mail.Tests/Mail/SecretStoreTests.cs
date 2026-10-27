using SidePhase.Mail.Infrastructure.Security;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Tests.Mail;

/// <summary>Reversible stand-in for DPAPI (which only exists on Windows).</summary>
internal sealed class XorProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x5A)).Reverse().ToArray();

    public byte[] Unprotect(byte[] protectedData) => protectedData.Reverse().Select(b => (byte)(b ^ 0x5A)).ToArray();
}

public sealed class SecretStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sp-secrets-" + Guid.NewGuid().ToString("N"));
    private readonly FileSecretStore _store;

    public SecretStoreTests() => _store = new FileSecretStore(new AppDataPaths(_root), new XorProtector());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Secrets_round_trip_and_are_not_written_in_plain_text()
    {
        var account = Guid.NewGuid();
        await _store.SetAsync(SecretKeys.Password(account), "hunter2-very-secret");
        Assert.Equal("hunter2-very-secret", await _store.GetAsync(SecretKeys.Password(account)));

        foreach (var file in Directory.GetFiles(Path.Combine(_root, "Secrets")))
        {
            Assert.DoesNotContain("hunter2-very-secret", await File.ReadAllTextAsync(file));
        }
    }

    [Fact]
    public async Task Windows_data_protection_round_trips_and_encrypts()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI exists only on Windows (covered by the Windows CI job)

        var store = new FileSecretStore(new AppDataPaths(_root), new DpapiSecretProtector());
        await store.SetAsync("dpapi-test", "correct horse battery staple");
        Assert.Equal("correct horse battery staple", await store.GetAsync("dpapi-test"));
        foreach (var file in Directory.GetFiles(Path.Combine(_root, "Secrets")))
        {
            Assert.DoesNotContain("correct horse", await File.ReadAllTextAsync(file));
        }
    }

    [Fact]
    public async Task Missing_secret_is_null_and_overwrite_replaces()
    {
        Assert.Null(await _store.GetAsync("nope"));
        await _store.SetAsync("k", "one");
        await _store.SetAsync("k", "two");
        Assert.Equal("two", await _store.GetAsync("k"));
    }

    [Fact]
    public async Task Removing_an_account_deletes_only_its_secrets()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await _store.SetAsync(SecretKeys.Password(a), "a-pass");
        await _store.SetAsync(SecretKeys.OAuthTokens(a), "a-token");
        await _store.SetAsync(SecretKeys.Password(b), "b-pass");

        await _store.DeletePrefixAsync(SecretKeys.AccountPrefix(a));
        Assert.Null(await _store.GetAsync(SecretKeys.Password(a)));
        Assert.Null(await _store.GetAsync(SecretKeys.OAuthTokens(a)));
        Assert.Equal("b-pass", await _store.GetAsync(SecretKeys.Password(b)));

        await _store.DeleteAsync(SecretKeys.Password(b));
        Assert.Null(await _store.GetAsync(SecretKeys.Password(b)));
    }

    [Fact]
    public async Task Corrupted_secret_reads_as_missing()
    {
        await _store.SetAsync("k", "value");
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, "Secrets")));
        var lines = await File.ReadAllLinesAsync(file);
        await File.WriteAllTextAsync(file, lines[0] + "\n" + "not-base64!!");
        Assert.Null(await _store.GetAsync("k"));
    }
}
