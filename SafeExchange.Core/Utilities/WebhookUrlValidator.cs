namespace SafeExchange.Core.Utilities
{
    using System;
    using System.Net;
    using System.Net.Sockets;

    /// <summary>
    /// Validates webhook target URLs to prevent Server-Side Request Forgery (CWE-918).
    /// A URL is only allowed when it is a well-formed absolute HTTPS URL whose host does
    /// not resolve to a loopback, link-local (incl. cloud metadata 169.254.169.254),
    /// private (RFC1918 / ULA fc00::/7), or otherwise reserved/internal address.
    /// Both literal IP hosts and hostnames (which are DNS-resolved) are checked.
    /// </summary>
    public static class WebhookUrlValidator
    {
        /// <summary>
        /// Validates a webhook URL string. When DNS resolution is required but not
        /// available (e.g. transient failure), the URL is rejected (fail-closed).
        /// </summary>
        public static bool TryValidate(string? url, out string? reason)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                reason = "Webhook URL is required.";
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                reason = "Webhook URL is not a valid absolute URL.";
                return false;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                reason = "Webhook URL must use the 'https' scheme.";
                return false;
            }

            IPAddress[] addresses;
            try
            {
                addresses = ResolveHost(uri, out var reasonInner);
                if (addresses.Length == 0)
                {
                    reason = reasonInner ?? "Webhook URL host could not be resolved.";
                    return false;
                }
            }
            catch (Exception)
            {
                reason = "Webhook URL host could not be resolved.";
                return false;
            }

            foreach (var address in addresses)
            {
                if (IsDisallowedAddress(address))
                {
                    reason = "Webhook URL resolves to a disallowed (loopback, link-local, or private) address.";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Convenience overload operating on an already-parsed <see cref="Uri"/>.
        /// </summary>
        public static bool IsAllowed(Uri? uri)
            => uri is not null && TryValidate(uri.AbsoluteUri, out _);

        private static IPAddress[] ResolveHost(Uri uri, out string? reason)
        {
            reason = null;

            if (uri.HostNameType == UriHostNameType.IPv4 || uri.HostNameType == UriHostNameType.IPv6)
            {
                if (IPAddress.TryParse(uri.Host, out var literal))
                {
                    return new[] { literal };
                }

                reason = "Webhook URL host is not a valid IP address.";
                return Array.Empty<IPAddress>();
            }

            // Hostname: resolve via DNS so that names pointing at internal IPs are caught.
            return Dns.GetHostAddresses(uri.DnsSafeHost);
        }

        private static bool IsDisallowedAddress(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return IsDisallowedIPv4(address);
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return IsDisallowedIPv6(address);
            }

            // Unknown address families are treated as unsafe.
            return true;
        }

        private static bool IsDisallowedIPv4(IPAddress address)
        {
            var bytes = address.GetAddressBytes();

            // 0.0.0.0/8 "this network"
            if (bytes[0] == 0)
            {
                return true;
            }

            // 10.0.0.0/8 (RFC1918)
            if (bytes[0] == 10)
            {
                return true;
            }

            // 127.0.0.0/8 loopback (covered by IsLoopback but keep explicit)
            if (bytes[0] == 127)
            {
                return true;
            }

            // 100.64.0.0/10 CGNAT
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
            {
                return true;
            }

            // 169.254.0.0/16 link-local (incl. 169.254.169.254 cloud metadata)
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }

            // 172.16.0.0/12 (RFC1918)
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return true;
            }

            // 192.0.0.0/24 IETF protocol assignments and 192.0.2.0/24 TEST-NET-1
            if (bytes[0] == 192 && bytes[1] == 0 && (bytes[2] == 0 || bytes[2] == 2))
            {
                return true;
            }

            // 192.168.0.0/16 (RFC1918)
            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }

            // 198.18.0.0/15 benchmarking
            if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
            {
                return true;
            }

            // 224.0.0.0/4 multicast and 240.0.0.0/4 reserved
            if (bytes[0] >= 224)
            {
                return true;
            }

            return false;
        }

        private static bool IsDisallowedIPv6(IPAddress address)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {
                return true;
            }

            // ::1 loopback handled by IsLoopback; :: unspecified
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback))
            {
                return true;
            }

            var bytes = address.GetAddressBytes();

            // fc00::/7 unique local addresses (ULA)
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return true;
            }

            return false;
        }
    }
}
