/// <summary>
/// LoopbackRedirectUri
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;

    /// <summary>
    /// The only client redirect URIs the OAuth broker accepts: <c>http</c> to <c>127.0.0.1</c>, <c>localhost</c> or
    /// <c>[::1]</c>, any port and path, no user-info, no fragment, at most <see cref="MaxLength"/> characters. A code
    /// can therefore only reach the signing-in user's own machine, so the broker cannot act as an open redirect.
    /// </summary>
    public static class LoopbackRedirectUri
    {
        public const int MaxLength = 2048;

        private static readonly string[] AllowedPrefixes = { "http://127.0.0.1", "http://localhost", "http://[::1]" };

        private static readonly string[] AllowedHosts = { "127.0.0.1", "localhost", "[::1]" };

        public static bool IsValid([NotNullWhen(true)] string? value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
            {
                return false;
            }

            if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\' || c == '#'))
            {
                return false;
            }

            // The host must end right after the prefix, so "localhost.evil.com" and "127.0.0.1.nip.io" fail here.
            var prefix = AllowedPrefixes.FirstOrDefault(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (prefix is null || (value.Length > prefix.Length && value[prefix.Length] is not (':' or '/' or '?')))
            {
                return false;
            }

            return Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttp
                && uri.UserInfo.Length == 0
                && AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
        }
    }
}
