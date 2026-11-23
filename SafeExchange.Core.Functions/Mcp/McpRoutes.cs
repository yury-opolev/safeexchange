/// <summary>
/// McpRoutes
/// </summary>

namespace SafeExchange.Core.Functions.Mcp
{
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Routes and function names of the MCP triggers. The route constants are relative to the default <c>api</c>
    /// route prefix (<see cref="RoutePrefix"/>), so the MCP endpoint lives at <c>/api/v1/mcp</c> and the OAuth broker
    /// endpoints at <c>/api/oauth/*</c>. The broker's issuer is <c>https://host/api</c> and its metadata is served
    /// only at the host root (below), in the RFC 8414 path-insertion form; there are no <c>/api/.well-known/*</c> routes.
    /// An app that sets <c>extensions.http.routePrefix</c> to <c>""</c> (as SafeExchange.Functions does) prefixes
    /// the relative routes with <see cref="ApiRoutePrefix"/> to keep the public paths. Such an app can also serve the
    /// root documents below: the platform answers 502 to paths outside the route prefix, so the documents are served at
    /// the host root in the forms MCP clients probe (the issuer path or the resource path appended to the
    /// well-known segment, RFC 8414 / RFC 9728), and any other <c>/.well-known/*</c> path answers 404. The
    /// <c>WWW-Authenticate</c> challenge points at the root protected-resource document.
    /// </summary>
    public static class McpRoutes
    {
        public const string RoutePrefix = "api";

        /// <summary>
        /// Prepended to the relative routes by apps with an empty route prefix.
        /// </summary>
        public const string ApiRoutePrefix = RoutePrefix + "/";

        public const string Mcp = McpOAuthPaths.Mcp;

        public const string ProtectedResourceMetadataDocument = ".well-known/oauth-protected-resource";

        public const string OAuthRegister = McpOAuthPaths.Register;

        public const string OAuthAuthorize = McpOAuthPaths.Authorize;

        public const string OAuthCallback = McpOAuthPaths.Callback;

        public const string OAuthToken = McpOAuthPaths.Token;

        // The root routes below are for apps with an empty route prefix.
        public const string WellKnownAuthorizationServerApi = McpOAuthPaths.AuthorizationServerMetadata + "/" + RoutePrefix;

        public const string WellKnownOpenIdConfigurationApi = McpOAuthPaths.OpenIdConfiguration + "/" + RoutePrefix;

        public const string WellKnownProtectedResourceApiMcp = ProtectedResourceMetadataDocument + "/" + ApiRoutePrefix + Mcp;

        // The Functions host tries the routes in function-name order and takes the first match (it does not prefer
        // literal routes), so the catch-all itself excludes the three root documents above (with an optional trailing slash).
        public const string WellKnownNotFound = ".well-known/{*rest:regex(^(?!(oauth-authorization-server/api|openid-configuration/api|oauth-protected-resource/api/v1/mcp)/?$).*$)}";
        public const string McpFunctionName = "SafeExchange-Mcp";

        public const string OAuthRegisterFunctionName = "SafeExchange-OAuthRegister";

        public const string OAuthAuthorizeFunctionName = "SafeExchange-OAuthAuthorize";

        public const string OAuthCallbackFunctionName = "SafeExchange-OAuthCallback";

        public const string OAuthTokenFunctionName = "SafeExchange-OAuthToken";

        public const string WellKnownAuthorizationServerApiFunctionName = "SafeExchange-WellKnownAuthorizationServerApi";

        public const string WellKnownOpenIdConfigurationApiFunctionName = "SafeExchange-WellKnownOpenIdConfigurationApi";

        public const string WellKnownProtectedResourceApiMcpFunctionName = "SafeExchange-WellKnownProtectedResourceApiMcp";

        public const string WellKnownNotFoundFunctionName = "SafeExchange-WellKnownNotFound";

        /// <summary>
        /// The path of the protected-resource metadata the <c>WWW-Authenticate</c> challenge points to (RFC 9728, resource <c>/api/v1/mcp</c>).
        /// </summary>
        public const string ProtectedResourceMetadataPath = "/" + WellKnownProtectedResourceApiMcp;

        /// <summary>
        /// The functions the authentication middleware skips, matched by exact (ordinal) name.
        /// </summary>
        public static IReadOnlySet<string> AnonymousFunctionNames { get; } = new HashSet<string>(StringComparer.Ordinal)
        {
            OAuthRegisterFunctionName,
            OAuthAuthorizeFunctionName,
            OAuthCallbackFunctionName,
            OAuthTokenFunctionName,
            WellKnownAuthorizationServerApiFunctionName,
            WellKnownOpenIdConfigurationApiFunctionName,
            WellKnownProtectedResourceApiMcpFunctionName,
            WellKnownNotFoundFunctionName,
        };
    }
}
