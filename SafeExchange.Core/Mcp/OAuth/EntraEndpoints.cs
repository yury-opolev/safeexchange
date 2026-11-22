/// <summary>
/// EntraEndpoints
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;

    /// <summary>
    /// Entra ID v2 authorize and token endpoints derived from <c>Authentication:Authority</c> (trailing <c>/</c> and
    /// <c>/v2.0</c> removed, then <c>/oauth2/v2.0/authorize|token</c> appended). Only a plain https authority
    /// qualifies; nothing a caller sends influences these URLs.
    /// </summary>
    public sealed record EntraEndpoints(string Authorize, string Token)
    {
        private const string V2Suffix = "/v2.0";

        /// <summary><c>scheme://host[:port]</c> of the authorize endpoint (the default port is omitted).</summary>
        public string AuthorizeOrigin => new Uri(this.Authorize).GetLeftPart(UriPartial.Authority);

        public static EntraEndpoints? FromAuthority(string? authority)
        {
            var value = (authority ?? string.Empty).Trim().TrimEnd('/');
            if (value.EndsWith(V2Suffix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^V2Suffix.Length].TrimEnd('/');
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || uri.UserInfo.Length > 0
                || uri.Query.Length > 0
                || uri.Fragment.Length > 0)
            {
                return null;
            }

            return new EntraEndpoints(value + "/oauth2/v2.0/authorize", value + "/oauth2/v2.0/token");
        }
    }
}
