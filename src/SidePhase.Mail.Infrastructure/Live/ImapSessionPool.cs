using System.Collections.Concurrent;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using SidePhase.Mail.Infrastructure.Accounts;
using SidePhase.Mail.Infrastructure.Mail;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Infrastructure.Live;

/// <summary>
/// One authenticated IMAP connection per account, used by one operation at a time. Dropped connections are
/// re-established and the operation retried once; an expired OAuth access token is refreshed and retried once.
/// </summary>
public sealed class ImapSessionPool(MailConnector connector, CredentialProvider credentials) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    public async Task<T> RunAsync<T>(StoredAccount account, Func<ImapClient, Task<T>> operation, CancellationToken ct)
    {
        var session = _sessions.GetOrAdd(account.Account.Id, _ => new Session());
        await session.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var refreshedToken = false;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var client = await EnsureConnectedAsync(session, account, forceTokenRefresh: false, ct).ConfigureAwait(false);
                    return await operation(client).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt == 0 && IsConnectionProblem(ex) && !ct.IsCancellationRequested)
                {
                    session.Drop();
                }
                catch (AuthenticationException) when (!refreshedToken && account.Settings.AuthMethod != Core.Models.AuthMethod.Password)
                {
                    refreshedToken = true;
                    session.Drop();
                    await EnsureConnectedAsync(session, account, forceTokenRefresh: true, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public Task RunAsync(StoredAccount account, Func<ImapClient, Task> operation, CancellationToken ct) =>
        RunAsync<bool>(account, async client =>
        {
            await operation(client).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>Closes and forgets the account's connection (account removed or credentials changed).</summary>
    public async Task CloseAsync(Guid accountId)
    {
        if (!_sessions.TryRemove(accountId, out var session)) return;
        await session.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await session.DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList()) await CloseAsync(id).ConfigureAwait(false);
    }

    private async Task<ImapClient> EnsureConnectedAsync(Session session, StoredAccount account, bool forceTokenRefresh, CancellationToken ct)
    {
        if (session.Client is { IsConnected: true, IsAuthenticated: true } client) return client;
        session.Drop();
        var credential = await credentials.GetAsync(account.Account.Id, account.Settings, forceTokenRefresh, ct).ConfigureAwait(false);
        session.Client = await connector.ConnectImapAsync(account.Settings.Imap, credential, ct).ConfigureAwait(false);
        return session.Client;
    }

    internal static bool IsConnectionProblem(Exception ex) =>
        ex is ServiceNotConnectedException or ServiceNotAuthenticatedException or ImapProtocolException or IOException or SocketException
            || (ex is InvalidOperationException && ex.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase));

    private sealed class Session
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ImapClient? Client { get; set; }

        public void Drop()
        {
            var client = Client;
            Client = null;
            client?.Dispose();
        }

        public async Task DisconnectAsync()
        {
            var client = Client;
            Client = null;
            if (client is null) return;
            try
            {
                if (client.IsConnected) await client.DisconnectAsync(true).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: the connection is going away regardless.
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}
