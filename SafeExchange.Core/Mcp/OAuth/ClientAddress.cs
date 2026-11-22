/// <summary>
/// ClientAddress
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;

    /// <summary>
    /// The client address for rate limiting: the last <c>X-Forwarded-For</c> entry, which the App Service front end
    /// appends (<c>ip:port</c> or <c>[ipv6]:port</c>); the entries before it are whatever the client sent. An IPv4 address
    /// is its own key, an IPv4-mapped IPv6 address (<c>::ffff:a.b.c.d</c>) is that IPv4 address, and any other IPv6
    /// address is keyed by its /64 prefix (<c>2001:db8:1:2::/64</c>), since one host controls a whole /64. Null when the
    /// last entry is missing or not an IP address.
    /// </summary>
    public static class ClientAddress
    {
        public static string? FromForwardedFor(string? forwardedFor)
        {
            var entry = (forwardedFor ?? string.Empty).Split(',').Last().Trim();
            if (entry.StartsWith('['))
            {
                var end = entry.IndexOf(']');
                entry = end > 0 ? entry[1..end] : string.Empty;
            }
            else if (entry.Count(c => c == ':') == 1)
            {
                entry = entry[..entry.IndexOf(':')];
            }

            if (!IPAddress.TryParse(entry, out var address))
            {
                return null;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                return address.MapToIPv4().ToString();
            }

            if (address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return address.ToString();
            }

            var bytes = address.GetAddressBytes();
            return string.Create(CultureInfo.InvariantCulture, $"{(bytes[0] << 8) | bytes[1]:x}:{(bytes[2] << 8) | bytes[3]:x}:{(bytes[4] << 8) | bytes[5]:x}:{(bytes[6] << 8) | bytes[7]:x}::/64");
        }
    }
}
