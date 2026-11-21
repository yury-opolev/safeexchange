/// <summary>
/// McpRoutes
/// </summary>

namespace SafeExchange.Core.Functions.Mcp
{
    /// <summary>
    /// Routes and function names of the MCP triggers. Azure Functions serves HTTP triggers under the
    /// <c>api</c> route prefix (host.json sets no other), so the metadata document lives at
    /// <c>/api/.well-known/oauth-protected-resource</c>; the <c>WWW-Authenticate</c> challenge points there.
    /// </summary>
    public static class McpRoutes
    {
        public const string RoutePrefix = "api";

        public const string Mcp = "mcp";

        public const string ProtectedResourceMetadata = ".well-known/oauth-protected-resource";

        public const string McpFunctionName = "SafeExchange-Mcp";

        public const string ProtectedResourceMetadataFunctionName = "SafeExchange-OAuthProtectedResource";

        public const string ProtectedResourceMetadataPath = "/" + RoutePrefix + "/" + ProtectedResourceMetadata;
    }
}
