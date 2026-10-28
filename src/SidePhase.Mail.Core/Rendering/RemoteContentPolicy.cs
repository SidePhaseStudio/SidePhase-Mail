using System.Net;
using System.Net.Sockets;

namespace SidePhase.Mail.Core.Rendering;

/// <summary>
/// Which remote images may load once the user chooses "Show images". Only public web addresses: never this PC, the
/// local network or link-local addresses, so an email can't use image requests to poke at a router, printer or other
/// local service (or the cloud metadata address). Names that only exist on local networks (.local, .lan, single-label
/// names) are refused too. This can't stop a public name that resolves to a private address; WebView2's own
/// private-network protections apply to those.
/// </summary>
public static class RemoteContentPolicy
{
    private static readonly string[] LocalSuffixes = [".localhost", ".local", ".lan", ".home", ".internal", ".intranet", ".corp", ".home.arpa"];

    public static bool IsAllowedImageUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("https" or "http")) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return false;

        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return IsPublic(ip);
        if (host == "localhost" || LocalSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal))) return false;
        return host.Contains('.'); // single-label names are local-network names
    }

    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)                // link-local, cloud metadata
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || b[0] >= 224);                                // multicast and reserved
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC);                     // unique local fc00::/7
        }

        return false;
    }
}
