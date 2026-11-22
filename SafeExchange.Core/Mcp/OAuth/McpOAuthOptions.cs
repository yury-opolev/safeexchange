/// <summary>
/// McpOAuthOptions
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    /// <summary>
    /// Binds to the <c>Mcp:OAuth</c> configuration section (Key Vault <c>Mcp--OAuth--ClientId</c>). The OAuth broker
    /// for MCP clients is configured only when <see cref="ClientId"/> is set.
    /// </summary>
    public sealed class McpOAuthOptions
    {
        public const string SectionName = "Mcp:OAuth";

        /// <summary>
        /// Application (client) ID of the Entra public client app the broker signs users in with.
        /// </summary>
        public string ClientId { get; set; } = string.Empty;
    }
}
