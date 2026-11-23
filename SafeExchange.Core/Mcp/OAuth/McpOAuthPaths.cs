/// <summary>
/// McpOAuthPaths
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;

    /// <summary>
    /// Paths of the MCP endpoint and the OAuth broker relative to the API base (<c>https://host/api/</c>, which must
    /// end with <c>/</c>). The broker's issuer is the API base without the trailing slash.
    /// </summary>
    public static class McpOAuthPaths
    {
        public const string Mcp = "v1/mcp";

        public const string AuthorizationServerMetadata = ".well-known/oauth-authorization-server";

        public const string OpenIdConfiguration = ".well-known/openid-configuration";

        public const string Register = "oauth/register";

        public const string Authorize = "oauth/authorize";

        public const string Callback = "oauth/callback";

        public const string Token = "oauth/token";

        public static string Issuer(Uri apiBaseUri)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            return apiBaseUri.AbsoluteUri.TrimEnd('/');
        }

        public static string Endpoint(Uri apiBaseUri, string path)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            return new Uri(apiBaseUri, path).AbsoluteUri;
        }
    }
}
