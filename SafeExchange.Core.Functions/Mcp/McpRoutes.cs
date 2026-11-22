/// <summary>
/// McpRoutes
/// </summary>

namespace SafeExchange.Core.Functions.Mcp
{
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Routes and function names of the MCP triggers. Azure Functions serves HTTP triggers under the
    /// <c>api</c> route prefix (host.json sets no other), so the metadata document lives at
    /// <c>/api/.well-known/oauth-protected-resource</c>; the <c>WWW-Authenticate</c> challenge points there.
    /// The OAuth broker's issuer is <c>https://host/api</c>, so its metadata is served at
    /// <c>/api/.well-known/openid-configuration</c> (the issuer-relative form MCP clients try) and
    /// <c>/api/.well-known/oauth-authorization-server</c>.
    /// </summary>
    public static class McpRoutes
    {
        public const string RoutePrefix = "api";

        public const string Mcp = "mcp";

        public const string ProtectedResourceMetadata = ".well-known/oauth-protected-resource";

        public const string OAuthAuthorizationServerMetadata = McpOAuthPaths.AuthorizationServerMetadata;

        public const string OAuthOpenIdConfiguration = McpOAuthPaths.OpenIdConfiguration;

        public const string OAuthRegister = McpOAuthPaths.Register;

        public const string OAuthAuthorize = McpOAuthPaths.Authorize;

        public const string OAuthCallback = McpOAuthPaths.Callback;

        public const string OAuthToken = McpOAuthPaths.Token;

        public const string McpFunctionName = "SafeExchange-Mcp";

        public const string ProtectedResourceMetadataFunctionName = "SafeExchange-OAuthProtectedResource";

        public const string OAuthAuthorizationServerMetadataFunctionName = "SafeExchange-OAuthAuthorizationServerMetadata";

        public const string OAuthOpenIdConfigurationFunctionName = "SafeExchange-OAuthOpenIdConfiguration";

        public const string OAuthRegisterFunctionName = "SafeExchange-OAuthRegister";

        public const string OAuthAuthorizeFunctionName = "SafeExchange-OAuthAuthorize";

        public const string OAuthCallbackFunctionName = "SafeExchange-OAuthCallback";

        public const string OAuthTokenFunctionName = "SafeExchange-OAuthToken";

        public const string ProtectedResourceMetadataPath = "/" + RoutePrefix + "/" + ProtectedResourceMetadata;

        /// <summary>
        /// The functions the authentication middleware skips, matched by exact (ordinal) name.
        /// </summary>
        public static IReadOnlySet<string> AnonymousFunctionNames { get; } = new HashSet<string>(StringComparer.Ordinal)
        {
            ProtectedResourceMetadataFunctionName,
            OAuthAuthorizationServerMetadataFunctionName,
            OAuthOpenIdConfigurationFunctionName,
            OAuthRegisterFunctionName,
            OAuthAuthorizeFunctionName,
            OAuthCallbackFunctionName,
            OAuthTokenFunctionName,
        };
    }
}
