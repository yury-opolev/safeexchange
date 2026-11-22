/// <summary>
/// ClientAddress
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Linq;
    using System.Net;

    /// <summary>
    /// The client address for rate limiting: the last <c>X-Forwarded-For</c> entry, which the App Service front end
    /// appends (<c>ip:port</c> or <c>[ipv6]:port</c>); the entries before it are whatever the client sent. Null when the
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

            return IPAddress.TryParse(entry, out var address) ? address.ToString() : null;
        }
    }
}
