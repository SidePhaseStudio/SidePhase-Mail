using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SidePhase.Mail.Infrastructure.OAuth;

/// <summary>
/// Receives the OAuth redirect on a random loopback port (RFC 8252 §7.3). Listens on both IPv4 and IPv6 loopback so
/// "localhost" redirects work whichever address the browser picks. Only the single redirect request is accepted.
/// </summary>
public sealed class LoopbackRedirectListener : IDisposable
{
    private readonly TcpListener _v4;
    private readonly TcpListener? _v6;

    public LoopbackRedirectListener()
    {
        _v4 = new TcpListener(IPAddress.Loopback, 0);
        _v4.Start();
        Port = ((IPEndPoint)_v4.LocalEndpoint).Port;
        try
        {
            _v6 = new TcpListener(IPAddress.IPv6Loopback, Port);
            _v6.Start();
        }
        catch (SocketException)
        {
            _v6 = null; // IPv6 unavailable or port taken: IPv4 alone is fine for 127.0.0.1 redirects.
        }
    }

    public int Port { get; }

    public string RedirectUri(string host) => $"http://{host}:{Port}/";

    /// <summary>Waits for the browser to arrive at the redirect URI and returns its query parameters.</summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var accepts = new List<Task<TcpClient>> { _v4.AcceptTcpClientAsync(cancellationToken).AsTask() };
            if (_v6 is not null) accepts.Add(_v6.AcceptTcpClientAsync(cancellationToken).AsTask());
            var accepted = await Task.WhenAny(accepts).ConfigureAwait(false);
            using var client = await accepted.ConfigureAwait(false);
            var stream = client.GetStream();

            var requestLine = await ReadRequestLineAsync(stream, cancellationToken).ConfigureAwait(false);
            var parts = requestLine.Split(' ');
            if (parts.Length < 2 || parts[0] != "GET" || !parts[1].StartsWith('/'))
            {
                await RespondAsync(stream, 400, "Bad request", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var target = parts[1];
            var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : string.Empty;
            var values = ParseQuery(query);
            if (values.Count == 0)
            {
                // e.g. a favicon request; keep waiting for the real redirect.
                await RespondAsync(stream, 404, "Not found", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var ok = !values.ContainsKey("error");
            await RespondAsync(stream, 200, ok
                ? "You're signed in. You can close this tab and return to SidePhase Mail."
                : "Sign-in was cancelled or failed. You can close this tab and return to SidePhase Mail.", cancellationToken).ConfigureAwait(false);
            return values;
        }
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var end = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (end >= 0) return text[..end];
        }

        return Encoding.ASCII.GetString(buffer, 0, total);
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string message, CancellationToken ct)
    {
        var html =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>SidePhase Mail</title></head>" +
            "<body style=\"font-family:'Segoe UI',sans-serif;background:#0b0d12;color:#f5f6fa;display:flex;align-items:center;" +
            "justify-content:center;height:100vh;margin:0\"><div style=\"text-align:center\"><h2 style=\"font-weight:600\">SidePhase Mail</h2>" +
            $"<p style=\"color:#9da5b4\">{WebUtility.HtmlEncode(message)}</p></div></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _v4.Stop();
        _v6?.Stop();
    }
}
