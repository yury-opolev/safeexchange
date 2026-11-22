/// <summary>
/// McpOAuthOptions
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    /// <summary>
    /// Binds to the <c>Mcp:OAuth</c> configuration section (Key Vault <c>Mcp--OAuth--ClientId</c>,
    /// <c>Mcp--OAuth--StateKey</c>). The OAuth broker for MCP clients is configured only when <see cref="ClientId"/> is
    /// set and <see cref="StateKey"/> is a valid key (<see cref="McpOAuthStateKey"/>).
    /// </summary>
    public sealed class McpOAuthOptions
    {
        public const string SectionName = "Mcp:OAuth";

        /// <summary>
        /// Application (client) ID of the Entra public client app the broker signs users in with.
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// Base64 HMAC key (at least 32 bytes) that signs the consent token and the state sent to Entra.
        /// </summary>
        public string StateKey { get; set; } = string.Empty;

        public McpOAuthRateLimitOptions RateLimit { get; set; } = new();
    }
}
