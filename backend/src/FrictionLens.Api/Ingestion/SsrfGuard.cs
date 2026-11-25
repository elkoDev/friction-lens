using System.Net;
using System.Net.Sockets;

namespace FrictionLens.Api.Ingestion;

/// <summary>
/// Validates user-supplied external URLs (e.g., Discourse forum targets) against SSRF attacks:
/// requires HTTPS and rejects loopback, RFC1918 private, link-local (169.254.169.254), and IPv6 ULA addresses.
/// </summary>
public static class SsrfGuard
{
    public static async Task<(bool Allowed, string? Reason, Uri? NormalizedUri)> ValidateExternalUrlAsync(
        string rawUrl,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(rawUrl?.Trim(), UriKind.Absolute, out var uri))
        {
            return (false, "Invalid absolute URL.", null);
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Only HTTPS URLs are permitted for external connectors.", null);
        }

        var host = uri.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(host)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Host '{host}' is blocked by SSRF policy.", null);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            if (addresses.Length == 0)
            {
                return (false, $"DNS resolution returned no addresses for '{host}'.", null);
            }

            foreach (var ip in addresses)
            {
                if (IsPrivateOrReserved(ip))
                {
                    return (false, $"Resolved IP '{ip}' for host '{host}' is private or reserved.", null);
                }
            }
        }
        catch (SocketException ex)
        {
            return (false, $"DNS resolution failed for '{host}': {ex.Message}", null);
        }

        return (true, null, uri);
    }

    public static bool IsPrivateOrReserved(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] switch
            {
                0 => true,                                            // 0.0.0.0/8
                10 => true,                                           // 10.0.0.0/8 (RFC1918)
                100 when bytes[1] >= 64 && bytes[1] <= 127 => true,   // 100.64.0.0/10 (CGNAT)
                127 => true,                                          // 127.0.0.0/8 (loopback)
                169 when bytes[1] == 254 => true,                     // 169.254.0.0/16 (link-local / cloud metadata)
                172 when bytes[1] >= 16 && bytes[1] <= 31 => true,    // 172.16.0.0/12 (RFC1918)
                192 when bytes[1] == 168 => true,                     // 192.168.0.0/16 (RFC1918)
                _ => false
            };
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            if (ip.IsIPv4MappedToIPv6) return IsPrivateOrReserved(ip.MapToIPv4());
            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true; // fc00::/7 Unique Local Address
        }

        return false;
    }
}
