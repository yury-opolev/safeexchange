/// <summary>
/// AuthorizationServerMetadata
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json.Nodes;

    /// <summary>
    /// RFC 8414 authorization-server metadata of the broker: issuer <c>https://host/api</c>, its four endpoints,
    /// authorization code with S256 PKCE for public clients only.
    /// </summary>
    public static class AuthorizationServerMetadata
    {
        public static string ToJson(Uri apiBaseUri, IEnumerable<string> scopes)
        {
            ArgumentNullException.ThrowIfNull(apiBaseUri);
            ArgumentNullException.ThrowIfNull(scopes);

            return new JsonObject
            {
                ["issuer"] = McpOAuthPaths.Issuer(apiBaseUri),
                ["authorization_endpoint"] = McpOAuthPaths.Endpoint(apiBaseUri, McpOAuthPaths.Authorize),
                ["token_endpoint"] = McpOAuthPaths.Endpoint(apiBaseUri, McpOAuthPaths.Token),
                ["registration_endpoint"] = McpOAuthPaths.Endpoint(apiBaseUri, McpOAuthPaths.Register),
                ["response_types_supported"] = new JsonArray("code"),
                ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"),
                ["code_challenge_methods_supported"] = new JsonArray("S256"),
                ["token_endpoint_auth_methods_supported"] = new JsonArray("none"),
                ["scopes_supported"] = new JsonArray(scopes.Select(s => (JsonNode?)s).ToArray()),
            }.ToJsonString();
        }
    }
}
